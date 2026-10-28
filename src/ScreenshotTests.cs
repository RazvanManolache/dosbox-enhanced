using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Windows.Media.Imaging;

namespace DosboxEnhanced;

static class ScreenshotTests
{
    public static void Run(Action<bool,string> check)
    {
        var source = new VideoFrame(2,1,[0,20,250,255,240,30,10,255],2,37,4);
        var output = new VideoFrame(1,2,[30,40,50,255,60,70,80,255],2,37,4,2,1);
        var pair = new ScreenshotPair(source,output,new(Passes:1,InputResize:true,InputScale:1.25),DateTimeOffset.UtcNow);
        var folder = Path.Combine(Paths.Root,"tests/fixtures/screenshot-codec"); Directory.CreateDirectory(folder);
        var receipt = Screenshots.SavePair(pair,folder); string stem = receipt[..^5];
        try {
            var metadata = JsonNode.Parse(File.ReadAllText(receipt))!;
            foreach(var (kind,frame) in new[] { ("original",source),("enhanced",output) }) {
                var png = File.ReadAllBytes(stem+"-"+kind+".png"); using var stream = new MemoryStream(png);
                var image = BitmapFrame.Create(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);
                var decoded = new byte[frame.Pixels.Length]; image.CopyPixels(decoded,frame.Width*4,0);
                check(image.PixelWidth==frame.Width&&image.PixelHeight==frame.Height&&decoded.SequenceEqual(frame.Pixels),"paired "+kind+" PNG preserves dimensions and exact BGRA pixels");
                check(metadata[kind]!["sha256"]!.GetValue<string>()==Convert.ToHexStringLower(SHA256.HashData(png)),"paired "+kind+" receipt authenticates PNG bytes");
            }
            check(metadata["frame"]!.GetValue<long>()==37&&metadata["epoch"]!.GetValue<long>()==4&&metadata["effects"]!["InputScale"]!.GetValue<double>()==1.25,"pair receipt preserves frame identity and fractional processing settings");
            check(!metadata.ToJsonString().Contains(Paths.Root,StringComparison.OrdinalIgnoreCase),"pair metadata excludes host paths");
        }
        finally { foreach(string suffix in new[]{".json","-original.png","-enhanced.png"})File.Delete(stem+suffix); }
    }
}
