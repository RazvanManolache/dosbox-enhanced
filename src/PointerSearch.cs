using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json.Serialization;

namespace DosboxEnhanced;

public sealed record PointerPath(long BaseAddress,int[] Offsets,string Mode="physical32",int Segment=0,bool Wrap20=false)
{
    public void Validate() {if(BaseAddress<0||BaseAddress>int.MaxValue||Offsets==null||Offsets.Length is <1 or >6||Offsets.Any(o=>o is < -65536 or >65536)||!PointerMemory.Modes.Contains(Mode)||Segment is <0 or >65535)throw new ArgumentException("Invalid pointer path; use 1–6 offsets within ±65536.");}
    public PointerPath Copy()=>this with{Offsets=(int[])Offsets.Clone()};
    [JsonIgnore] public string Description=>"[0x"+BaseAddress.ToString("X")+"] "+string.Join(" → ",Offsets.Select(o=>(o<0?"-0x":"+0x")+Math.Abs(o).ToString("X")));
}
public static class PointerMemory
{
    public static readonly string[] Modes=["physical32","mapped32","far16","near16"];
    public static int Width(string mode)=>mode switch{"near16"=>2,"physical32" or "mapped32" or "far16"=>4,_=>throw new ArgumentException("Unknown pointer mode.")};
    public static long Coordinate(long mapped,string mode,RamRegion[] regions) {
        var region=regions.FirstOrDefault(r=>mapped>=r.Start&&mapped<r.Start+r.Length)??throw new ArgumentException("Pointer target is outside mapped DOS RAM.");
        return mode=="mapped32"?mapped:(region.PhysicalStart??throw new ArgumentException("This core has no verified physical-address translation; use mapped32 only for pointers stored in mapped coordinates."))+mapped-region.Start;
    }
    public static long Mapped(long coordinate,string mode,RamRegion[] regions) {
        if(mode=="mapped32"){Coordinate(coordinate,mode,regions);return coordinate;}
        var region=regions.FirstOrDefault(r=>r.PhysicalStart is long start&&coordinate>=start&&coordinate<start+r.Length)??throw new ArgumentException("Pointer resolves outside mapped DOS RAM.");
        return region.Start+coordinate-region.PhysicalStart!.Value;
    }
    public static long Decode(ReadOnlySpan<byte> bytes,PointerPath path) {
        long value=path.Mode switch {"mapped32" or "physical32"=>BinaryPrimitives.ReadUInt32LittleEndian(bytes),"far16"=>(long)BinaryPrimitives.ReadUInt16LittleEndian(bytes[2..])*16+BinaryPrimitives.ReadUInt16LittleEndian(bytes),"near16"=>(long)path.Segment*16+BinaryPrimitives.ReadUInt16LittleEndian(bytes),_=>throw new ArgumentException("Unknown pointer mode.")};
        return path.Wrap20&&path.Mode!="mapped32"?value&0xfffff:value;
    }
    public static long Resolve(PointerPath path,RamRegion[] regions,Func<long,int,byte[]> read) {
        path.Validate();long address=path.BaseAddress;var visited=new HashSet<long>();
        foreach(int offset in path.Offsets){if(!visited.Add(address))throw new ArgumentException("Pointer chain contains a cycle.");var bytes=read(address,Width(path.Mode));if(bytes.All(b=>b==0)&&(path.Mode!="near16"||path.Segment==0))throw new ArgumentException("Pointer is null.");long coordinate=checked(Decode(bytes,path)+offset);if(path.Wrap20&&path.Mode!="mapped32")coordinate&=0xfffff;address=Mapped(coordinate,path.Mode,regions);if(visited.Contains(address))throw new ArgumentException("Pointer chain contains a cycle.");}
        return address;
    }
    public static byte[] Read(RamSnapshot snapshot,long address,int count) {var block=snapshot.Blocks.FirstOrDefault(b=>address>=b.Region.Start&&address<=b.Region.Start+b.Bytes.Length-count)??throw new ArgumentException("Pointer slot is outside the captured RAM.");return block.Bytes.AsSpan(checked((int)(address-block.Region.Start)),count).ToArray();}
}
public sealed record PointerOptions(string Mode="physical32",int Depth=3,int MaxOffset=256,bool NegativeOffsets=false,bool Aligned=true,int? Region=0,int Segment=0,bool Wrap20=false,int MaxResults=1000)
{
    public void Validate(){if(!PointerMemory.Modes.Contains(Mode)||Depth is <1 or >6||MaxOffset is <0 or >65536||Segment is <0 or >65535||MaxResults is <1 or >1000||Region is <0 or >31)throw new ArgumentException("Pointer scan: depth 1–6, offset 0–65536, results 1–1000.");}
}
public sealed record PointerCandidate(PointerPath Path,long Target) {
    [JsonIgnore] public string BaseText=>"0x"+Path.BaseAddress.ToString("X8");
    [JsonIgnore] public string OffsetsText=>string.Join(" → ",Path.Offsets.Select(o=>(o<0?"-0x":"+0x")+Math.Abs(o).ToString("X")));
    [JsonIgnore] public int Depth=>Path.Offsets.Length;
    [JsonIgnore] public string TargetText=>"0x"+Target.ToString("X8");
}
public sealed record PointerScanState(PointerOptions Options,long Target,PointerCandidate[] Candidates,bool Limited=false,string LimitReason="");
public static class PointerSearch
{
    readonly record struct Slot(long Value,long Address);
    sealed record Node(long Target,int[] Offsets,long[] Slots);
    public static PointerScanState Scan(RamSnapshot snapshot,long target,PointerOptions options,CancellationToken token)
    {
        options.Validate();var regions=snapshot.AddressMap;PointerMemory.Coordinate(target,options.Mode,regions);
        if(snapshot.Blocks.Sum(b=>(long)b.Bytes.Length)>64L*1024*1024)throw new ArgumentException("Scan at most 64 MiB.");
        var prototype=new PointerPath(0,[0],options.Mode,options.Segment,options.Wrap20);int width=PointerMemory.Width(options.Mode),step=options.Aligned?width:1;
        var slots=new List<Slot>();var timer=Stopwatch.StartNew();bool limited=false;string reason="";
        foreach(var block in snapshot.Blocks.Where(b=>options.Region==null||b.Region.Index==options.Region)){int begin=options.Aligned?(int)((width-block.Region.Start%width)%width):0;int visited=0;
            for(int i=begin;i<=block.Bytes.Length-width;i+=step){if((visited++&0x3fff)==0){token.ThrowIfCancellationRequested();if(timer.ElapsedMilliseconds>8000){limited=true;reason="Time budget";break;}}
                var bytes=block.Bytes.AsSpan(i,width);if(bytes.IndexOfAnyExcept((byte)0)<0&&(options.Mode!="near16"||options.Segment==0))continue;long value=PointerMemory.Decode(bytes,prototype);
                bool plausible=regions.Any(r=>{long? start=options.Mode=="mapped32"?r.Start:r.PhysicalStart;if(!start.HasValue)return false;return (options.Wrap20&&options.Mode!="mapped32"?new[]{value,value+0x100000,value-0x100000}:new[]{value}).Any(v=>v>=start.Value-options.MaxOffset&&v<start.Value+r.Length+(options.NegativeOffsets?options.MaxOffset:0));});
                if(!plausible)continue;slots.Add(new(value,block.Region.Start+i));if(slots.Count>=1000000){limited=true;reason="Pointer index limit";break;}
            }if(limited)break;
        }
        var ordered=slots.ToArray();Array.Sort(ordered,(a,b)=>a.Value!=b.Value?a.Value.CompareTo(b.Value):a.Address.CompareTo(b.Address));
        int Lower(long value){int lo=0,hi=ordered.Length;while(lo<hi){int mid=(lo+hi)/2;if(ordered[mid].Value<value)lo=mid+1;else hi=mid;}return lo;}
        var result=new List<PointerCandidate>();var frontier=new List<Node>{new(target,[],[])};var keys=new HashSet<string>();int explored=0;
        for(int depth=1;depth<=options.Depth&&frontier.Count>0;depth++) {
            var next=new List<Node>();
            foreach(var node in frontier){token.ThrowIfCancellationRequested();if(++explored>100000||timer.ElapsedMilliseconds>8000){limited=true;reason="Search budget";goto Done;}
                long goal=PointerMemory.Coordinate(node.Target,options.Mode,regions);
                var goals=options.Wrap20&&options.Mode!="mapped32"?new[]{goal,goal+0x100000,goal-0x100000}:new[]{goal};
                foreach(long destination in goals){int matches=0;for(int i=Lower(destination-options.MaxOffset);i<ordered.Length&&ordered[i].Value<=destination+(options.NegativeOffsets?options.MaxOffset:0);i++){
                    if((matches++&0x3ff)==0)token.ThrowIfCancellationRequested();var slot=ordered[i];if(node.Slots.Contains(slot.Address)||slot.Address==node.Target||slot.Address==target)continue;
                    int offset=checked((int)(destination-slot.Value));var offsets=new[]{offset}.Concat(node.Offsets).ToArray();string key=slot.Address+":"+string.Join(',',offsets);if(!keys.Add(key))continue;
                    var path=new PointerPath(slot.Address,offsets,options.Mode,options.Segment,options.Wrap20);result.Add(new(path,target));
                    if(result.Count>=options.MaxResults){limited=true;reason="Result limit";goto Done;}
                    next.Add(new(slot.Address,offsets,new[]{slot.Address}.Concat(node.Slots).ToArray()));
                }}
            }frontier=next;
        }
        Done:token.ThrowIfCancellationRequested();return new(options,target,result.ToArray(),limited,reason);
    }
    public static PointerScanState Rescan(PointerScanState previous,RamSnapshot snapshot,long target,CancellationToken token) {
        PointerMemory.Coordinate(target,previous.Options.Mode,snapshot.AddressMap);var results=new List<PointerCandidate>();
        foreach(var candidate in previous.Candidates){token.ThrowIfCancellationRequested();try{if(PointerMemory.Resolve(candidate.Path,snapshot.AddressMap,(a,c)=>PointerMemory.Read(snapshot,a,c))==target)results.Add(candidate with{Target=target});}catch(ArgumentException){}}
        return new(previous.Options,target,results.ToArray(),previous.Limited,previous.LimitReason);
    }
}
