using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DosboxEnhanced;

public sealed class PhoenixProcessor : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr Create([MarshalAs(UnmanagedType.LPUTF8Str)] string config, IntPtr hwnd);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Destroy(IntPtr session);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Reconfigure(IntPtr session, [MarshalAs(UnmanagedType.LPUTF8Str)] string config);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Frame(IntPtr session, byte[] color, nuint capacity, uint width, uint height, [MarshalAs(UnmanagedType.LPUTF8Str)] string metadata, byte[] output, nuint outputCapacity);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr Error();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate nuint StatusFn(IntPtr session, IntPtr destination, nuint capacity);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
    readonly Thread worker;
    readonly AutoResetEvent wake = new(false);
    VideoFrame? pending;
    volatile bool stopping;
    public volatile bool Paused, MenuOpen, Background;
    public volatile bool SharpPixels;
    public EffectSettings Settings = new();
    ScreenshotPair? latestPair;
    public ScreenshotPair? LatestPair {
        get { var pair = Volatile.Read(ref latestPair); return pair?.Original.Epoch == Interlocked.Read(ref epoch) ? pair : null; }
    }
    public VideoFrame? Latest => LatestPair?.Enhanced;
    public string Status = "Graphics: waiting for DOS";
    public string UpscalingStatus = "NVIDIA Super Resolution: waiting for DOS";
    public string FrameGenerationStatus = "NVIDIA Frame Generation: waiting for DOS";
    public string RuntimeStatus = "{}";
    public long Completed;
    public double Milliseconds;
    long epoch, failedEpoch = -1;
    internal bool HasFailed => Interlocked.Read(ref failedEpoch) == Interlocked.Read(ref epoch);
    IntPtr window;
    public PhoenixProcessor()
    {
        worker = new Thread(Loop) { IsBackground = true, Name = "Native graphics" }; worker.Start();
    }
    public void AttachWindow(IntPtr hwnd) { Interlocked.Exchange(ref window, hwnd); Reset(); }
    public void Submit(VideoFrame frame) { Interlocked.Exchange(ref pending, frame with { Epoch = Interlocked.Read(ref epoch) }); wake.Set(); }
    public void Reset() { Interlocked.Increment(ref epoch); Volatile.Write(ref latestPair, null); }
    void Loop()
    {
        IntPtr library = IntPtr.Zero, session = IntPtr.Zero;
        Destroy? destroy = null;
        long sessionEpoch = -1;
        IntPtr sessionWindow = IntPtr.Zero;
        int lastWidth = 0, lastHeight = 0;
        GraphicsLayout? sessionLayout = null;
        try
        {
            library = LoadLibraryEx(Path.Combine(Paths.Runtime, "dosbox_nvngx.dll"), IntPtr.Zero, 0x1100);
            if (library == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            T Get<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
            var create = Get<Create>("db_graphics_create"); destroy = Get<Destroy>("db_graphics_destroy");
            var reconfigure = Get<Reconfigure>("db_graphics_reconfigure");
            var process = Get<Frame>("db_graphics_frame"); var error = Get<Error>("db_graphics_error"); var statusFn = Get<StatusFn>("db_graphics_status");
            var config = GraphicsConfiguration.Load(Paths.Root);
            while (!stopping)
            {
                wake.WaitOne(100);
                var source = Interlocked.Exchange(ref pending, null);
                if (source == null || source.Epoch != Interlocked.Read(ref epoch) || source.Epoch == failedEpoch) continue;
                var settings = Volatile.Read(ref Settings);
                try
                {
                    var currentWindow = Interlocked.CompareExchange(ref window, IntPtr.Zero, IntPtr.Zero);
                    var layout = GraphicsLayout.For(source.Width, source.Height, settings);
                    if (session == IntPtr.Zero || sessionWindow != currentWindow || sessionLayout != layout)
                    {
                        Status = "Preparing native graphics…";
                        var sessionConfig = JsonNode.Parse(config)!.AsObject();
                        sessionConfig["source_width"] = layout.SourceWidth; sessionConfig["source_height"] = layout.SourceHeight;
                        sessionConfig["input_factor"] = layout.InputFactor; sessionConfig["input_filter"] = layout.InputFilter;
                        sessionConfig["upscaling"] = layout.Upscaling; sessionConfig["upscale_factor"] = layout.Factor;
                        sessionConfig["upscale_order"] = layout.UpscaleFirst ? "before_nr" : "after_nr";
                        if (session != IntPtr.Zero && sessionWindow == currentWindow)
                        {
                            if (reconfigure(session, sessionConfig.ToJsonString()) != 0) throw new InvalidOperationException(Marshal.PtrToStringUTF8(error()));
                        }
                        else
                        {
                            if (session != IntPtr.Zero) { if (destroy(session) != 0) throw new InvalidOperationException(Marshal.PtrToStringUTF8(error())); session = IntPtr.Zero; }
                            session = create(sessionConfig.ToJsonString(), currentWindow);
                        }
                        if (session == IntPtr.Zero) throw new InvalidOperationException(Marshal.PtrToStringUTF8(error()));
                        sessionWindow = currentWindow;
                        sessionLayout = layout; lastWidth = lastHeight = 0;
                    }
                    if (sessionEpoch != source.Epoch) { sessionEpoch = source.Epoch; lastWidth = lastHeight = 0; }
                    var metadata = JsonSerializer.Serialize(new { settings.Enabled, settings.Intensity, settings.Tone, settings.Structure, settings.Style, settings.Compare,
                        settings.Passes, settings.Renderer, settings.Phoenix, settings.NvidiaUpscaling, settings.NvidiaFrameGeneration,
                        SharpPixels, paused = Paused, menu = MenuOpen, background = Background, reset = lastWidth != source.Width || lastHeight != source.Height });
                    var output = new byte[checked(layout.OutputWidth * layout.OutputHeight * 4)]; var clock = Stopwatch.StartNew();
                    if (process(session, source.Pixels, (nuint)source.Pixels.Length, (uint)source.Width, (uint)source.Height, metadata, output, (nuint)output.Length) != 0)
                        throw new InvalidOperationException(Marshal.PtrToStringUTF8(error()));
                    Milliseconds = clock.Elapsed.TotalMilliseconds;
                    var size = statusFn(session, IntPtr.Zero, 0); var ptr = Marshal.AllocHGlobal(checked((int)size));
                    try { statusFn(session, ptr, size); RuntimeStatus = Marshal.PtrToStringUTF8(ptr) ?? "{}"; }
                    finally { Marshal.FreeHGlobal(ptr); }
                    using var report = JsonDocument.Parse(RuntimeStatus); var stats = report.RootElement;
                    var name = settings.Renderer == "nvidia" ? "NVIDIA DLSS5" : "Phoenix DLSS5";
                    Status = settings.Enabled ? $"{name} active · {stats.GetProperty("neural_ms").GetDouble():F1} ms" : "Neural rendering off";
                    if(settings.Renderer=="phoenix"&&settings.Phoenix.Monitoring>0&&stats.TryGetProperty("phoenix",out var detail)) {
                        if(detail.TryGetProperty("complete_call_ms",out var call)) Status+=$" · SDK {call.GetDouble():F1} ms";
                        if(detail.TryGetProperty("gpu_processing_ms",out var gpu)&&gpu.GetDouble()>0) Status+=$" · sampled GPU {gpu.GetDouble():F1} ms";
                        if(detail.TryGetProperty("model_id",out var model)) Status+="\nModel · "+model.GetString();
                        if(detail.TryGetProperty("tuning",out var tuning)) Status+=$" · {tuning.GetProperty("neural_scale").GetDouble():0}% · {tuning.GetProperty("central_blocks").GetInt32()} block limit";
                    }
                    var entry = layout.InputWidth != layout.SourceWidth || layout.InputHeight != layout.SourceHeight ? $"Input {layout.SourceWidth}×{layout.SourceHeight} → {layout.InputWidth}×{layout.InputHeight}\n" : "";
                    UpscalingStatus = entry + (layout.Upscaling ? $"NVIDIA SR · {layout.InputWidth}×{layout.InputHeight} → {layout.OutputWidth}×{layout.OutputHeight} · {(layout.UpscaleFirst ? "before NR" : "after NR")}" : $"Processing · {layout.InputWidth}×{layout.InputHeight} · NVIDIA SR off");
                    var fg = stats.GetProperty("fg_active").GetBoolean(); var generated = stats.GetProperty("generated").GetInt64();
                    var reason = stats.GetProperty("fg_error").GetString();
                    FrameGenerationStatus = fg ? $"NVIDIA FG · {generated} generated · status {stats.GetProperty("frame_generation_status_bits").GetInt32()}" :
                        (settings.NvidiaFrameGeneration ? "NVIDIA FG suspended" + (Paused ? " · paused" : Background ? " · app in background" : MenuOpen ? " · menu / window changing" : string.IsNullOrWhiteSpace(reason) ? "" : " · " + reason) : "NVIDIA FG off");
                    if (source.Epoch == Interlocked.Read(ref epoch)) Volatile.Write(ref latestPair, new(source,
                        new(layout.OutputWidth, layout.OutputHeight, output, source.Aspect, source.Sequence, source.Epoch, source.Width, source.Height), settings, DateTimeOffset.UtcNow));
                    lastWidth = source.Width; lastHeight = source.Height; Interlocked.Increment(ref Completed);
                }
                catch (Exception e) { Status = "Graphics unavailable: " + e.Message; UpscalingStatus = FrameGenerationStatus = "Graphics stopped · original DOS image"; Interlocked.Exchange(ref failedEpoch, source.Epoch); Volatile.Write(ref latestPair, null); }
            }
        }
        catch (Exception e) { Status = "Graphics unavailable: " + e.Message; UpscalingStatus = FrameGenerationStatus = "Original DOS image"; }
        finally { if (session != IntPtr.Zero && destroy != null) { if (destroy(session) != 0) { Status = "Graphics teardown could not drain the device"; library = IntPtr.Zero; } } if (library != IntPtr.Zero) NativeLibrary.Free(library); }
    }
    public void Dispose() { stopping = true; wake.Set(); if (worker.Join(20000)) wake.Dispose(); }
}
