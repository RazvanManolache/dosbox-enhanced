using System.Text.Json;

namespace DosboxEnhanced;

static class CheatTests
{
    public static void Run(Action<bool,string> check)
    {
        void Reject(Action action,string name) {bool refused=false;try{action();}catch(ArgumentException){refused=true;}check(refused,name);}
        foreach(var (type,text) in new[]{("u8","255"),("i8","-128"),("u16","65535"),("i16","-32768"),("u32","4294967295"),("i32","-2147483648"),("u64","18446744073709551615"),("i64","-9223372036854775808"),("f32","1.25"),("f64","-1.23456789012345")})
            check(CheatValue.Format(CheatValue.Parse(text,type),type)==text,"cheat type round-trips without precision loss: "+type);
        check(CheatValue.Format(CheatValue.Parse("0xFF","i8"),"i8")=="-1","hexadecimal signed values preserve their bit pattern");
        Reject(()=>CheatValue.Parse("256","u8"),"cheats reject integer overflow");Reject(()=>CheatValue.Parse("-1","u16"),"cheats reject negative unsigned values");
        Reject(()=>CheatValue.Parse("NaN","f32"),"cheats reject NaN writes");Reject(()=>CheatValue.Parse("1e300","f32"),"cheats reject float32 overflow");
        Reject(()=>CheatValue.Address("-1"),"cheats reject negative addresses");check(CheatValue.Address("0x1234")==4660,"manual hexadecimal DOS addresses");
        RamSnapshot Snapshot(long revision,params ushort[] values) {var bytes=new byte[values.Length*2];for(int i=0;i<values.Length;i++)CheatValue.Parse(values[i].ToString(),"u16").CopyTo(bytes,i*2);return new(revision,[new(new(0,0,bytes.Length,true,"Fixture"),bytes)]);}
        var search=new CheatSearch();search.Scan(Snapshot(1,100,100,40,0),true,"u16","exact","100","",true,0,CancellationToken.None);
        check(search.Count==2&&search.Results(0,20).Select(r=>r.Address).SequenceEqual(new long[]{0,2}),"first exact scan discovers both candidates");
        search.Scan(Snapshot(1,90,100,40,0),false,"u16","decreased_by","10","",true,0,CancellationToken.None);
        check(search.Count==1&&search.Results(0,1)[0] is {Address:0,Value:"90",Previous:"100"},"decreased-by scan narrows candidates and retains the previous value");
        search.Scan(Snapshot(1,90,100,90,0),false,"u16","unchanged","","",true,0,CancellationToken.None);
        check(search.Count==1,"later scans cannot re-add previously eliminated addresses");
        var unknown=new CheatSearch();unknown.Scan(Snapshot(1,5,8,3),true,"u16","unknown","","",true,0,CancellationToken.None);
        unknown.Scan(Snapshot(1,5,9,2),false,"u16","changed","","",true,0,CancellationToken.None);
        check(unknown.Count==2,"unknown initial value narrows by changed values");
        unknown.Scan(Snapshot(1,5,10,2),false,"u16","increased","","",true,0,CancellationToken.None);
        check(unknown.Count==1&&unknown.Results(0,10)[0].Address==2,"increased compares against the immediately preceding scan");
        var range=new CheatSearch();range.Scan(Snapshot(1,10,20,30),true,"u16","between","10","20",true,0,CancellationToken.None);check(range.Count==2,"between includes both bounds");
        var unaligned=new CheatSearch();var block=new RamSnapshot(1,[new(new(0,0,4,true,"Fixture"),[0,0x34,0x12,0])]);
        unaligned.Scan(block,true,"u16","exact","0x1234","",false,0,CancellationToken.None);check(unaligned.Count==1&&unaligned.Results(0,1)[0].Address==1,"unaligned DOS values are found by default");
        unaligned.Scan(block,true,"u16","exact","0x1234","",true,0,CancellationToken.None);check(unaligned.Count==0,"aligned-only scan skips unaligned words");
        var savedCount=search.Count;using(var cancel=new CancellationTokenSource()){cancel.Cancel();bool stopped=false;try{search.Scan(Snapshot(1,0,0,0,0),true,"u16","unknown","","",true,0,cancel.Token);}catch(OperationCanceledException){stopped=true;}check(stopped&&search.Count==savedCount,"cancelled scans retain the previous result set");}
        bool stale=false;try{search.Scan(Snapshot(2,90,100,40,0),false,"u16","exact","90","",true,0,CancellationToken.None);}catch(InvalidOperationException){stale=true;}check(stale,"scan cannot continue across memory-layout changes");
        Reject(()=>range.Scan(Snapshot(1,5),true,"u16","between","20","10",true,0,CancellationToken.None),"inverted ranges are rejected before changing results");
        var watched=new CheatWatch{Target="99",Frozen=true};var loaded=JsonSerializer.Deserialize<CheatWatch>(JsonSerializer.Serialize(watched))!;check(!loaded.Frozen&&loaded.Target=="99","saved watches never auto-enable memory locks");
        var floatSearch=new CheatSearch();floatSearch.Scan(new(1,[new(new(0,0,4,true,"Float"),CheatValue.Parse("1.25","f32"))]),true,"f32","exact","1.25","",false,0,CancellationToken.None);check(floatSearch.Count==1,"float scans compare numeric values");
    }
}
