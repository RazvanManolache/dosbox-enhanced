using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DosboxEnhanced;

public sealed partial class MainWindow
{
    // Developer-only captures use disposable game copies and a separate app/data folder.
    async Task CaptureGameGallery(JsonObject manifest)
    {
        string output = Path.GetFullPath(manifest["output"]!.GetValue<string>());
        Directory.CreateDirectory(output);
        var games = new JsonArray();
        var proof = new JsonObject { ["capturedUtc"] = DateTimeOffset.UtcNow.ToString("o"), ["games"] = games, ["passed"] = false };
        bool explore = manifest["explore"]?.GetValue<bool>() == true;
        try
        {
            if (library.Environments.Any(p => !p.Id.StartsWith("showcase-"))) throw new InvalidOperationException("Gallery requires a disposable demo library.");
            Stop(); library.Environments.Clear(); preferences.Audio = new(0); volume.Value = 0;
            Width = 1440; Height = 1040; SetAllPanels(true); ((TabControl)deck.Children[0]).SelectedIndex = 0;
            foreach (var item in manifest["games"]!.AsArray())
            {
                var game = item!.AsObject();
                library.Environments.Add(new() { Id = "showcase-" + game["slug"]!.GetValue<string>(), Name = game["title"]!.GetValue<string>(),
                    Startup = "C:\\" + game["exe"]!.GetValue<string>(), Cycles = game["cycles"]?.GetValue<string>() ?? "auto",
                    Drives = [new() { Folder = game["folder"]!.GetValue<string>(), Label = "Games" }],
                    Effects = game["effects"]?.Deserialize<EffectSettings>() ?? new(Enabled: false, NvidiaUpscaling: false, NvidiaFrameGeneration: false),
                    DiscoverPrograms = false, Programs = [new() { Path = "C:\\" + game["exe"]!.GetValue<string>(), Name = game["title"]!.GetValue<string>(), Available = true }] });
            }
            library.Save();
            foreach (var item in manifest["games"]!.AsArray())
            {
                var game = item!.AsObject(); string slug = game["slug"]!.GetValue<string>();
                var profile = library.Environments.Single(p => p.Id == "showcase-" + slug);
                var result = new JsonObject { ["game"] = profile.Name, ["slug"] = slug, ["effects"] = JsonSerializer.SerializeToNode(profile.Effects) }; games.Add(result);
                environments.SelectedItem = profile; Launch(profile); FocusForValidation();
                await Task.Delay(game["bootMs"]?.GetValue<int>() ?? 5000);
                if (explore && core?.Latest is VideoFrame initial) SaveFrame(initial, Path.Combine(output, slug + "-boot.png"));
                int step = 0;
                foreach (var action in game["actions"]?.AsArray() ?? new JsonArray())
                {
                    if (action!["key"] is JsonNode key)
                    {
                        uint code = (uint)key.GetValue<int>(); core!.Key(code, true); await Task.Delay(action["holdMs"]?.GetValue<int>() ?? 80); core.Key(code, false);
                    }
                    if (action["mouse"] is JsonArray movement) core!.MouseMove(movement[0]!.GetValue<int>(), movement[1]!.GetValue<int>());
                    if (action["click"]?.GetValue<bool>() == true) { core!.MouseButton(1, true); await Task.Delay(100); core.MouseButton(1, false); }
                    await Task.Delay(action["waitMs"]?.GetValue<int>() ?? 1200);
                    if (explore && core?.Latest is VideoFrame stage) SaveFrame(stage, Path.Combine(output, $"{slug}-step-{++step:00}.png"));
                }
                if (core?.Latest is not VideoFrame) throw new Exception("No DOS frame for " + profile.Name);
                if (!explore)
                {
                    long generatedBefore = JsonNode.Parse(phoenix.RuntimeStatus)?["generated"]?.GetValue<long>() ?? 0;
                    for (int i = 0; i < 150; i++)
                    {
                        FocusForValidation(); await Task.Delay(100);
                        var observed = JsonNode.Parse(phoenix.RuntimeStatus)!;
                        if (observed["generated"]?.GetValue<long>() > generatedBefore + 2 && phoenix.Latest != null) break;
                        if (phoenix.Status.StartsWith("Graphics unavailable")) throw new Exception(phoenix.Status);
                    }
                    var live = JsonNode.Parse(phoenix.RuntimeStatus)!.AsObject(); result["liveGraphics"] = live;
                    result["generatedDuringObservation"] = (live["generated"]?.GetValue<long>() ?? 0) - generatedBefore;
                    if (profile.Effects.NvidiaFrameGeneration && (live["generated"]?.GetValue<long>() <= generatedBefore || live["frame_generation_status_bits"]?.GetValue<int>() != 0)) throw new Exception("No successful generated presentation for " + profile.Name);
                    Tick(); CaptureWindow(Path.Combine(output, slug + "-workspace.png"));
                }
                core.Pause(true); await Task.Delay(150); var source = core.Latest!;
                SaveFrame(source, Path.Combine(output, slug + "-original.png"));
                result["source"] = new JsonArray(source.Width, source.Height); result["aspect"] = source.Aspect;
                if (!explore)
                {
                    phoenix.Reset();
                    for (int i = 0; i < 16; i++)
                    {
                        long completed = phoenix.Completed; phoenix.Submit(source);
                        for (int wait = 0; wait < 3000 && phoenix.Completed <= completed; wait++)
                        { await Task.Delay(10); if (phoenix.Status.StartsWith("Graphics unavailable")) throw new Exception(phoenix.Status); }
                        if (phoenix.Completed <= completed) throw new TimeoutException("Gallery graphics timeout");
                    }
                    var enhanced = phoenix.Latest ?? throw new Exception("No enhanced frame");
                    Tick(); SaveFrame(enhanced, Path.Combine(output, slug + "-enhanced.png"));
                    SaveGameComparison(source, enhanced, profile, Path.Combine(output, slug + "-comparison.png"));
                    result["frozenGraphics"] = JsonNode.Parse(phoenix.RuntimeStatus);
                }
                result["passed"] = true; Paths.AtomicWrite(Path.Combine(output, "gallery-proof.private.json"), proof.ToJsonString(Paths.Json)); Stop();
            }
            proof["passed"] = true;
        }
        catch (Exception e) { proof["error"] = e.ToString(); }
        finally { Paths.AtomicWrite(Path.Combine(output, "gallery-proof.private.json"), proof.ToJsonString(Paths.Json)); Close(); }
    }

    static void SaveGameComparison(VideoFrame before, VideoFrame after, EnvironmentProfile profile, string path)
    {
        var effects = profile.Effects; double aspect = before.Aspect > 0 ? before.Aspect : 4.0 / 3;
        var panel = new StackPanel { Width = 1344, Background = Brush(15, 20, 29) };
        panel.Children.Add(new TextBlock { Text = profile.Name + " · selected settings", FontSize = 26, Foreground = Brushes.WhiteSmoke, Margin = new Thickness(24, 20, 24, 16) });
        var grid = new Grid { Margin = new Thickness(24, 0, 24, 0) }; grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new());
        string name = effects.Renderer == "nvidia" ? "NVIDIA NR" : "Phoenix";
        foreach (var (frame, label, col) in new[] { (before, $"Original · {before.Width} × {before.Height}", 0), (after, $"{name} + NVIDIA SR · {after.Width} × {after.Height}", 1) })
        {
            var cell = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
            cell.Children.Add(new TextBlock { Text = label, FontSize = 16, Foreground = Brush(120, 221, 224), Margin = new Thickness(0, 0, 0, 10) });
            var image = new Image { Source = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null, frame.Pixels, frame.Width * 4), Width = 632, Height = 632 / aspect, Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(image, col == 0 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
            cell.Children.Add(image); Grid.SetColumn(cell, col); grid.Children.Add(cell);
        }
        panel.Children.Add(grid);
        string entry = effects.InputResize ? effects.InputScale.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "× " + effects.InputFilter : "native";
        panel.Children.Add(new TextBlock { Text = $"Intensity {effects.Intensity:0.##} · Tone {effects.Tone:0.##} · Structure {effects.Structure:0.##} · One pass\nInput {entry} · SR {effects.UpscaleFactor}× {(effects.UpscaleOrder == "before_nr" ? "before" : "after")} NR · Same frozen source · DOS aspect preserved\nFrame generation verified during the live run; a still image cannot show it.", FontSize = 14, Foreground = Brushes.LightSlateGray, Margin = new Thickness(24, 18, 24, 22) });
        panel.Measure(new Size(1344, double.PositiveInfinity)); panel.Arrange(new Rect(panel.DesiredSize)); CaptureElement(panel, path);
    }
}
