using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace DosboxEnhanced;

public static class App
{
    [STAThread]
    public static int Main(string[] args)
    {
        Paths.Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
        Directory.CreateDirectory(Paths.Data);
        if (args.Contains("--self-test")) return SelfTest.Run();
        if (args.Contains("--mcp-stdio")) return McpStdio.Run().GetAwaiter().GetResult();
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.DispatcherUnhandledException += (_, e) =>
        {
            File.AppendAllText(Path.Combine(Paths.Data, "errors.log"), DateTimeOffset.Now + " " + e.Exception + "\n");
            MessageBox.Show(e.Exception.Message, "DOSBox Enhanced"); e.Handled = true;
        };
        bool smoke = args.Contains("--smoke");
        var executable = args.Contains("--profile") || args.Contains("--showcase") ? null : args.FirstOrDefault(a => !a.StartsWith("--"));
        var window = new MainWindow(executable);
        if (args.Contains("--mouse-test")) window.Loaded += async (_, _) => await window.RunMouseValidation();
        if (args.Contains("--ui-test")) window.Loaded += async (_, _) => await window.RunUiValidation();
        if (args.Contains("--mcp-test")) window.Loaded += async (_, _) => await window.RunMcpValidation();
        if (args.Contains("--cpu-test")) window.Loaded += async (_, _) => await window.RunCpuValidation();
        if (args.Contains("--profile")) window.Loaded += (_, _) =>
        {
            var path = args.First(a => !a.StartsWith("--"));
            window.Launch(JsonSerializer.Deserialize<EnvironmentProfile>(File.ReadAllText(path))!);
        };
        if (args.Contains("--library")) window.Loaded += (_, _) => window.OpenEnvironmentBrowser();
        if (args.Contains("--showcase")) window.Loaded += async (_, _) => await window.CaptureShowcase(args[Array.IndexOf(args, "--showcase") + 1]);
        if (args.Contains("--mcp-panel")) window.Loaded += (_, _) => window.ShowMcpPanel();
        if (smoke)
        {
            window.Loaded += async (_, _) =>
            {
                var report = new Dictionary<string, object?>();
                try
                {
                    await Task.Delay(250); report["foreground_for_test"] = window.FocusForValidation();
                    await Task.Delay(5000);
                    var core = window.Core ?? throw new InvalidOperationException("Pass a DOS executable to --smoke.");
                    var initial = core.Latest ?? throw new InvalidOperationException(core.Error ?? "No DOS video frames received.");
                    var directory = Path.Combine(Paths.Root, "tests", "results"); Directory.CreateDirectory(directory);
                    report["source_width"] = initial.Width; report["source_height"] = initial.Height; report["first_sequence"] = initial.Sequence;
                    report["core_version"] = core.CoreVersion;
                    if (args.Contains("--controls-test"))
                    {
                        var controls = await window.ValidateAudioAndRenderer(); report["controls"] = controls;
                        if ((double)controls["half_volume_gain"] != .5 || (double)controls["muted_gain"] != 0 ||
                            !(bool)controls["nvidia_selection"] || !(bool)controls["nvidia_active"] || !(bool)controls["phoenix_active"] || !(bool)controls["nvidia_options_retained"] || !(bool)controls["phoenix_options_retained"])
                            throw new InvalidOperationException("Audio/provider control validation failed.");
                    }
                    if (args.Contains("--key-test"))
                    {
                        var pixels = initial.Pixels;
                        await window.TestNativeKeyboard();
                        report["keyboard_changed_frame"] = !pixels.SequenceEqual(core.Latest!.Pixels);
                    }
                    if (args.Contains("--graphics-test"))
                    {
                        // Provider setup can outlast initial activation. Observe real FG while focused before testing suspension.
                        for (int i = 0; i < 100; i++)
                        {
                            window.FocusForValidation(); await Task.Delay(100);
                            using var activeGpu = JsonDocument.Parse(window.Phoenix.RuntimeStatus);
                            if (activeGpu.RootElement.TryGetProperty("generated", out var generated) && generated.GetInt64() > 0) break;
                        }
                    }
                    core.Pause(true); await Task.Delay(100); long pauseSequence = core.Latest!.Sequence; await Task.Delay(250); report["pause_stable"] = core.Latest!.Sequence == pauseSequence;
                    if (args.Contains("--graphics-test")) { using var pauseGpu = JsonDocument.Parse(window.Phoenix.RuntimeStatus); report["fg_suspended_on_pause"] = !pauseGpu.RootElement.GetProperty("fg_active").GetBoolean(); }
                    var state = Path.Combine(directory, "smoke.state");
                    try { await core.SaveState(state); report["state_bytes"] = new FileInfo(state).Length; await core.LoadState(state); report["state_restore"] = true; }
                    catch (Exception e) { if (!args.Contains("--skip-state")) throw; report["state_skipped"] = e.Message; }
                    core.Pause(false);
                    await Task.Delay(1000); report["resume_advanced"] = core.Latest!.Sequence > pauseSequence;
                    window.CaptureWindow(Path.Combine(directory, "window.png")); window.Screenshot();
                    report["window_render"] = window.RenderDiagnostics();
                    window.ToggleFullscreen(); await Task.Delay(1000); window.CaptureWindow(Path.Combine(directory, "fullscreen.png")); report["fullscreen_render"] = window.RenderDiagnostics();
                    report["top_menu_before_second"] = window.TestTopPointer(400, 0, 0) || window.TestTopPointer(400, 0, .99);
                    report["top_menu_after_second"] = window.TestTopPointer(400, 0, 1);
                    await Task.Delay(250);
                    if (args.Contains("--graphics-test")) { using var menuGpu = JsonDocument.Parse(window.Phoenix.RuntimeStatus); report["fg_suspended_in_menu"] = !menuGpu.RootElement.GetProperty("fg_active").GetBoolean(); }
                    window.CaptureWindow(Path.Combine(directory, "fullscreen-menu.png")); window.ToggleFullscreen();
                    report["audio_frames"] = core.AudioFrames; report["last_sequence"] = core.Latest!.Sequence; report["core_error"] = core.Error;
                    report["phoenix_status"] = window.Phoenix.Status; report["phoenix_frames"] = window.Phoenix.Completed;
                    report["graphics_runtime"] = JsonSerializer.Deserialize<object>(window.Phoenix.RuntimeStatus);
                    if (args.Contains("--graphics-test"))
                    {
                        using var gpu = JsonDocument.Parse(window.Phoenix.RuntimeStatus);
                        if (window.Phoenix.Completed == 0 || gpu.RootElement.GetProperty("sr_frames").GetInt64() == 0 ||
                            gpu.RootElement.GetProperty("generated").GetInt64() == 0 || gpu.RootElement.GetProperty("frame_generation_status_bits").GetInt32() != 0 ||
                            !(bool)report["fg_suspended_on_pause"]! || !(bool)report["fg_suspended_in_menu"]!)
                            throw new InvalidOperationException("Neural rendering, NVIDIA SR and generated presentations must all be observed: " + window.Phoenix.Status);
                    }
                    report["status"] = window.CurrentStatus; report["passed"] = core.Error == null && (bool)report["pause_stable"]! && (bool)report["resume_advanced"]! && !(bool)report["top_menu_before_second"]! && (bool)report["top_menu_after_second"]! && (!args.Contains("--key-test") || (bool)report["keyboard_changed_frame"]!);
                }
                catch (Exception e) { report["passed"] = false; report["error"] = e.ToString(); }
                var output = Path.Combine(Paths.Root, "tests", "results", "smoke.json"); Paths.AtomicWrite(output, JsonSerializer.Serialize(report, Paths.Json));
                window.Close();
            };
        }
        app.Run(window);
        if (args.Contains("--mouse-test")) return File.ReadAllText(Path.Combine(Paths.Root,"tests","results","mouse-validation.json")).Contains("\"passed\": true") ? 0 : 1;
        if (args.Contains("--ui-test")) return File.ReadAllText(Path.Combine(Paths.Root,"tests","results","ui-validation.json")).Contains("\"passed\": true") ? 0 : 1;
        if (args.Contains("--mcp-test")) return File.ReadAllText(Path.Combine(Paths.Root,"tests","results","mcp-integration.json")).Contains("\"passed\": true") ? 0 : 1;
        if (args.Contains("--cpu-test")) return File.ReadAllText(Path.Combine(Paths.Root,"tests","results","cpu-validation.json")).Contains("\"passed\": true") ? 0 : 1;
        return smoke && File.Exists(Path.Combine(Paths.Root, "tests", "results", "smoke.json")) && File.ReadAllText(Path.Combine(Paths.Root, "tests", "results", "smoke.json")).Contains("\"passed\": true") ? 0 : smoke ? 1 : 0;
    }
}
public static class SelfTest
{
    public static int Run()
    {
        var results = new List<string>();
        void Check(bool condition, string name) { if (!condition) throw new Exception(name); results.Add(name); }
        void Reject(Action action, string name) { try { action(); } catch (ArgumentException) { results.Add(name); return; } throw new Exception(name); }
        try
        {
            var dwell = new TopEdgeDwell();
            Check(!dwell.Update(200, 0, 0, true), "dwell starts hidden");
            Check(!dwell.Update(200, 0, .99, true), "dwell before one second stays hidden");
            Check(dwell.Update(200, 0, 1, true), "dwell reveals at one second");
            Check(!dwell.Update(250, 0, 1.1, true), "motion restarts dwell");
            Check(!dwell.Update(250, 20, 2.2, false), "leaving top cancels dwell");
            Check(!dwell.Update(250, 0, 2.3, true), "returning top starts fresh dwell");
            var folder = Path.Combine(Paths.Root, "tests", "fixtures"); Directory.CreateDirectory(folder);
            var profile = new EnvironmentProfile { Drives = [new() { Folder = folder }, new() { Letter = "D", Folder = folder }], Startup = "D:\\SUB\\TEST.BAT" };
            var conf = Configuration.Generate(profile);
            Check(conf.Contains("mount C ") && conf.Contains("mount D "), "multiple folders mapped independently");
            Check(conf.Contains("cd \\SUB") && conf.Contains("TEST.BAT"), "startup changes to executable directory");
            profile.Drives[1].Letter = "C"; Reject(() => Configuration.Generate(profile), "duplicate drives rejected"); profile.Drives[1].Letter = "D";
            profile.Drives[1].Letter = "Z"; Reject(() => Configuration.Generate(profile), "reserved DOS drive rejected"); profile.Drives[1].Letter = "D";
            profile.Cycles = "auto\nexit"; Reject(() => Configuration.Generate(profile), "CPU configuration injection rejected"); profile.Cycles = "auto";
            profile.Startup = "C:\\GAME.EXE\nexit"; Reject(() => Configuration.Generate(profile), "startup line injection rejected"); profile.Startup = "D:\\SUB\\TEST.BAT";
            File.WriteAllText(Path.Combine(folder, "TEST.BAT"), "@echo off\r\necho DOSBOX ENHANCED TEST\r\npause\r\n");
            var quick = Configuration.Quick(Path.Combine(folder, "TEST.BAT")); Check(quick.Startup == "C:\\TEST.BAT", "quick runner mounts executable parent");
            var duplicate = JsonSerializer.Deserialize<EnvironmentProfile>(JsonSerializer.Serialize(profile))!;
            duplicate.Drives[0].Folder = "different"; Check(profile.Drives[0].Folder == folder, "profile copies have independent drive mappings");
            Check(MainWindow.MapKey(System.Windows.Input.Key.Left) == 276 && MainWindow.MapKey(System.Windows.Input.Key.A) == 97, "DOS navigation and letter key codes");
            short[] originalPcm = [-32768, -20000, 0, 20000, 32767];
            var halfPcm = (short[])originalPcm.Clone(); WaveAudio.ApplyGain(halfPcm, new(50));
            Check(halfPcm.SequenceEqual(new short[] { -16384, -10000, 0, 10000, 16384 }), "volume scales positive and negative stereo PCM");
            var mutePcm = (short[])originalPcm.Clone(); WaveAudio.ApplyGain(mutePcm, new(80, true)); Check(mutePcm.All(v => v == 0), "mute silences PCM independently of volume");
            var zeroPcm = (short[])originalPcm.Clone(); WaveAudio.ApplyGain(zeroPcm, new(0)); Check(zeroPcm.All(v => v == 0), "zero volume silences PCM");
            var fullPcm = (short[])originalPcm.Clone(); WaveAudio.ApplyGain(fullPcm, new(100)); Check(fullPcm.SequenceEqual(originalPcm), "full volume retains exact PCM");
            var effects = new EffectSettings(Renderer: "nvidia", NvidiaUpscaling: true, NvidiaFrameGeneration: true);
            var restoredEffects = JsonSerializer.Deserialize<EffectSettings>(JsonSerializer.Serialize(effects))!;
            Check(restoredEffects.Renderer == "nvidia" && restoredEffects.NvidiaUpscaling && restoredEffects.NvidiaFrameGeneration, "NVIDIA renderer preserves independent NVIDIA SR and FG preferences");
            Check((restoredEffects with { Renderer = "phoenix" }).NvidiaUpscaling && (restoredEffects with { Renderer = "phoenix" }).NvidiaFrameGeneration, "renderer switching does not change NVIDIA SR or FG preferences");
            var legacyEffects = JsonSerializer.Deserialize<EffectSettings>("{\"Enabled\":true,\"Intensity\":0.65}")!;
            Check(legacyEffects.Renderer == "phoenix" && legacyEffects.NvidiaUpscaling && legacyEffects.NvidiaFrameGeneration, "existing environment profiles receive compatible renderer defaults");
            var native = GraphicsLayout.For(320, 200, new(NvidiaUpscaling: false));
            Check(native.NeuralWidth == 320 && native.NeuralHeight == 200 && native.OutputWidth == 320 && native.OutputHeight == 200, "SR off keeps native input, neural processing and output dimensions");
            var after = GraphicsLayout.For(320, 200, new(UpscaleFactor: 4));
            Check(after.NeuralWidth == 320 && after.NeuralHeight == 200 && after.OutputWidth == 1280 && after.OutputHeight == 800 && !after.UpscaleFirst, "NR-first processes native pixels before four-times SR");
            var beforeSettings = new EffectSettings(UpscaleOrder: "before_nr", UpscaleFactor: 3);
            var before = GraphicsLayout.For(640, 480, beforeSettings);
            Check(before.InputWidth == 640 && before.NeuralWidth == 1920 && before.NeuralHeight == 1440 && before.UpscaleFirst, "SR-first processes NR at the upscaled dimensions without a fixed canvas");
            Check(JsonSerializer.Deserialize<EffectSettings>(JsonSerializer.Serialize(beforeSettings)) == beforeSettings && legacyEffects.UpscaleOrder == "after_nr" && legacyEffects.UpscaleFactor == 4, "processing order and factor persist with compatible defaults for old profiles");
            var entry = GraphicsLayout.For(320, 200, new(InputResize: true, InputScale: 2, NvidiaUpscaling: false));
            Check(entry.SourceWidth == 320 && entry.InputWidth == 640 && entry.InputHeight == 400 && entry.OutputWidth == 640 && entry.OutputHeight == 400, "optional input resize works independently of NVIDIA SR");
            var combined = GraphicsLayout.For(320, 200, new(InputResize: true, InputScale: 2, UpscaleFactor: 2, UpscaleOrder: "before_nr"));
            Check(combined.SourceWidth == 320 && combined.InputWidth == 640 && combined.NeuralWidth == 1280 && combined.OutputHeight == 800 && !legacyEffects.InputResize, "input resize composes with either NR/SR order and defaults off for existing profiles");
            var fractional = new EffectSettings(InputResize: true, InputScale: 1.5, UpscaleFactor: 2);
            var rounded = GraphicsLayout.For(321, 201, fractional);
            Check(rounded.InputWidth == 482 && rounded.InputHeight == 302 && rounded.OutputWidth == 964 && rounded.OutputHeight == 604, "fractional resize rounds half pixels up before applying the independent SR multiplier");
            var smaller = GraphicsLayout.For(320, 200, fractional with { InputScale = .25, NvidiaUpscaling = false });
            Check(smaller.InputWidth == 80 && smaller.InputHeight == 50 && GraphicsLayout.For(1, 1, fractional with { InputScale = .25, NvidiaUpscaling = false }).InputWidth == 1, "fractional downscaling retains at least one pixel per dimension");
            Check(JsonSerializer.Deserialize<EffectSettings>(JsonSerializer.Serialize(fractional)) == fractional && JsonSerializer.Deserialize<EffectSettings>("{\"InputResize\":true,\"InputScale\":3}")!.InputScale == 3, "fractional scales persist and old integer scales remain compatible");
            Reject(() => GraphicsLayout.For(320, 200, fractional with { InputScale = double.NaN }), "nonfinite input scales are rejected");
            Reject(() => GraphicsLayout.For(320, 200, fractional with { InputScale = .1 }), "out of range input scales are rejected");
            var cpuProfile = new EnvironmentProfile { Cycles = "12345" };
            var fixedConfig = Configuration.Generate(cpuProfile); var liveConfig = Configuration.Generate(cpuProfile, liveCpu: true);
            Check(fixedConfig.Contains("cycles=12345") && !liveConfig.Contains("cycles=12345") && liveConfig.Contains("Host CPU cycles: 12345"), "live CPU runtime configuration avoids locking cycles while standard export preserves them");
            var advanced = new EnvironmentProfile { DosboxSettings = DosboxIni.Parse("[CPU]\ncore=normal\ncycles=max\n[mixer]\nrate=22050\n[libretro]\ndosbox_pure_force60fps=false") };
            var advancedIni = Configuration.Generate(advanced);
            Check(advancedIni.Contains("core=normal") && advancedIni.Contains("cycles=max") && advancedIni.Contains("rate=22050"), "all supported INI sections and keys have general overrides");
            Check(!advancedIni.Contains("[libretro]") && advanced.DosboxSettings["libretro"]["dosbox_pure_force60fps"] == "false", "DOSBox Pure frontend options route separately from DOS INI");
            Reject(() => DosboxIni.Parse("[cpu]\ncore=normal\n[autoexec]\nexit"), "advanced settings reject injected autoexec commands");
            Reject(() => DosboxIni.Set(advanced.DosboxSettings,"cpu","cycles","auto\nexit"), "advanced setting values cannot inject lines");
            Check(!new McpSettings().Enabled && !new McpSettings().AllowRemote, "MCP is disabled and local-only by default");
            Check(McpTools.List(false).Length == 8 && McpTools.List(true).Length == 23, "MCP observation hides every control tool");
            var folderRoot = Path.Combine(folder, "folders-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folderRoot);
            var gamesFolder = DriveFolders.Create(folderRoot, folderRoot, "GAMES");
            var gameFolder = DriveFolders.Create(folderRoot, gamesFolder, "DOOM");
            Check(Directory.Exists(gameFolder) && Path.GetRelativePath(folderRoot, gameFolder) == "GAMES\\DOOM", "nested drive folders persist on disk");
            Reject(() => DriveFolders.Create(folderRoot, gamesFolder, "DOOM"), "existing folders are not overwritten");
            Reject(() => DriveFolders.Create(folderRoot, gamesFolder, "..\\ESCAPE"), "folder names cannot escape their parent");
            Reject(() => DriveFolders.Create(folderRoot, folder, "OUTSIDE"), "folder creation stays inside the mapped drive");
            Directory.Delete(gameFolder); Directory.Delete(gamesFolder); Directory.Delete(folderRoot);
            Paths.AtomicWrite(Path.Combine(Paths.Root, "tests", "results", "self-test.json"), JsonSerializer.Serialize(new { passed = true, count = results.Count, checks = results }, Paths.Json)); return 0;
        }
        catch (Exception e) { Paths.AtomicWrite(Path.Combine(Paths.Root, "tests", "results", "self-test.json"), JsonSerializer.Serialize(new { passed = false, error = e.ToString(), checks = results }, Paths.Json)); return 1; }
    }
}

