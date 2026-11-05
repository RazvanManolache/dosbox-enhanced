using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace DosboxEnhanced;

static class CheatValidation
{
    public static async Task<int> Run()
    {
        var checks=new List<string>();var report=new Dictionary<string,object?>();void Check(bool value,string name){if(!value)throw new Exception(name);checks.Add(name);}
        string folder=Path.Combine(Paths.Root,"tests/fixtures/cheats");Directory.CreateDirectory(folder);DosCore? core=null;string? watchId=null;CheatSession? session=null;
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(55));var token=timeout.Token;
        async Task<ushort> Read(long address) {var b=await core!.ReadMemory(checked((int)address),2,token);return BitConverter.ToUInt16(b);}
        async Task Key() {core!.Key(97,true,97,0);await Task.Delay(70,token);core.Key(97,false,0,0);await Task.Delay(120,token);}
        try {
            // Initialize DS:0200, wait for a BIOS key, decrement the word, repeat.
            File.WriteAllBytes(Path.Combine(folder,"CHEAT.COM"),[0xc7,0x06,0,2,0x35,0xa1,0x30,0xe4,0xcd,0x16,0xff,0x0e,0,2,0xeb,0xf6]);
            var profile=new EnvironmentProfile{Id="cheat-validation",Name="Cheat validation",Startup="C:\\CHEAT.COM",Drives=[new(){Folder=folder}]};
            var config=Path.Combine(folder,"dosbox.conf");File.WriteAllText(config,Configuration.Generate(profile));core=new(config,Path.Combine(folder,"saves"),sound:false);
            RamRegion[] regions=[];
            for(int i=0;i<150;i++){if(core.Error!=null)throw new Exception(core.Error);if(core.IsReady){try{regions=await core.GetRamRegions(token);if(regions.Length>0){session=new(core,"automated-cheat-validation");await session.Scan(true,"u16","exact","41269","",false,0,token);if(session.Search.Count>0)break;}}catch(InvalidOperationException){}}await Task.Delay(50,token);}
            Check(session?.Search.Count>0&&regions.Length>=2,"real DOSBox exposes guest RAM through its memory-map callback");report["regions"]=regions;report["initial_candidates"]=session!.Search.Count;
            await Key();await session.Scan(false,"u16","decreased_by","1","",false,0,token);
            Check(session.Search.Count==1,"gameplay input and next scan isolate the live DOS word from its instruction literal");
            long address=session.Search.Results(0,1)[0].Address;Check(await Read(address)==41268,"read uses the core's mapped game address");
            var watch=await session.Add("Test health",address,"u16",token);watchId=watch.Id;
            await session.Update(watch.Id,null,"777",false,true,token);Check(await Read(address)==777,"typed edit changes actual guest RAM");
            await session.Update(watch.Id,null,"999",true,false,token);await Key();Check(await Read(address)==999&&watch.Frozen,"freeze survives gameplay writes across emulated frames");
            bool overlap=false;try{await core.SetCheatValue("overlap",address+1,[1],true,core.MemoryRevision,token);}catch(ArgumentException){overlap=true;}Check(overlap&&await Read(address)==999,"overlapping locks are rejected without partial writes");
            await session.Update(watch.Id,null,null,false,false,token);await Key();Check(await Read(address)==998,"unlock releases the value to the game");
            bool boundary=false;try{await core.SetCheatValue("boundary",regions[0].Start+regions[0].Length-1,[1,2],false,core.MemoryRevision,token);}catch(ArgumentException){boundary=true;}Check(boundary,"edits cannot cross a mapped RAM boundary");
            using(var cancelled=new CancellationTokenSource()){cancelled.Cancel();bool stopped=false;try{await core.SetCheatValue("cancelled",address,CheatValue.Parse("123","u16"),false,core.MemoryRevision,cancelled.Token);}catch(OperationCanceledException){stopped=true;}await Task.Delay(80,token);Check(stopped&&await Read(address)==998,"cancelled writes do not execute later from the emulation queue");}
            var restored=new CheatSession(core,"automated-cheat-validation");Check(restored.Watches.Single(w=>w.Id==watch.Id).Name=="Test health"&&!restored.Watches.Single(w=>w.Id==watch.Id).Frozen,"per-game watch list reloads with locks disabled");
            Check(regions.All(r=>r.PhysicalStart!=null),"DOSBox Pure physical RAM translation is verified against its mapped layout");
            long root=address+0x100,other=address+0x20;
            uint Physical(long a){var r=regions.Single(r=>a>=r.Start&&a<r.Start+r.Length);return checked((uint)(r.PhysicalStart!.Value+a-r.Start));}
            async Task Write(long a,byte[] b)=>await core.SetCheatValue("pointer-fixture",a,b,false,core.MemoryRevision,token);
            await Write(root,BitConverter.GetBytes(Physical(address)));
            await session.ScanPointers(address,new(Depth:1,MaxOffset:0),false,token);
            int pointerIndex=Array.FindIndex(session.Pointers!.Candidates,c=>c.Path.BaseAddress==root);
            Check(pointerIndex>=0,"pointer scan finds a real physical pointer to the DOS variable");
            var pointerWatch=await session.AddPointer(pointerIndex,"Moving health","u16",token);
            await session.Update(pointerWatch.Id,null,"888",true,false,token);Check(await Read(address)==888,"pointer watch locks the resolved real DOS target");
            await Write(other,CheatValue.Parse("222","u16"));await Write(root,BitConverter.GetBytes(Physical(other)));await Task.Delay(100,token);await session.Refresh(token);
            Check(await Read(other)==888&&await Read(address)==888&&pointerWatch.Address==other,"dynamic pointer freeze follows a moved target without rewriting the old one");
            await Write(address,CheatValue.Parse("998","u16"));await Task.Delay(70,token);Check(await Read(address)==998,"previous pointer target is released when the chain moves");
            await session.ScanPointers(other,new(),true,token);Check(session.Pointers!.Candidates.Any(c=>c.Path.BaseAddress==root),"saved chain rescan follows the relocated target in live guest RAM");
            var pointerReload=new CheatSession(core,"automated-cheat-validation");Check(pointerReload.Pointers!.Candidates.Any(c=>c.Path.BaseAddress==root)&&pointerReload.Watches.Single(w=>w.Id==pointerWatch.Id).Pointer!=null&&!pointerReload.Watches.Single(w=>w.Id==pointerWatch.Id).Frozen,"pointer paths and candidates persist while locks stay disabled on reload");
            await Write(root,new byte[4]);await Task.Delay(100,token);Check(core.ActiveCheatLocks==0,"invalid pointer updates the toolbar lock count even with the panel closed");await session.Refresh(token);Check(!pointerWatch.Frozen&&pointerWatch.Current=="Unavailable"&&core.IsReady,"null chain disarms the lock and leaves emulation running");
            await session.Remove(pointerWatch.Id,token);session.ClearPointers();
            var disassembly=await core.Disassemble(address-0x100,16,0x100,6,16,token);
            Check(disassembly.Instructions.Length==5&&disassembly.Instructions[0].Instruction.Contains("mov")&&disassembly.Instructions[2].Instruction.Contains("int"),"disassembler reads and decodes the actual running COM program");
            var edge=await core.Disassemble(regions[0].Start+regions[0].Length-1,16,0,4,4096,token);Check(edge.BytesRead==1,"live disassembly clamps reads at RAM region boundaries");
            var state=Path.Combine(folder,"check.state");await core.SaveState(state);await session.Update(watch.Id,null,"333",true,false,token);await core.LoadState(state);session.Sync();Check(!watch.Frozen&&await Read(address)==998,"loading a save state clears locks and preserves the restored value");
            long revision=core.MemoryRevision;core.Reset();for(int i=0;i<60&&core.MemoryRevision==revision;i++)await Task.Delay(30,token);session.Sync();Check(core.MemoryRevision!=revision&&!session.Search.Started&&!watch.Frozen,"reset invalidates scans and disarms watches");
            report["passed"]=true;
        } catch(Exception e){report["passed"]=false;report["error"]=e.ToString();}
        finally {if(session!=null&&watchId!=null&&core?.IsReady==true)try{await session.Remove(watchId,CancellationToken.None);}catch{}core?.Dispose();report["checks"]=checks;Paths.AtomicWrite(Path.Combine(Paths.Root,"tests/results/cheats.json"),JsonSerializer.Serialize(report,Paths.Json));}
        return report["passed"] is true?0:1;
    }
}
