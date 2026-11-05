namespace DosboxEnhanced;

static class PointerTests
{
    public static void Run(Action<bool,string> check){
        void Reject(Action action,string name){bool rejected=false;try{action();}catch(ArgumentException){rejected=true;}check(rejected,name);}
        var regions=new[]{new RamRegion(0,0,512,true,"Game",0x2000),new RamRegion(1,0x100000,128,true,"System",0),new RamRegion(2,0x200000,512,true,"Extended",0xA0000)};
        var bytes=new byte[512];void Put(int slot,uint value)=>BitConverter.GetBytes(value).CopyTo(bytes,slot);
        Put(16,0x203c);Put(64,0xA0040); // [16]+4 -> slot64; [64]+8 -> extended+72.
        RamSnapshot Snapshot()=>new(1,[new(regions[0],(byte[])bytes.Clone()),new(regions[1],new byte[128]),new(regions[2],new byte[512])],regions);
        long Resolve(PointerPath p,RamSnapshot s)=>PointerMemory.Resolve(p,s.AddressMap,(a,n)=>PointerMemory.Read(s,a,n));
        var snapshot=Snapshot();var path=new PointerPath(16,[4,8]);
        check(Resolve(path,snapshot)==0x200048,"physical pointer chains translate through rearranged core RAM regions");
        var scan=PointerSearch.Scan(snapshot,0x200048,new(Depth:2,MaxOffset:8),CancellationToken.None);
        check(scan.Candidates.Any(c=>c.Path.BaseAddress==16&&c.Path.Offsets.SequenceEqual(new[]{4,8}))&&!scan.Limited,"backward scan discovers a multi-level chain across a mapped address gap");
        Put(64,0xA0080);var moved=Snapshot();var rescanned=PointerSearch.Rescan(scan,moved,0x200088,CancellationToken.None);
        check(rescanned.Candidates.Any(c=>c.Path.BaseAddress==16)&&Resolve(path,moved)==0x200088,"rescan keeps chains that follow a relocated allocation");
        check(PointerSearch.Rescan(scan,moved,0x200048,CancellationToken.None).Candidates.Length==0,"rescan eliminates paths that no longer reach the supplied target");
        Put(16,0);Reject(()=>Resolve(path,Snapshot()),"null pointers are rejected before touching a target");
        Put(16,0x2040);Put(64,0x2010);Reject(()=>Resolve(new(16,[0,0,0]),Snapshot()),"cyclic pointer chains terminate safely");
        Reject(()=>Resolve(new(16,[0,0]),Snapshot()),"cycles ending exactly at the final target are rejected");
        check(PointerSearch.Scan(Snapshot(),16,new(Depth:3,MaxOffset:0),CancellationToken.None).Candidates.All(c=>c.Path.BaseAddress!=16&&c.Path.Offsets.Length==1),"backward search excludes roots that form a cycle through the target");
        Put(16,0xA0060);var negative=PointerSearch.Scan(Snapshot(),0x200058,new(Depth:1,MaxOffset:8,NegativeOffsets:true),CancellationToken.None);
        check(negative.Candidates.Any(c=>c.Path.BaseAddress==16&&c.Path.Offsets[0]==-8),"negative offsets are optional and retain their sign");
        Put(16,0x01230004);check(Resolve(new(16,[0],"far16"),new(1,[new(new(0,0,65536,true,"Physical",0),bytes.Concat(new byte[65536-bytes.Length]).ToArray())]))==0x1234,"16:16 pointers decode offset word then segment word");
        var near=new RamSnapshot(1,[new(new(0,0,65536,true,"Physical",0),new byte[65536])]);near.Blocks[0].Bytes[16]=0x34;near.Blocks[0].Bytes[17]=0x12;
        check(Resolve(new(16,[0],"near16",0x200),near)==0x3234,"near pointers use the explicit real-mode segment");
        near.Blocks[0].Bytes[16]=near.Blocks[0].Bytes[17]=0;check(Resolve(new(16,[0],"near16",0x200),near)==0x2000,"near segment:0000 remains a valid pointer when the segment is nonzero");
        var wrapBytes=new byte[128];BitConverter.GetBytes(0xFFFF0u).CopyTo(wrapBytes,16);var wrap=new RamSnapshot(1,[new(new(0,0,128,true,"Physical",0),wrapBytes)]);
        check(Resolve(new(16,[48],Wrap20:true),wrap)==32&&PointerSearch.Scan(wrap,32,new(Depth:1,MaxOffset:48,Wrap20:true),CancellationToken.None).Candidates.Any(c=>c.Path.BaseAddress==16&&c.Path.Offsets[0]==48),"A20-off wrapping works in both resolution and backward searches");
        Reject(()=>Resolve(new(16,[0]),wrap),"non-wrapped physical pointers outside RAM are rejected");
        Put(16,0x200040);check(Resolve(new(16,[8],"mapped32"),Snapshot())==0x200048,"mapped32 remains explicit for normalized-coordinate pointers");
        Reject(()=>PointerMemory.Coordinate(0,"physical32",[new(0,0,100,true,"Unknown")]),"physical translation is never guessed for an unknown core layout");
        Reject(()=>PointerMemory.Read(snapshot,511,4),"pointer slots cannot cross mapped region boundaries");
        Reject(()=>new PointerOptions(Depth:7).Validate(),"excessive pointer depth is rejected");
        Put(16,0xA0040);var limited=PointerSearch.Scan(Snapshot(),0x200048,new(Depth:2,MaxOffset:8,MaxResults:1),CancellationToken.None);
        check(limited.Limited&&limited.Candidates.Length==1,"bounded pointer searches explicitly report partial results");
        using(var cancel=new CancellationTokenSource()){cancel.Cancel();bool stopped=false;try{PointerSearch.Scan(Snapshot(),0x200048,new(),cancel.Token);}catch(OperationCanceledException){stopped=true;}check(stopped,"pointer indexing honors cancellation");}
        var code=new byte[]{0xc7,0x06,0,2,0x35,0xa1,0x30,0xe4,0xcd,0x16,0xff,0x0e,0,2,0xeb,0xf6};
        var decoded=DosDisassembler.Decode(code,0x120,0x20100,16,0x100,32);
        check(decoded.Instructions.Length==5&&decoded.Instructions[0].Instruction.Contains("mov")&&decoded.Instructions[2].Instruction.Contains("int")&&decoded.Instructions[^1].BranchTarget==0x106,"16-bit DOS fixture decodes with correct relative branch labels");
        check(decoded.Instructions[0].PhysicalAddress==0x20100&&decoded.Instructions[^1].Address==0x12e,"disassembly retains distinct mapped, physical and logical addresses");
        var mode16=DosDisassembler.Decode([0xb8,0x34,0x12,0x90,0x90],0,null,16,0,4);var mode32=DosDisassembler.Decode([0xb8,0x34,0x12,0x90,0x90],0,null,32,0,4);
        check(mode16.Instructions[0].Bytes.Length==6&&mode32.Instructions[0].Bytes.Length==10,"16-bit and 32-bit instruction modes produce distinct operand sizes");
        check(!DosDisassembler.Decode([0x0f],0,null,16,0,4).Instructions[0].Valid,"truncated instructions are explicitly marked invalid");
        Reject(()=>DosDisassembler.Decode(code,0,null,24,0,10),"disassembly rejects unsupported CPU decoding modes");
    }
}
