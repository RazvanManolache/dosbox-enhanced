using System.IO;
using System.Text.Json;

namespace DosboxEnhanced;

public sealed partial class CheatSession
{
    public PointerScanState? Pointers { get; private set; }
    string PointerFile=>Path.ChangeExtension(file,"pointers.json");
    void LoadPointers() {
        if(!File.Exists(PointerFile))return;
        var state=JsonSerializer.Deserialize<PointerScanState>(File.ReadAllText(PointerFile));
        if(state==null)return;state.Options.Validate();
        if(state.Candidates.Length>1000)throw new ArgumentException("Invalid saved pointer results.");
        foreach(var c in state.Candidates)c.Path.Validate();Pointers=state;
    }
    public void ClearPointers(){Pointers=null;SavePointers();}
    void SavePointers()=>Paths.AtomicWrite(PointerFile,JsonSerializer.Serialize(Pointers,Paths.Json));
    public async Task ScanPointers(long target,PointerOptions options,bool rescan,CancellationToken token) {
        await Gate.WaitAsync(token);try {
            Sync();options.Validate();var previous=Pointers;
            if(rescan&&previous==null)throw new ArgumentException("Run a pointer scan first.");
            // A rescan can follow chains into any RAM region after the allocation moves.
            var snapshot=await Core.SnapshotRam(rescan?null:options.Region,token);
            var result=await Task.Run(()=>rescan?PointerSearch.Rescan(previous!,snapshot,target,token):PointerSearch.Scan(snapshot,target,options,token),token);
            if(Core.MemoryRevision!=snapshot.Revision){Sync();throw new InvalidOperationException("DOS memory changed during the pointer scan; try again.");}
            Pointers=result;SavePointers();
        }finally{Gate.Release();}
    }
    public async Task<CheatWatch> AddPointer(int index,string name,string type,CancellationToken token) {
        Sync();if(Pointers==null||index<0||index>=Pointers.Candidates.Length)throw new ArgumentException("Choose a pointer result.");
        if(Watches.Count>=256)throw new ArgumentException("Keep at most 256 watches.");
        var path=Pointers.Candidates[index].Path.Copy();string id=Guid.NewGuid().ToString("N");
        var reading=(await Core.ReadWatchValues([new(id,0,CheatValue.Size(type),path)],Revision,token))[0];
        if(reading.Bytes==null)throw new ArgumentException(reading.Error??"Pointer is unavailable. Rescan it.");
        var watch=new CheatWatch{Id=id,Name=string.IsNullOrWhiteSpace(name)?"Pointer value":name,Address=reading.Address!.Value,Type=type,Pointer=path,Target=CheatValue.Format(reading.Bytes,type),Current=CheatValue.Format(reading.Bytes,type)};
        Validate(watch);Watches.Add(watch);Save();return watch;
    }
}
