using System.Text.Json.Nodes;

namespace DosboxEnhanced;

public sealed partial class MainWindow
{
    internal Task<object> McpCheats(JsonObject args,CancellationToken token)=>CheatOperation<object>(async(s,t)=>{
        await s.Refresh(t);var regions=await s.Core.GetRamRegions(t);
        return new { address_space="DOSBox core mapped RAM, not Windows process addresses. Game/conventional RAM normally starts at zero; these are not real-mode segment:offset addresses.",
            map_revision=s.Revision,regions,scan=new{started=s.Search.Started,type=s.Search.Type,region=s.Search.Region,aligned=s.Search.Aligned,count=s.Search.Count,
                results=s.Search.Results(args["offset"]?.GetValue<int>()??0,args["count"]?.GetValue<int>()??100)},
            pointer_scan=s.Pointers==null?null:new{options=s.Pointers.Options,target=s.Pointers.Target,count=s.Pointers.Candidates.Length,limited=s.Pointers.Limited,limit_reason=s.Pointers.LimitReason,results=s.Pointers.Candidates.Skip(args["offset"]?.GetValue<int>()??0).Take(args["count"]?.GetValue<int>()??100).Select((c,i)=>new{index=i+(args["offset"]?.GetValue<int>()??0),path=c.Path,target=c.Target}).ToArray()},
            watches=s.Watches.Select(w=>new{id=w.Id,name=w.Name,address=w.Address,type=w.Type,target=w.Target,current=w.Current,frozen=w.Frozen,pointer=w.Pointer}).ToArray() };
    },token);
    internal Task<object> McpCheatScan(JsonObject args,CancellationToken token)=>CheatOperation<object>(async(s,t)=>{
        string action=args["action"]?.GetValue<string>()??"new";
        if(action=="reset"){s.ResetSearch();return new{reset=true};}
        if(action is not ("new" or "next"))throw new ArgumentException("Scan action: new, next or reset.");
        if(args["region"]!=null&&args["all_regions"]?.GetValue<bool>()==true)throw new ArgumentException("Choose a region or all_regions.");
        await s.Scan(action=="new",args["type"]?.GetValue<string>()??"u16",args["filter"]?.GetValue<string>()??"exact",args["value"]?.GetValue<string>()??"",args["upper"]?.GetValue<string>()??"",args["aligned"]?.GetValue<bool>()??false,
            args["all_regions"]?.GetValue<bool>()==true?null:args["region"]?.GetValue<int>()??0,t);
        return new { count=s.Search.Count,type=s.Search.Type,revision=s.Search.Revision,results=s.Search.Results(0,100),note="Use dosbox_cheats to page through the results; values are from the last scan." };
    },token);
    internal Task<object> McpCheatWrite(JsonObject args,CancellationToken token)=>CheatOperation<object>(async(s,t)=>{
        s.Sync();long address=args["address"]!.GetValue<int>();string type=args["type"]?.GetValue<string>()??"u16",value=args["value"]!.GetValue<string>();
        await s.Core.SetCheatValue("direct-write",address,CheatValue.Parse(value,type),false,s.Revision,t);return new{written=true,address,type,value};
    },token);
    internal Task<object> McpCheatWatch(JsonObject args,CancellationToken token)=>CheatOperation<object>(async(s,t)=>{
        string action=args["action"]!.GetValue<string>(),id=args["id"]?.GetValue<string>()??"";
        switch(action) {
            case "add_pointer":var pw=await s.AddPointer(args["candidate"]?.GetValue<int>()??throw new ArgumentException("Supply pointer candidate index."),args["name"]?.GetValue<string>()??"Pointer value",args["type"]?.GetValue<string>()??"u16",t);return new{id=pw.Id,address=pw.Address,type=pw.Type,target=pw.Target,pointer=pw.Pointer};
            case "add":var w=await s.Add(args["name"]?.GetValue<string>()??"Value",args["address"]?.GetValue<int>()??throw new ArgumentException("Supply address."),args["type"]?.GetValue<string>()??"u16",t);return new{id=w.Id,address=w.Address,type=w.Type,target=w.Target};
            case "update":await s.Update(id,args["name"]?.GetValue<string>(),args["value"]?.GetValue<string>(),args["frozen"]?.GetValue<bool>(),args["write"]?.GetValue<bool>()??false,t);break;
            case "remove":await s.Remove(id,t);break;
            case "unlock_all":await s.UnlockAll(t);break;
            default:throw new ArgumentException("Watch action: add, add_pointer, update, remove or unlock_all.");
        }
        return new{completed=true,action};
    },token);
    internal Task<object> McpPointerScan(JsonObject args,CancellationToken token)=>CheatOperation<object>(async(s,t)=>{
        string action=args["action"]?.GetValue<string>()??"new";if(action=="clear"){s.ClearPointers();return new{cleared=true};}
        if(action is not ("new" or "rescan"))throw new ArgumentException("Pointer action: new, rescan or clear.");
        if(args["region"]!=null&&args["all_regions"]?.GetValue<bool>()==true)throw new ArgumentException("Choose a region or all_regions.");
        var options=new PointerOptions(args["mode"]?.GetValue<string>()??"physical32",args["depth"]?.GetValue<int>()??3,args["max_offset"]?.GetValue<int>()??256,args["negative_offsets"]?.GetValue<bool>()??false,args["aligned"]?.GetValue<bool>()??true,args["all_regions"]?.GetValue<bool>()==true?null:args["region"]?.GetValue<int>()??0,args["segment"]?.GetValue<int>()??0,args["wrap20"]?.GetValue<bool>()??false,args["max_results"]?.GetValue<int>()??1000);
        await s.ScanPointers(args["target"]?.GetValue<int>()??throw new ArgumentException("Supply mapped target address."),options,action=="rescan",t);var p=s.Pointers!;
        return new{count=p.Candidates.Length,limited=p.Limited,limit_reason=p.LimitReason,options=p.Options,target=p.Target,note="Saved per game. Page paths with dosbox_cheats; add one with dosbox_cheat_watch action=add_pointer and candidate index. Rescan uses saved options."};
    },token);
    internal Task<object> McpDisassemble(JsonObject args,CancellationToken token)=>CheatOperation<object>(async(s,t)=>{
        int bits=args["bits"]?.GetValue<int>()??16;ulong ip=DisassemblyIP(args["ip"]?.GetValue<string>()??"0",bits);
        return await s.Core.Disassemble(args["address"]!.GetValue<int>(),bits,ip,args["count"]?.GetValue<int>()??64,args["bytes"]?.GetValue<int>()??1024,t);
    },token);
}
