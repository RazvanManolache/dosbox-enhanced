using Iced.Intel;

namespace DosboxEnhanced;

public sealed record DisassemblyLine(long Address,long? PhysicalAddress,ulong IP,string Bytes,string Instruction,string Flow,bool Valid,ulong? BranchTarget=null)
{
    public string AddressText=>"0x"+Address.ToString("X8");
    public string PhysicalText=>PhysicalAddress is long p?"0x"+p.ToString("X8"):"—";
    public string IPText=>"0x"+IP.ToString("X");
}
public sealed record DisassemblyResult(int Bits,long Address,ulong IP,int BytesRead,DisassemblyLine[] Instructions,string Note);
public static class DosDisassembler
{
    public static DisassemblyResult Decode(byte[] bytes,long address,long? physical,int bits,ulong ip,int count)
    {
        if(bits is not (16 or 32)||count is <1 or >256||bytes.Length is <1 or >4096||address<0||ip>(bits==16?65535UL:uint.MaxValue))throw new ArgumentException("Disassembly: choose 16/32 bits, 1–256 instructions, a valid logical IP and up to 4096 bytes.");
        var decoder=Decoder.Create(bits,new ByteArrayCodeReader(bytes));decoder.IP=ip;
        var formatter=new NasmFormatter();formatter.Options.HexPrefix="0x";formatter.Options.HexSuffix=null;
        var output=new StringOutput();var lines=new List<DisassemblyLine>();int consumed=0;
        while(consumed<bytes.Length&&lines.Count<count){decoder.Decode(out var instruction);int length=instruction.Length;if(length==0)break;
            formatter.Format(instruction,output);bool valid=instruction.Code!=Code.INVALID;
            lines.Add(new(address+consumed,physical+consumed,instruction.IP,Convert.ToHexString(bytes.AsSpan(consumed,length)),output.ToStringAndReset(),valid?instruction.FlowControl.ToString():"Invalid / truncated",valid,instruction.Op0Kind is OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64?instruction.NearBranchTarget:null));consumed+=length;
        }
        return new(bits,address,ip,bytes.Length,lines.ToArray(),"Read-only x86 decoding. Mode and logical IP are supplied manually; no CPU registers, paging/selector translation, stepping or code/data detection.");
    }
}
public sealed partial class DosCore
{
    public Task<DisassemblyResult> Disassemble(long address,int bits,ulong ip,int count,int bytes,CancellationToken token)=>MemoryCommand(()=>{
        if(bytes is <1 or >4096)throw new ArgumentException("Read 1–4096 disassembly bytes.");
        var map=ResolveRam(address,1);int length=(int)Math.Min(bytes,map.Info.Start+map.Info.Length-address);
        return DosDisassembler.Decode(ReadRam(address,length),address,map.Info.PhysicalStart+address-map.Info.Start,bits,ip,count);
    },token);
}
