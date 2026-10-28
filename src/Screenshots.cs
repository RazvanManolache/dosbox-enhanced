using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DosboxEnhanced;

// Published atomically by the graphics worker. Both pixel arrays belong to this completed call;
// neither the current emulator frame nor the current controls are consulted when capturing it.
public sealed record ScreenshotPair(VideoFrame Original, VideoFrame Enhanced, EffectSettings Effects, DateTimeOffset CompletedAt);

public static class Screenshots
{
    public static byte[] Encode(VideoFrame frame)
    {
        var image = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null, frame.Pixels, checked(frame.Width * 4));
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }
    public static object Describe(ScreenshotPair pair, byte[] original, byte[] enhanced) => new {
        schema = "dosbox-screenshot-pair/1", frame = pair.Original.Sequence, epoch = pair.Original.Epoch,
        completed_at = pair.CompletedAt, aspect = pair.Original.Aspect,
        original = new { width = pair.Original.Width, height = pair.Original.Height, sha256 = Convert.ToHexStringLower(SHA256.HashData(original)) },
        enhanced = new { width = pair.Enhanced.Width, height = pair.Enhanced.Height, sha256 = Convert.ToHexStringLower(SHA256.HashData(enhanced)) },
        effects = pair.Effects,
        note = "Same source frame; native original and processed output at their own resolutions. Excludes generated presentation frames; compare mode, if enabled, remains visible."
    };
    public static string SavePair(ScreenshotPair pair, string directory)
    {
        var original = Encode(pair.Original); var enhanced = Encode(pair.Enhanced);
        string stem = Path.Combine(directory, "pair-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        // Publish the manifest last: it is the receipt that both PNGs were saved successfully.
        try {
            File.WriteAllBytes(stem + "-original.png", original); File.WriteAllBytes(stem + "-enhanced.png", enhanced);
            File.WriteAllText(stem + ".json", JsonSerializer.Serialize(Describe(pair, original, enhanced), Paths.Json));
            return stem + ".json";
        }
        catch { foreach (var suffix in new[] { "-original.png", "-enhanced.png", ".json" }) File.Delete(stem + suffix); throw; }
    }
}

public sealed partial class MainWindow
{
    string captureNotice = "";
    ContextMenu? screenshotPopup;
    double captureNoticeUntil;
    void CaptureNotice(string text) { captureNotice = text; captureNoticeUntil = clock.Elapsed.TotalSeconds + 8; status.Text = text; }
    internal ContextMenu ScreenshotMenu(Button button)
    {
        var popup = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom, Background = Brush(30,40,54), Foreground = Brush(221,231,241) };
        var itemStyle = new Style(typeof(MenuItem)); itemStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brush(221,231,241)));
        var highlight = new Trigger { Property = MenuItem.IsHighlightedProperty, Value = true }; highlight.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.Black)); itemStyle.Triggers.Add(highlight);
        popup.Resources[typeof(MenuItem)] = itemStyle;
        void Item(string label, Action action, bool available) { var item = new MenuItem { Header = label, IsEnabled = available }; item.Click += (_, _) => Try(action); popup.Items.Add(item); }
        Item("Save displayed image", Screenshot, displayed != null);
        Item("Save original DOS pixels", () => SaveScreenshot(McpFrame(false)), core?.Latest != null);
        Item("Save original + enhanced pair", () => CaptureNotice("Screenshot pair saved: " + Screenshots.SavePair(McpFramePair(), Path.Combine(Paths.Data, "screenshots"))), core is { IsReady:true } && phoenix.LatestPair != null);
        return popup;
    }
    void ShowScreenshotMenu(Button button)
    {
        var popup = ScreenshotMenu(button); screenshotPopup = popup; button.ContextMenu = popup; popup.IsOpen = true;
    }
    void SaveScreenshot(VideoFrame frame)
    {
        var directory = Path.Combine(Paths.Data, "screenshots"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N")[..8] + ".png");
        File.WriteAllBytes(path, Screenshots.Encode(frame)); CaptureNotice("Screenshot saved: " + path);
    }
    internal ScreenshotPair McpFramePair() => core is { IsReady: true } && phoenix.LatestPair is { } pair ? pair :
        throw new InvalidOperationException("No completed original/enhanced pair is available. Start a DOS environment and wait for graphics; no unmatched fallback is captured.");
}
