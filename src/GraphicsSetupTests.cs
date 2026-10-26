using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DosboxEnhanced;

static class GraphicsSetupTests
{
    sealed class Response(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)});
    }
    public static void Run(Action<bool,string> check)
    {
        new PhoenixOptions().Validate();
        var settings=new EffectSettings { Phoenix=new(NeuralScale:75,Blocks:4,Repeats:"1,0,2,1,1,1,1,1",Gamma:1.2) };
        check(JsonSerializer.Deserialize<EffectSettings>(JsonSerializer.Serialize(settings))!.Phoenix==settings.Phoenix,"advanced Phoenix controls survive environment serialization");
        check(JsonSerializer.Deserialize<EffectSettings>("{}")!.Phoenix.NeuralScale==100,"old environments receive compatible Phoenix defaults");
        foreach(var invalid in new[]{new PhoenixOptions(NeuralScale:9),new PhoenixOptions(Skin:double.NaN),new PhoenixOptions(Blocks:9),new PhoenixOptions(Repeats:"1,2"),new PhoenixOptions(Repeats:"1,1,1,1,1,1,1,5")})
        {
            bool refused=false;try{invalid.Validate();}catch(ArgumentException){refused=true;}check(refused,"invalid advanced Phoenix controls rejected");
        }
        string folder=Path.Combine(Path.GetTempPath(),"dosbox-download-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        try
        {
            byte[] bytes=Encoding.UTF8.GetBytes("owned download integrity fixture");string hash=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();string file=Path.Combine(folder,"payload.bin");
            using var client=new HttpClient(new Response(bytes));GraphicsSetup.Download(client,"https://fixture.invalid/file",file,hash,null,CancellationToken.None).GetAwaiter().GetResult();
            check(File.ReadAllBytes(file).SequenceEqual(bytes),"download is installed only after its SHA256 matches");
            bool refused=false;try{GraphicsSetup.Download(client,"https://fixture.invalid/file",file,new string('0',64),null,CancellationToken.None).GetAwaiter().GetResult();}catch(InvalidDataException){refused=true;}
            check(refused&&File.ReadAllBytes(file).SequenceEqual(bytes)&&!File.Exists(file+".partial"),"bad download cannot replace a verified file and partial output is cleaned");
            using var cancelled=new CancellationTokenSource();cancelled.Cancel();bool stopped=false;
            try{GraphicsSetup.Download(client,"https://fixture.invalid/file",file+"2",hash,null,cancelled.Token).GetAwaiter().GetResult();}catch(OperationCanceledException){stopped=true;}
            check(stopped&&!File.Exists(file+"2.partial"),"cancelled download leaves no partial file");
        }
        finally{Directory.Delete(folder,true);}
    }
}
