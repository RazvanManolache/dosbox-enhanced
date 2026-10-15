using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DosboxEnhanced;

public sealed partial class MainWindow
{
    // Developer capture workflow. Run only in a separate, disposable app folder.
    public async Task CaptureShowcase(string manifestPath)
    {
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        if (manifest["gallery"]?.GetValue<bool>() == true) { await CaptureGameGallery(manifest); return; }
        string output = manifest["output"]!.GetValue<string>(); Directory.CreateDirectory(output);
        var proof = new JsonObject { ["version"] = "0.5", ["controls"] = new JsonObject { ["intensity"] = 2.0, ["tone"] = 2.0, ["structure"] = 2.0, ["phoenixPasses"] = 1, ["upscaling"] = true, ["frameGeneration"] = true }, ["capturedUtc"] = DateTimeOffset.UtcNow.ToString("o"), ["games"] = new JsonArray() };
        var featured = manifest["games"]!.AsArray().First(g => g!["slug"]!.GetValue<string>() == (manifest["featuredSlug"]?.GetValue<string>() ?? manifest["games"]![0]!["slug"]!.GetValue<string>()))!;
        string featuredSlug = featured["slug"]!.GetValue<string>(), featuredTitle = featured["title"]!.GetValue<string>();
        Window? demo = null;
        try
        {
            if (library.Environments.Any(p => !p.Name.StartsWith("DOS workspace") && !p.Id.StartsWith("showcase-"))) throw new InvalidOperationException("Showcase captures require a fresh, disposable data folder.");
            Stop(); library.Environments.Clear();
            preferences.Audio = new(0); volume.Value = 0; Width = 1440; Height = 1040;
            foreach (var game in manifest["games"]!.AsArray())
                library.Environments.Add(new() { Id = "showcase-" + game!["slug"]!.GetValue<string>(), Name = game["environment"]!.GetValue<string>(), Startup = "C:\\" + game["exe"]!.GetValue<string>(),
                    Drives = [new() { Folder = game["folder"]!.GetValue<string>(), Label = "Games" }], Effects = new(Intensity: 2.0, Tone: 2.0, Structure: 2.0, Passes: 1),
                    DiscoverPrograms = false, Programs = [new() { Path = "C:\\" + game["exe"]!.GetValue<string>(), Name = game["title"]!.GetValue<string>(), Available = true }] });
            library.Save(); SetAllPanels(true); ((TabControl)deck.Children[0]).SelectedIndex = 0;
            if (manifest["pickerOnly"]?.GetValue<bool>() == true)
            {
                environments.SelectedIndex = 0; UpdateProgramPicker(); programPicker.Open(); await Task.Delay(200);
                if (System.Windows.PresentationSource.FromVisual(programPicker.Search)?.RootVisual is FrameworkElement pickerView) CaptureElement(pickerView, Path.Combine(output, "program-picker.png"));
                programPicker.Close(); proof["passed"] = true; return;
            }
            async Task WaitGraphics(int frames = 12)
            {
                long start = phoenix.Completed;
                for (int i = 0; i < 600 && phoenix.Completed < start + frames; i++) { await Task.Delay(50); if (phoenix.Status.StartsWith("Graphics unavailable")) throw new Exception(phoenix.Status); }
                if (phoenix.Completed < start + frames) throw new TimeoutException("Graphics did not complete enough frames: " + phoenix.Status);
            }
            foreach (var game in manifest["games"]!.AsArray())
            {
                string slug = game!["slug"]!.GetValue<string>(); var profile = library.Environments.Single(p => p.Id == "showcase-" + slug);
                environments.SelectedItem = profile; Launch(profile); FocusForValidation(); await Task.Delay(7000);
                foreach (var key in game["keys"]?.AsArray() ?? new JsonArray()) { core!.Key((uint)key!.GetValue<int>(), true); await Task.Delay(100); core.Key((uint)key.GetValue<int>(), false); await Task.Delay(1800); }
                FocusForValidation(); await WaitGraphics();
                // Runtime initialization can outlast initial activation. Require an actual
                // generated presentation after focus settles before recording evidence.
                for (int i = 0; i < 100; i++)
                {
                    FocusForValidation(); await Task.Delay(100);
                    var observedGraphics = JsonNode.Parse(phoenix.RuntimeStatus)!;
                    if (observedGraphics["generated"]?.GetValue<long>() > 0) break;
                }
                CaptureWindow(Path.Combine(output, slug + "-workspace.png"));
                if (manifest["cpuOnly"]?.GetValue<bool>() == true)
                {
                    autoCycles.IsChecked = false; cycles.Text = "20000"; CommitCpuControls();
                    await Task.Delay(300); FocusForValidation(); await WaitGraphics(12); Tick();
                    CaptureWindow(Path.Combine(output, "cpu-controls.png")); proof["passed"] = true; return;
                }
                var stats = JsonNode.Parse(phoenix.RuntimeStatus)!.AsObject();
                if (stats["sr_frames"]!.GetValue<long>() == 0 || stats["generated"]!.GetValue<long>() == 0) { proof["failedGraphics"] = stats; throw new Exception("Real SR and generated presentations are required for the showcase."); }
                ((JsonArray)proof["games"]!).Add(new JsonObject { ["game"] = game["title"]!.GetValue<string>(), ["sourceWidth"] = core!.Latest!.Width, ["sourceHeight"] = core.Latest.Height, ["graphics"] = stats });
                SaveFrame(core.Latest, Path.Combine(output, slug + "-original.png"));
                SaveFrame(phoenix.Latest!, Path.Combine(output, slug + "-enhanced.png"));
                if (slug == featuredSlug)
                {
                    if (manifest["pipelineValidation"]?.GetValue<bool>() == true)
                    {
                        var pipelines = new JsonArray();
                        foreach (int selectedRenderer in new[] { 0, 1 })
                        foreach (string order in new[] { "after_nr", "before_nr", "native" })
                        {
                            renderer.SelectedIndex = selectedRenderer; upscaleOrder.SelectedIndex = order == "before_nr" ? 1 : 0;
                            upscaleFactor.SelectedItem = 4; nvidiaUpscaling.IsChecked = order != "native";
                            FocusForValidation(); await WaitGraphics(16); Tick();
                            var actual = JsonNode.Parse(phoenix.RuntimeStatus)!.AsObject(); var frame = phoenix.Latest!;
                            int factor = order == "native" ? 1 : 4, width = core!.Latest!.Width, height = core.Latest.Height;
                            int neuralFactor = order == "before_nr" ? 4 : 1;
                            if (actual["input_width"]!.GetValue<int>() != width || actual["input_height"]!.GetValue<int>() != height || actual["neural_width"]!.GetValue<int>() != width * neuralFactor || actual["neural_height"]!.GetValue<int>() != height * neuralFactor || frame.Width != width * factor || frame.Height != height * factor || actual["pre_resize"]!.GetValue<bool>())
                                throw new Exception("Live graphics pipeline geometry did not match the selected controls.");
                            string mode = (selectedRenderer == 0 ? "phoenix-" : "nvidia-") + order;
                            pipelines.Add(new JsonObject { ["mode"] = mode, ["graphics"] = actual });
                            CaptureWindow(Path.Combine(output, "pipeline-" + mode + ".png"));
                        }
                        proof["pipelines"] = pipelines;
                        var inputPipelines = new JsonArray();
                        foreach (int selectedRenderer in new[] { 0, 1 })
                        foreach (int selectedOrder in new[] { 0, 1 })
                        foreach (int selectedFilter in new[] { 0, 1 })
                        {
                            renderer.SelectedIndex = selectedRenderer; upscaleOrder.SelectedIndex = selectedOrder;
                            inputResize.IsChecked = true; inputScale.Value = 1.25; inputFilter.SelectedIndex = selectedFilter; upscaleFactor.SelectedItem = 2; nvidiaUpscaling.IsChecked = true;
                            FocusForValidation(); await WaitGraphics(16); Tick();
                            var actual = JsonNode.Parse(phoenix.RuntimeStatus)!.AsObject(); var frame = phoenix.Latest!; int w = core!.Latest!.Width, h = core.Latest.Height;
                            int iw = GraphicsLayout.ScaleDimension(w, 1.25), ih = GraphicsLayout.ScaleDimension(h, 1.25);
                            int neuralFactor = selectedOrder == 1 ? 2 : 1;
                            if (actual["input_width"]!.GetValue<int>() != iw || actual["input_height"]!.GetValue<int>() != ih || actual["neural_width"]!.GetValue<int>() != iw * neuralFactor || actual["neural_height"]!.GetValue<int>() != ih * neuralFactor || frame.Width != iw * 2 || frame.Height != ih * 2 || !actual["pre_resize"]!.GetValue<bool>()) throw new Exception("Optional input resize geometry did not match the selected controls.");
                            string mode = (selectedRenderer == 0 ? "phoenix-" : "nvidia-") + (selectedOrder == 0 ? "after_nr-" : "before_nr-") + (selectedFilter == 0 ? "nearest" : "linear");
                            inputPipelines.Add(new JsonObject { ["mode"] = mode, ["graphics"] = actual });
                            CaptureWindow(Path.Combine(output, "input-" + mode + ".png"));
                        }
                        proof["inputPipelines"] = inputPipelines;
                        inputResize.IsChecked = false; upscaleFactor.SelectedItem = 4;
                        renderer.SelectedIndex = 0; upscaleOrder.SelectedIndex = 0; nvidiaUpscaling.IsChecked = true;
                        FocusForValidation(); await WaitGraphics(16); Tick();
                    }
                    core.Pause(true); await Task.Delay(150); var source = core.Latest!;
                    // Process one frozen source repeatedly so the comparison has identical content.
                    for (int i = 0; i < 16; i++) { phoenix.Submit(source); await Task.Delay(80); }
                    SaveComparison(source, phoenix.Latest!, Path.Combine(output, "dlss-comparison.png"), featuredTitle + " · same frozen DOS frame", phoenix.Status);
                    enabled.IsChecked = true; compare.IsChecked = true; await Task.Delay(300); phoenix.Submit(source); await Task.Delay(300);
                    CaptureWindow(Path.Combine(output, "dlss-controls.png")); compare.IsChecked = false;
                    renderer.SelectedIndex = 1; core.Pause(false); FocusForValidation(); await WaitGraphics();
                    CaptureWindow(Path.Combine(output, "nvidia-workspace.png")); proof["nvidia"] = JsonNode.Parse(phoenix.RuntimeStatus);
                    renderer.SelectedIndex = 0; await WaitGraphics();
                    programPicker.Open(); programPicker.Search.Text = ""; await Task.Delay(150);
                    if (System.Windows.PresentationSource.FromVisual(programPicker.Search)?.RootVisual is FrameworkElement pickerRoot) CaptureElement(pickerRoot, Path.Combine(output, "program-picker.png"));
                    programPicker.Close(); ShowDosboxSettings(); await Task.Delay(150); CaptureDialog(configurationWindow!, Path.Combine(output, "dosbox-settings.png")); configurationWindow!.Close();
                }
                Stop();
            }
            // Use the real authenticated HTTP endpoint, never direct tool-method calls.
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            var settings = new McpSettings { Enabled = true, AllowRemote = false, AllowControl = true, Port = port };
            await ConfigureMcp(settings); using var client = new HttpClient(new HttpClientHandler { UseProxy = false }); string session = ""; int id = 0;
            var actions = new List<(string Tool, string Result)>();
            async Task<JsonObject?> Rpc(string method, object? args = null, bool notification = false)
            {
                var rpc = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = JsonSerializer.SerializeToNode(args ?? new { }) }; if (!notification) rpc["id"] = ++id;
                using var request = new HttpRequestMessage(HttpMethod.Post, settings.Url) { Content = new StringContent(rpc.ToJsonString(), System.Text.Encoding.UTF8, "application/json") };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.Token); request.Headers.Accept.ParseAdd("application/json, text/event-stream"); request.Headers.Add("MCP-Protocol-Version", "2025-11-25");
                if (session.Length > 0) request.Headers.Add("Mcp-Session-Id", session);
                using var response = await client.SendAsync(request); response.EnsureSuccessStatusCode(); if (response.Headers.TryGetValues("Mcp-Session-Id", out var header)) session = header.First();
                string body = await response.Content.ReadAsStringAsync(); return body.Length == 0 ? null : JsonNode.Parse(body)!.AsObject();
            }
            async Task<JsonObject> Tool(string tool, object args, string summary)
            {
                var response = await Rpc("tools/call", new { name = tool, arguments = args }); var result = response!["result"]!.AsObject();
                if (result["isError"]?.GetValue<bool>() == true) throw new Exception("Showcase MCP tool failed: " + tool);
                actions.Add((tool, summary)); return result;
            }
            await Rpc("initialize", new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "release-showcase", version = "0.5" } }); await Rpc("notifications/initialized", notification: true);
            var tools = await Rpc("tools/list"); int count = tools!["result"]!["tools"]!.AsArray().Count;
            await Tool("dosbox_launch", new { environment_id = "showcase-" + featuredSlug }, "Launched " + featuredTitle + " in its saved environment"); await Task.Delay(4000);
            await Tool("dosbox_press_keys", new { keys = new[] { "enter" }, hold_ms = 80 }, "Sent Enter directly to the DOS session"); await Task.Delay(2000);
            await Tool("dosbox_effects", new { renderer = "phoenix", enabled = true, intensity = 2.0, tone = 2.0, structure = 2.0, passes = 1, upscaling = true, frame_generation = true }, "Max intensity, tone and structure; one pass; NVIDIA upscaling and FG");
            FocusForValidation(); await WaitGraphics();
            await Tool("dosbox_audio", new { volume = 35 }, "Set this app's volume to 35%");
            var observed = await Tool("dosbox_status", new { }, "Read the running environment and graphics status");
            var screenshot = await Tool("dosbox_screenshot", new { enhanced = true }, "Captured the enhanced DOS image as PNG");
            var png = Convert.FromBase64String(screenshot["content"]!.AsArray().Single(c => c?["type"]?.GetValue<string>() == "image")!["data"]!.GetValue<string>()); File.WriteAllBytes(Path.Combine(output, "mcp-game.png"), png);
            proof["mcp"] = new JsonObject { ["transport"] = "authenticated loopback HTTP", ["toolCount"] = count, ["actions"] = JsonSerializer.SerializeToNode(actions.Select(a => new { tool = a.Tool, result = a.Result })), ["graphics"] = observed["structuredContent"]?["graphics"]?.DeepClone() };
            var bodyPanel = new DockPanel { Margin = new Thickness(24) };
            var top = new StackPanel(); top.Children.Add(Label("A DOS workspace your AI can actually use", 26)); top.Children.Add(Label($"Live MCP workflow · {count} tools · local connections only", 15)); top.Children.Add(Label("These are recorded tool calls over authenticated HTTP, not a simulated AI conversation.")); DockPanel.SetDock(top, Dock.Top); bodyPanel.Children.Add(top);
            var log = new StackPanel { Width = 390, Margin = new Thickness(20, 20, 0, 0) }; DockPanel.SetDock(log, Dock.Right); bodyPanel.Children.Add(log);
            foreach (var action in actions) { log.Children.Add(new TextBlock { Text = "✓  " + action.Tool, Foreground = Brush(111, 223, 208), FontFamily = new FontFamily("Consolas"), FontSize = 14, Margin = new Thickness(0, 12, 0, 3) }); log.Children.Add(Label(action.Result)); }
            var bmp = new BitmapImage(); bmp.BeginInit(); bmp.StreamSource = new MemoryStream(png); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.EndInit();
            bodyPanel.Children.Add(new Image { Source = bmp, Stretch = Stretch.Uniform, Margin = new Thickness(0, 20, 0, 0) });
            demo = new Window { Title = "DOSBox Enhanced · MCP demonstration", Width = 1280, Height = 780, Background = Background, Foreground = Foreground, Resources = Resources, Content = bodyPanel, Owner = this }; demo.Show(); await Task.Delay(200); CaptureDialog(demo, Path.Combine(output, "mcp-session.png")); demo.Close(); demo = null;
            proof["passed"] = true;
        }
        catch (Exception e) { proof["passed"] = false; proof["error"] = e.ToString(); }
        finally { demo?.Close(); await ConfigureMcp(preferences.Mcp with { Enabled = false }); Paths.AtomicWrite(Path.Combine(output, "capture-proof.private.json"), proof.ToJsonString(Paths.Json)); Close(); }
    }
    static void SaveFrame(VideoFrame frame, string path)
    {
        var bitmap = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null, frame.Pixels, frame.Width * 4); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); encoder.Save(stream);
    }
    static void SaveComparison(VideoFrame before, VideoFrame after, string path, string heading, string timing)
    {
        var panel = new StackPanel { Width = 1344, Background = Brush(15, 20, 29), Margin = new Thickness(0) }; panel.Children.Add(new TextBlock { Text = heading + " · maximum strength, one pass", FontSize = 25, Foreground = Brushes.WhiteSmoke, Margin = new Thickness(24, 20, 24, 12) });
        var grid = new Grid { Margin = new Thickness(24, 0, 24, 0) }; grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new());
        foreach (var (frame, label, column) in new[] { (before, $"Original DOS · {before.Width} × {before.Height}", 0), (after, $"Phoenix + NVIDIA DLSS SR · {after.Width} × {after.Height}", 1) })
        {
            var stack = new StackPanel { Margin = new Thickness(0, 0, 12, 0) }; stack.Children.Add(new TextBlock { Text = label, Foreground = Brush(120, 221, 224), FontSize = 16, Margin = new Thickness(0, 0, 0, 10) });
            var picture = new Image { Source = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null, frame.Pixels, frame.Width * 4), Width = 632, Height = 395, Stretch = Stretch.Fill }; RenderOptions.SetBitmapScalingMode(picture, column == 0 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality); stack.Children.Add(picture); Grid.SetColumn(stack, column); grid.Children.Add(stack);
        }
        panel.Children.Add(grid); panel.Children.Add(new TextBlock { Text = "Intensity / tone / structure: 2.0 · Phoenix passes: 1 · Same frozen source; both images fitted to the same display size.\n" + timing + " · A still image cannot demonstrate frame generation.", Foreground = Brushes.LightSlateGray, FontSize = 14, Margin = new Thickness(24, 18, 24, 22) });
        panel.Measure(new Size(1344, double.PositiveInfinity)); panel.Arrange(new Rect(panel.DesiredSize)); CaptureElement(panel, path);
    }
}
