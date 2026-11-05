using System.Buffers.Binary;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DosboxEnhanced;

public static class CheatValue
{
    public static readonly string[] Types=["u8","i8","u16","i16","u32","i32","u64","i64","f32","f64"];
    public static int Size(string type) => type switch { "u8" or "i8"=>1,"u16" or "i16"=>2,"u32" or "i32" or "f32"=>4,"u64" or "i64" or "f64"=>8,_=>throw new ArgumentException("Choose u8/i8/u16/i16/u32/i32/u64/i64/f32/f64.") };
    public static bool Floating(string type)=>type.StartsWith('f');
    public static decimal Integer(ReadOnlySpan<byte> b,string type)=>type switch { "u8"=>b[0],"i8"=>(sbyte)b[0],"u16"=>BinaryPrimitives.ReadUInt16LittleEndian(b),"i16"=>BinaryPrimitives.ReadInt16LittleEndian(b),"u32"=>BinaryPrimitives.ReadUInt32LittleEndian(b),"i32"=>BinaryPrimitives.ReadInt32LittleEndian(b),"u64"=>BinaryPrimitives.ReadUInt64LittleEndian(b),"i64"=>BinaryPrimitives.ReadInt64LittleEndian(b),_=>throw new ArgumentException("Not an integer type.") };
    public static double Real(ReadOnlySpan<byte> b,string type)=>type=="f32"?BinaryPrimitives.ReadSingleLittleEndian(b):BinaryPrimitives.ReadDoubleLittleEndian(b);
    public static string Format(ReadOnlySpan<byte> b,string type)=>Floating(type)?Real(b,type).ToString("R",CultureInfo.InvariantCulture):Integer(b,type).ToString(CultureInfo.InvariantCulture);
    public static byte[] Parse(string text,string type)
    {
        int size=Size(type);text=text.Trim();if(text.Length>128)throw new ArgumentException("Value is too long.");var bytes=new byte[size];
        if(Floating(type)) {
            if(!double.TryParse(text,NumberStyles.Float,CultureInfo.InvariantCulture,out var value)||!double.IsFinite(value)||type=="f32"&&!float.IsFinite((float)value))throw new ArgumentException("Enter a finite number using a decimal point.");
            if(type=="f32")BinaryPrimitives.WriteSingleLittleEndian(bytes,(float)value);else BinaryPrimitives.WriteDoubleLittleEndian(bytes,value);return bytes;
        }
        bool hex=text.StartsWith("0x",StringComparison.OrdinalIgnoreCase);BigInteger number;
        bool valid=hex?BigInteger.TryParse("0"+text[2..],NumberStyles.AllowHexSpecifier,CultureInfo.InvariantCulture,out number):BigInteger.TryParse(text,NumberStyles.Integer,CultureInfo.InvariantCulture,out number);
        int bits=size*8;BigInteger min=!hex&&type.StartsWith('i')?-(BigInteger.One<<(bits-1)):BigInteger.Zero;
        BigInteger max=!hex&&type.StartsWith('i')?(BigInteger.One<<(bits-1))-1:(BigInteger.One<<bits)-1;
        if(!valid||hex&&text.Length==2||number<min||number>max)throw new ArgumentException("Value is outside the range for "+type+".");
        if(number<0)number+=BigInteger.One<<bits;
        for(int i=0;i<size;i++){bytes[i]=(byte)(number&255);number>>=8;}return bytes;
    }
    public static long Address(string text) { text=text.Trim();bool hex=text.StartsWith("0x",StringComparison.OrdinalIgnoreCase);if(!long.TryParse(hex?text[2..]:text,hex?NumberStyles.HexNumber:NumberStyles.Integer,CultureInfo.InvariantCulture,out long value)||value<0||value>int.MaxValue)throw new ArgumentException("Enter a DOS mapped address, e.g. 0x1234 or 4660.");return value; }
}

public sealed record CheatResult(long Address,string Type,string Value,string Previous,string Region) { public string AddressText=>"0x"+Address.ToString("X8"); }
public sealed class CheatSearch
{
    public static readonly string[] Filters=["exact","not_equal","greater","less","between","unknown","changed","unchanged","increased","decreased","increased_by","decreased_by"];
    sealed record Candidates(RamRegion Region,byte[] Previous,BitArray Matches,byte[]? Prior);
    Candidates[] blocks=[];
    public long Revision { get; private set; }
    public string Type { get; private set; }="u16";
    public bool Aligned { get; private set; }
    public int? Region { get; private set; }
    public int Count { get; private set; }
    public bool Started { get; private set; }
    public int ScanNumber { get; private set; }
    public static bool Relative(string filter)=>filter is "changed" or "unchanged" or "increased" or "decreased" or "increased_by" or "decreased_by";
    public void Scan(RamSnapshot snapshot,bool first,string type,string filter,string value,string upper,bool aligned,int? region,CancellationToken token)
    {
        int size=CheatValue.Size(type);if(!Filters.Contains(filter))throw new ArgumentException("Unknown scan filter.");
        if(first&&Relative(filter))throw new ArgumentException("Start with an exact, unknown, greater, less or between search.");
        if(!first&&(!Started||snapshot.Revision!=Revision||type!=Type||aligned!=Aligned||region!=Region))throw new InvalidOperationException("Start a new search after changing the memory layout, type or region.");
        if(!first&&filter=="unknown")throw new ArgumentException("Unknown initial value is only for a first scan.");
        byte[]? target=filter is "unknown" or "changed" or "unchanged" or "increased" or "decreased"?null:CheatValue.Parse(value,type);
        byte[]? high=filter=="between"?CheatValue.Parse(upper,type):null;
        if(high!=null&&(CheatValue.Floating(type)?CheatValue.Real(target!,type)>CheatValue.Real(high,type):CheatValue.Integer(target!,type)>CheatValue.Integer(high,type)))throw new ArgumentException("The lower value must not exceed the upper value.");
        var next=new List<Candidates>();int count=0;int step=aligned?size:1;
        foreach(var block in snapshot.Blocks) {
            var old=first?null:blocks.SingleOrDefault(b=>b.Region==block.Region)??throw new InvalidOperationException("DOS RAM map changed; start a new search.");
            var matches=new BitArray(block.Bytes.Length);int firstOffset=aligned?(int)((size-block.Region.Start%size)%size):0;
            int visited=0;
            for(int offset=firstOffset;offset<=block.Bytes.Length-size;offset+=step) {
                if((visited++&0x3fff)==0)token.ThrowIfCancellationRequested();if(old!=null&&!old.Matches[offset])continue;
                if(Match(block.Bytes.AsSpan(offset,size),old==null?ReadOnlySpan<byte>.Empty:old.Previous.AsSpan(offset,size),type,filter,target,high)) { matches[offset]=true;count++; }
            }
            next.Add(new(block.Region,block.Bytes,matches,old?.Previous));
        }
        token.ThrowIfCancellationRequested();blocks=next.ToArray();Revision=snapshot.Revision;Type=type;Aligned=aligned;Region=region;Count=count;Started=true;ScanNumber++;
    }
    static bool Match(ReadOnlySpan<byte> current,ReadOnlySpan<byte> previous,string type,string filter,byte[]? target,byte[]? upper)
    {
        if(filter=="unknown")return true;
        if(CheatValue.Floating(type)) {
            double a=CheatValue.Real(current,type),b=previous.IsEmpty?0:CheatValue.Real(previous,type),v=target==null?0:CheatValue.Real(target,type),hi=upper==null?0:CheatValue.Real(upper,type);
            if(!double.IsFinite(a)||Relative(filter)&&!double.IsFinite(b))return false;
            return filter switch { "exact"=>a==v,"not_equal"=>a!=v,"greater"=>a>v,"less"=>a<v,"between"=>a>=v&&a<=hi,"changed"=>a!=b,"unchanged"=>a==b,"increased"=>a>b,"decreased"=>a<b,"increased_by"=>a-b==v,"decreased_by"=>b-a==v,_=>false };
        }
        decimal x=CheatValue.Integer(current,type),y=previous.IsEmpty?0:CheatValue.Integer(previous,type),n=target==null?0:CheatValue.Integer(target,type),h=upper==null?0:CheatValue.Integer(upper,type);
        return filter switch { "exact"=>x==n,"not_equal"=>x!=n,"greater"=>x>n,"less"=>x<n,"between"=>x>=n&&x<=h,"changed"=>x!=y,"unchanged"=>x==y,"increased"=>x>y,"decreased"=>x<y,"increased_by"=>x-y==n,"decreased_by"=>y-x==n,_=>false };
    }
    public CheatResult[] Results(int offset,int count)
    {
        if(offset<0||count is <1 or >500)throw new ArgumentException("Request 1–500 results with a nonnegative offset.");
        var result=new List<CheatResult>();int index=0,size=CheatValue.Size(Type);
        foreach(var block in blocks)for(int i=0;i<=block.Previous.Length-size;i++)if(block.Matches[i]) {
            if(index++<offset)continue;result.Add(new(block.Region.Start+i,Type,CheatValue.Format(block.Previous.AsSpan(i,size),Type),block.Prior==null?"—":CheatValue.Format(block.Prior.AsSpan(i,size),Type),block.Region.Name));if(result.Count==count)return result.ToArray();
        }
        return result.ToArray();
    }
}

public sealed class CheatWatch
{
    public string Id { get; set; }=Guid.NewGuid().ToString("N");
    public string Name { get; set; }="Value";
    public long Address { get; set; }
    public string Type { get; set; }="u16";
    public string Target { get; set; }="0";
    public PointerPath? Pointer { get; set; }
    [JsonIgnore] public bool Frozen { get; set; }
    [JsonIgnore] public string Current { get; set; }="—";
    [JsonIgnore] public string AddressText => "0x"+Address.ToString("X8");
    [JsonIgnore] public string PointerText=>Pointer==null?"Fixed address":Pointer.Mode+" · "+Pointer.Description;
    [JsonIgnore] public string State => Frozen?"Locked":Current=="Unavailable"?"Unavailable":Pointer!=null?"Pointer":"Watching";
}

public sealed partial class CheatSession
{
    public readonly DosCore Core;
    readonly string file;
    public CheatSearch Search { get; private set; }=new();
    public List<CheatWatch> Watches { get; }
    public long Revision { get; private set; }
    public readonly SemaphoreSlim Gate=new(1);
    public CheatSession(DosCore core,string key) {
        Core=core;Revision=core.MemoryRevision;file=Path.Combine(Paths.Data,"cheats",Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))+".json");
        Watches=File.Exists(file)?JsonSerializer.Deserialize<List<CheatWatch>>(File.ReadAllText(file))??[]:[];
        if(Watches.Count>256||Watches.Select(w=>w.Id).Distinct().Count()!=Watches.Count)throw new ArgumentException("Invalid saved cheat list.");
        foreach(var w in Watches){Validate(w);w.Frozen=false;}
        LoadPointers();
    }
    public void Sync() { if(Revision!=Core.MemoryRevision){Revision=Core.MemoryRevision;Search=new();foreach(var w in Watches){w.Frozen=false;w.Current="—";}} }
    static void Validate(CheatWatch w) { if(w.Id.Length is <1 or >64 || w.Name.Length>120 || w.Address<0||w.Address>int.MaxValue)throw new ArgumentException("Invalid watch.");CheatValue.Parse(w.Target,w.Type);w.Pointer?.Validate(); }
    public void Save()=>Paths.AtomicWrite(file,JsonSerializer.Serialize(Watches,Paths.Json));
    public async Task Scan(bool first,string type,string filter,string value,string upper,bool aligned,int? region,CancellationToken token) {
        await Gate.WaitAsync(token);try { Sync();var snapshot=await Core.SnapshotRam(first?region:Search.Region,token);
            await Task.Run(()=>Search.Scan(snapshot,first,first?type:Search.Type,filter,value,upper,first?aligned:Search.Aligned,first?region:Search.Region,token),token);
            if(Core.MemoryRevision!=snapshot.Revision){Sync();throw new InvalidOperationException("DOS program changed during the scan; start again.");}
        } finally { Gate.Release(); }
    }
    public async Task Refresh(CancellationToken token) {
        Sync();var readings=await Core.ReadWatchValues(Watches.Select(w=>new DosCore.WatchRequest(w.Id,w.Address,CheatValue.Size(w.Type),w.Pointer?.Copy())).ToArray(),Revision,token);
        foreach(var reading in readings){var w=Find(reading.Id);w.Frozen=reading.Frozen;if(reading.Address is long address)w.Address=address;w.Current=reading.Bytes==null?"Unavailable":CheatValue.Format(reading.Bytes,w.Type);}
    }
    public async Task<CheatWatch> Add(string name,long address,string type,CancellationToken token) {
        Sync();if(Watches.Count>=256)throw new ArgumentException("Keep at most 256 watches per game.");
        var data=await Core.ReadCheatValues([(address,CheatValue.Size(type))],Revision,token);
        var watch=new CheatWatch{Name=string.IsNullOrWhiteSpace(name)?"Value":name,Address=address,Type=type,Target=CheatValue.Format(data[0],type),Current=CheatValue.Format(data[0],type)};Validate(watch);Watches.Add(watch);Save();return watch;
    }
    public CheatWatch Find(string id)=>Watches.FirstOrDefault(w=>w.Id==id)??throw new ArgumentException("Unknown watch ID.");
    public async Task Update(string id,string? name,string? target,bool? freeze,bool write,CancellationToken token) {
        Sync();var w=Find(id);var candidate=new CheatWatch{Id=w.Id,Name=name??w.Name,Address=w.Address,Type=w.Type,Target=target??w.Target,Pointer=w.Pointer};Validate(candidate);
        bool locked=freeze??w.Frozen;
        if(locked||write)await Core.SetCheatValue(w.Id,w.Address,CheatValue.Parse(candidate.Target,w.Type),locked,Revision,token,w.Pointer);
        else if(w.Frozen)await Core.UnlockCheat(w.Id,token);
        w.Name=candidate.Name;w.Target=candidate.Target;w.Frozen=locked;Save();
    }
    public async Task Remove(string id,CancellationToken token) { var w=Find(id);await Core.UnlockCheat(id,token);Watches.Remove(w);Save(); }
    public async Task UnlockAll(CancellationToken token) { await Core.UnlockCheat(null,token);foreach(var w in Watches)w.Frozen=false; }
    public void ResetSearch() => Search=new();
}
