using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace DosboxEnhanced;

public sealed partial class MainWindow
{
    public async Task RunScreenshotValidation()
    {
        var checks = new List<string>(); var limitations = new List<string>(); var report = new Dictionary<string, object?>();
        var output = Path.Combine(Paths.Root, "tests/results/screenshot-pair"); Directory.CreateDirectory(output);
        void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks.Add(name); }
        async Task<ScreenshotPair> Ready()
        {
            for (int i = 0; i < 600; i++) {
                if (phoenix.LatestPair is { } pair) return pair;
                if (phoenix.HasFailed) throw new Exception(phoenix.Status);
                await Task.Delay(50);
            }
            throw new TimeoutException("Graphics pair not ready: " + phoenix.Status);
        }
        static byte[] Decode(byte[] png, out int width, out int height)
        {
            using var stream = new MemoryStream(png); var bitmap = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            width = bitmap.PixelWidth; height = bitmap.PixelHeight; var pixels = new byte[width * height * 4]; bitmap.CopyPixels(pixels, width * 4, 0); return pixels;
        }
        try {
            bool absent = false;
            try { McpFramePair(); } catch (InvalidOperationException) { absent = true; }
            Check(absent, "pair capture rejects a missing session");
            var fixture = Path.Combine(Paths.Root, "tests/fixtures/pair"); Directory.CreateDirectory(fixture);
            // Original animated DOS fixture: mode 13h, fill VGA with a changing palette index.
            File.WriteAllBytes(Path.Combine(fixture, "PAIR.COM"), [0xb8,0x13,0,0xcd,0x10,0xb8,0,0xa0,0x8e,0xc0,0xfc,0x30,0xc0,0x31,0xff,0xb9,0,0xfa,0xf3,0xaa,0xfe,0xc0,0xeb,0xf5]);
            var effects = new EffectSettings(Enabled:false, NvidiaUpscaling:false, NvidiaFrameGeneration:false);
            Launch(new EnvironmentProfile { Id="screenshot-validation", Name="Screenshot validation", Startup="C:\\PAIR.COM", Effects=effects, Drives=[new(){Folder=fixture}] });
            var pair = await Ready(); var firstSequence = pair.Original.Sequence;
            for (int i = 0; i < 100 && phoenix.LatestPair?.Original.Sequence == firstSequence; i++) await Task.Delay(50);
            pair = McpFramePair();
            Check(pair.Original.Sequence > firstSequence, "capture works while the DOS fixture advances");
            Check(pair.Original.Sequence == pair.Enhanced.Sequence && pair.Original.Epoch == pair.Enhanced.Epoch, "one completed call publishes matching sequence and epoch");
            Check(pair.Original.Pixels.SequenceEqual(pair.Enhanced.Pixels), "native bypass original and processed pixels match exactly");
            var originalHash = SHA256.HashData(pair.Original.Pixels);
            await Task.Delay(200);
            Check(originalHash.SequenceEqual(SHA256.HashData(pair.Original.Pixels)), "retained source pixels remain immutable as emulation advances");
            var result = await McpTools.Call(this, "dosbox_screenshot", new() { ["pair"]=true }, CancellationToken.None);
            Check(result["content"]!.AsArray().Count == 3 && result["isError"]!.GetValue<bool>() == false, "MCP returns metadata and two ordered PNG images");
            var metadata = result["structuredContent"]!;
            for (int i = 0; i < 2; i++) {
                string kind = i == 0 ? "original" : "enhanced";
                var png = Convert.FromBase64String(result["content"]![i+1]!["data"]!.GetValue<string>());
                Decode(png, out int width, out int height);
                Check(metadata[kind]!["width"]!.GetValue<int>() == width && metadata[kind]!["height"]!.GetValue<int>() == height &&
                    metadata[kind]!["sha256"]!.GetValue<string>() == Convert.ToHexStringLower(SHA256.HashData(png)), "MCP " + kind + " PNG dimensions and checksum are correct");
            }
            bool conflict = false;
            try { await McpTools.Call(this, "dosbox_screenshot", new(){["pair"]=true,["enhanced"]=true}, CancellationToken.None); } catch (ArgumentException) { conflict = true; }
            Check(conflict, "ambiguous pair/enhanced request is rejected");
            foreach (var route in new[] { (Renderer:"phoenix", Sr:false), (Renderer:"phoenix", Sr:true), (Renderer:"nvidia", Sr:true) }) {
                string rendererName = route.Renderer + (route.Sr ? " + NVIDIA SR" : " native");
                phoenix.Settings = effects with { Enabled=true, Renderer=route.Renderer, InputResize=true, InputScale=1.25, NvidiaUpscaling=route.Sr, UpscaleFactor=2, Passes=1 };
                phoenix.Reset(); submitted=-1;
                Check(phoenix.LatestPair == null, "reset invalidates the previous pair for " + rendererName);
                try { pair = await Ready(); }
                catch (Exception e) when (route.Sr && e.Message.Contains("NGX 0xBAD00001")) {
                    limitations.Add(rendererName + ": current NGX runtime reports unsupported hardware (0xBAD00001); also reproduced with the prior released bridge.");
                    Check(phoenix.LatestPair == null, rendererName + " failure exposes no stale or unmatched pair"); continue;
                }
                Check(pair.Original.Width == 320 && pair.Original.Height == 200 && pair.Enhanced.Width == (route.Sr ? 800 : 400) && pair.Enhanced.Height == (route.Sr ? 500 : 250),
                    rendererName + " capture preserves native original and fractional-resize output resolutions");
                Check(!pair.Original.Pixels.SequenceEqual(pair.Enhanced.Pixels) && pair.Effects.Renderer == route.Renderer && pair.Effects.Passes == 1,
                    rendererName + " pair retains the processing settings for its own frame");
                var receipt = Screenshots.SavePair(pair, output);
                var saved = JsonNode.Parse(File.ReadAllText(receipt))!;
                var stem = receipt[..^5];
                foreach (string kind in new[] { "original", "enhanced" }) {
                    var png = File.ReadAllBytes(stem+"-"+kind+".png"); var decoded = Decode(png, out _, out _);
                    Check(decoded.SequenceEqual(kind == "original" ? pair.Original.Pixels : pair.Enhanced.Pixels) && saved[kind]!["sha256"]!.GetValue<string>() == Convert.ToHexStringLower(SHA256.HashData(png)),
                        rendererName + " saved " + kind + " PNG round-trips exact pixels and matches manifest");
                }
            }
            phoenix.Settings = effects with { Enabled=true }; phoenix.Reset(); submitted=-1; await Ready();
            var camera = menu.Children.OfType<Button>().Single(b => b.ToolTip?.ToString()?.StartsWith("Screenshots ·") == true);
            camera.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            var popup = screenshotPopup!;
            Check(popup.Items.Count == 3, "camera menu exposes displayed, original and paired captures");
            await Task.Delay(100); Check(popup.IsOpen, "camera dropdown opens in the running app");
            if (System.Windows.PresentationSource.FromVisual(popup)?.RootVisual is System.Windows.FrameworkElement popupRoot) CaptureElement(popupRoot, Path.Combine(output,"menu.png")); popup.IsOpen=false;
            ((MenuItem)popup.Items[2]).RaiseEvent(new System.Windows.RoutedEventArgs(MenuItem.ClickEvent));
            const string noticePrefix = "Screenshot pair saved: ";
            Check(captureNotice.StartsWith(noticePrefix), "camera pair action reports the saved capture");
            string savedReceipt = captureNotice[noticePrefix.Length..];
            Check(File.Exists(savedReceipt) && File.Exists(savedReceipt[..^5]+"-original.png") && File.Exists(savedReceipt[..^5]+"-enhanced.png"), "camera pair action saves both images and the receipt");
            foreach (string suffix in new[] { ".json", "-original.png", "-enhanced.png" }) File.Delete(savedReceipt[..^5]+suffix);
            Stop();
            bool stopped = false; try { McpFramePair(); } catch (InvalidOperationException) { stopped = true; }
            Check(stopped && phoenix.LatestPair == null, "stopping DOS clears paired capture availability");
            report["passed"]=true;
        }
        catch (Exception e) { report["passed"]=false; report["error"]=e.ToString(); }
        finally { Stop(); report["checks"]=checks; report["limitations"]=limitations; Paths.AtomicWrite(Path.Combine(Paths.Root,"tests/results/screenshot-pair.json"),JsonSerializer.Serialize(report,Paths.Json)); Close(); }
    }
}
