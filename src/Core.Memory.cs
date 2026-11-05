using System.Runtime.InteropServices;

namespace DosboxEnhanced;

public sealed record RamRegion(int Index, long Start, int Length, bool Writable, string Name, long? PhysicalStart=null);
public sealed record RamBlock(RamRegion Region, byte[] Bytes);
public sealed record RamSnapshot(long Revision, RamBlock[] Blocks, RamRegion[]? Regions=null) { public RamRegion[] AddressMap=>Regions??Blocks.Select(b=>b.Region).ToArray(); }
public sealed partial class DosCore
{
    [StructLayout(LayoutKind.Sequential)] struct RetroMap { public IntPtr Descriptors; public uint Count; }
    [StructLayout(LayoutKind.Sequential)] struct RetroDescriptor {
        public ulong Flags; public IntPtr Pointer; public nuint Offset, Start, Select, Disconnect, Length; public IntPtr AddressSpace;
    }
    sealed record Mapping(RamRegion Info, IntPtr Pointer);
    Mapping[] memoryMaps = [];
    long rawMemoryBytes, memoryRevision;
    sealed record FrozenValue(long Address,byte[] Bytes,PointerPath? Pointer);
    readonly Dictionary<string,FrozenValue> freezes = new();
    int activeCheatLocks;
    public int ActiveCheatLocks=>Volatile.Read(ref activeCheatLocks);
    void RemoveFreeze(string id){freezes.Remove(id);Volatile.Write(ref activeCheatLocks,freezes.Count);}
    public long MemoryRevision => Interlocked.Read(ref memoryRevision);
    public bool CanReadTextMemory => rawMemoryBytes >= 0xc0000 && memoryMaps.Length == 0;
    void UpdateMemorySize() => Interlocked.Exchange(ref MemoryBytes, memoryMaps.Length == 0 ? rawMemoryBytes : memoryMaps.Sum(m => (long)m.Info.Length));
    void InvalidateCheatMemory() { freezes.Clear();Volatile.Write(ref activeCheatLocks,0);Interlocked.Increment(ref memoryRevision); }
    void ClearCheatMemory() { InvalidateCheatMemory(); memoryMaps=[]; rawMemoryBytes=0; UpdateMemorySize(); }
    void ReceiveMemoryMap(IntPtr data)
    {
        var map = Marshal.PtrToStructure<RetroMap>(data);
        if (map.Count > 32 || map.Count > 0 && map.Descriptors == IntPtr.Zero) throw new ArgumentException("Invalid DOS RAM map.");
        var mappings = new List<Mapping>(); int stride = Marshal.SizeOf<RetroDescriptor>();
        for(int i=0;i<map.Count;i++) {
            var d=Marshal.PtrToStructure<RetroDescriptor>(map.Descriptors+i*stride);
            // This host supports linear, little-endian system RAM, never register/ROM/aliased maps.
            if(d.Pointer==IntPtr.Zero || (d.Flags&4)==0 || (d.Flags&2)!=0 || d.Select!=0 || d.Disconnect!=0 || d.Length==0 || d.Length>256*1024*1024 ||
                (ulong)d.Start+(ulong)d.Length>2UL*1024*1024*1024 || d.Offset>1024UL*1024*1024 || d.AddressSpace!=IntPtr.Zero && !string.IsNullOrEmpty(Marshal.PtrToStringUTF8(d.AddressSpace))) continue;
            long start=checked((long)d.Start); int length=checked((int)d.Length);
            if(mappings.Any(m => start<m.Info.Start+m.Info.Length && m.Info.Start<start+length)) continue;
            string name=start==0?"Game / conventional RAM":start==0x100000?"DOS / system RAM":start==0x200000?"Extended RAM":"RAM "+i;
            mappings.Add(new(new(mappings.Count,start,length,(d.Flags&1)==0,name),new IntPtr(checked(d.Pointer.ToInt64()+(long)d.Offset))));
        }
        // Infer physical offsets only for the verified DOSBox Pure layout. No native
        // pointers leave this class, and no translation is guessed for other cores.
        if(CoreVersion.Contains("DOSBox",StringComparison.OrdinalIgnoreCase)&&CoreVersion.Contains("Pure",StringComparison.OrdinalIgnoreCase)) {
            var game=mappings.FirstOrDefault(m=>m.Info.Start==0);var dos=mappings.FirstOrDefault(m=>m.Info.Start==0x100000);var extended=mappings.FirstOrDefault(m=>m.Info.Start==0x200000);
            long? physicalBase=dos?.Pointer.ToInt64()??(game?.Info.Length==640*1024?game.Pointer.ToInt64():null);
            if(physicalBase is long origin&&game!=null&&extended!=null&&extended.Pointer.ToInt64()-origin==640*1024&&game.Pointer.ToInt64()-origin+game.Info.Length==640*1024) {
                for(int i=0;i<mappings.Count;i++){long physical=mappings[i].Pointer.ToInt64()-origin;if(physical>=0&&physical<=1024L*1024*1024)mappings[i]=mappings[i] with {Info=mappings[i].Info with {PhysicalStart=physical}};}
            }
        }
        memoryMaps=mappings.ToArray(); InvalidateCheatMemory(); UpdateMemorySize();
    }
    Mapping[] CurrentMappings()
    {
        if(memoryMaps.Length>0)return memoryMaps;
        if(rawMemoryBytes is >0 and <= 1024L*1024*1024) {
            var pointer=Export<MemoryData>("retro_get_memory_data")(2);
            if(pointer!=IntPtr.Zero)return [new(new(0,0,checked((int)rawMemoryBytes),true,"Physical DOS RAM",0),pointer)];
        }
        throw new InvalidOperationException("DOS RAM is not ready. Start a DOS program and wait for its memory map.");
    }
    Mapping ResolveRam(long address,int count,bool write=false)
    {
        if(address<0 || count<1)throw new ArgumentException("Invalid DOS RAM range.");
        var map=CurrentMappings().FirstOrDefault(m => address>=m.Info.Start && address<=m.Info.Start+m.Info.Length-count)
            ?? throw new ArgumentException("Address is outside a mapped DOS RAM region, or crosses its boundary.");
        if(write&&!map.Info.Writable)throw new ArgumentException("This DOS RAM region is read-only.");return map;
    }
    byte[] ReadRam(long address,int count) { var map=ResolveRam(address,count);var bytes=new byte[count];Marshal.Copy(new IntPtr(checked(map.Pointer.ToInt64()+address-map.Info.Start)),bytes,0,count);return bytes; }
    void WriteRam(long address,byte[] value) { var map=ResolveRam(address,value.Length,true);Marshal.Copy(value,0,new IntPtr(checked(map.Pointer.ToInt64()+address-map.Info.Start)),value.Length); }
    void CheckRevision(long revision) { if(revision!=MemoryRevision)throw new InvalidOperationException("DOS memory layout changed. Scan again and re-enable any freezes explicitly."); }
    async Task<T> MemoryCommand<T>(Func<T> action,CancellationToken cancellation)
    {
        using var lifetime=CancellationTokenSource.CreateLinkedTokenSource(cancellation);lifetime.CancelAfter(TimeSpan.FromSeconds(10));var token=lifetime.Token;
        var result=new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if(!ready||stopping)throw new InvalidOperationException("DOS is not running.");
        commands.Enqueue(()=> { try { token.ThrowIfCancellationRequested();if(!ready||stopping)throw new InvalidOperationException("DOS stopped.");result.TrySetResult(action()); } catch(Exception e) { result.TrySetException(e); } });
        return await result.Task.WaitAsync(token);
    }
    public Task<RamRegion[]> GetRamRegions(CancellationToken token) => MemoryCommand(()=>CurrentMappings().Select(m=>m.Info).ToArray(),token);
    public Task<RamSnapshot> SnapshotRam(int? region,CancellationToken token) => MemoryCommand(()=> {
        var all=CurrentMappings();var selected=all.Where(m=>region==null||m.Info.Index==region).ToArray();
        if(selected.Length==0)throw new ArgumentException("Unknown DOS RAM region.");
        if(selected.Sum(m=>(long)m.Info.Length)>64L*1024*1024)throw new ArgumentException("Scan up to 64 MiB at once; choose a smaller RAM region.");
        return new RamSnapshot(MemoryRevision,selected.Select(m=>new RamBlock(m.Info,ReadRam(m.Info.Start,m.Info.Length))).ToArray(),all.Select(m=>m.Info).ToArray());
    },token);
    public Task<byte[][]> ReadCheatValues((long Address,int Size)[] values,long revision,CancellationToken token) => MemoryCommand(()=> {
        CheckRevision(revision);if(values.Length>500)throw new ArgumentException("Read at most 500 watch values.");
        return values.Select(v=>{if(v.Size is <1 or >8)throw new ArgumentException("Invalid cheat value size.");return ReadRam(v.Address,v.Size);}).ToArray();
    },token);
    long ResolvePointer(PointerPath pointer) => PointerMemory.Resolve(pointer,CurrentMappings().Select(m=>m.Info).ToArray(),ReadRam);
    long FrozenAddress(FrozenValue value)=>value.Pointer==null?value.Address:ResolvePointer(value.Pointer);
    public Task SetCheatValue(string id,long address,byte[] bytes,bool freeze,long revision,CancellationToken token,PointerPath? pointer=null) => MemoryCommand(()=> {
        CheckRevision(revision);if(bytes.Length is <1 or >8 || id.Length>64)throw new ArgumentException("Invalid cheat value.");
        pointer?.Validate();if(pointer!=null)address=ResolvePointer(pointer);
        ResolveRam(address,bytes.Length,true);
        foreach(var entry in freezes.ToArray())if(entry.Key!=id){long other;try{other=FrozenAddress(entry.Value);}catch(ArgumentException){RemoveFreeze(entry.Key);continue;}if(address<other+entry.Value.Bytes.Length&&other<address+bytes.Length)throw new ArgumentException("This value overlaps an active freeze. Unlock it first.");}
        if(freeze&&!freezes.ContainsKey(id)&&freezes.Count>=256)throw new ArgumentException("At most 256 freezes are supported.");
        WriteRam(address,bytes);if(freeze)freezes[id]=new(address,(byte[])bytes.Clone(),pointer?.Copy());else freezes.Remove(id);Volatile.Write(ref activeCheatLocks,freezes.Count);
        return true;
    },token);
    public Task UnlockCheat(string? id,CancellationToken token) => MemoryCommand(()=> { if(id==null)freezes.Clear();else freezes.Remove(id);Volatile.Write(ref activeCheatLocks,freezes.Count);return true; },token);
    public sealed record WatchRequest(string Id,long Address,int Size,PointerPath? Pointer);
    public sealed record WatchReading(string Id,long? Address,byte[]? Bytes,bool Frozen,string? Error);
    public Task<WatchReading[]> ReadWatchValues(WatchRequest[] watches,long revision,CancellationToken token)=>MemoryCommand(()=>{
        CheckRevision(revision);if(watches.Length>256)throw new ArgumentException("At most 256 watches.");
        return watches.Select(w=>{try{if(w.Size is <1 or >8)throw new ArgumentException("Invalid value size.");long address=w.Pointer==null?w.Address:ResolvePointer(w.Pointer);return new WatchReading(w.Id,address,ReadRam(address,w.Size),freezes.ContainsKey(w.Id),null);}catch(ArgumentException e){RemoveFreeze(w.Id);return new WatchReading(w.Id,null,null,false,e.Message);}}).ToArray();
    },token);
    void ApplyFrozenValues() {
        var written=new List<(long Address,int Size)>();
        foreach(var entry in freezes.ToArray())try{long address=FrozenAddress(entry.Value);if(written.Any(w=>address<w.Address+w.Size&&w.Address<address+entry.Value.Bytes.Length))throw new ArgumentException("Pointer locks overlap.");WriteRam(address,entry.Value.Bytes);written.Add((address,entry.Value.Bytes.Length));}
            catch(ArgumentException){RemoveFreeze(entry.Key);OnMessage?.Invoke("A cheat lock was disabled because its pointer or RAM target is unavailable or overlaps another lock.");}
    }
}
