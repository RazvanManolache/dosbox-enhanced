using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace DosboxEnhanced;

public sealed class DosCore : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate byte Env(uint command, IntPtr data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void Video(IntPtr data, uint width, uint height, nuint pitch);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void Sample(short left, short right);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate nuint Batch(IntPtr data, nuint frames);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void Poll();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate short Input(uint port, uint device, uint index, uint id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void Keyboard(byte down, uint key, uint character, ushort modifiers);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void Setter(IntPtr callback);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void VoidCall();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate byte Load(ref GameInfo game);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void GetAv(out AvInfo info);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void GetSystem(out SystemInfo info);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate nuint SizeCall();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate byte Serialize(IntPtr data, nuint size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr MemoryData(uint id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate nuint MemorySize(uint id);
    [StructLayout(LayoutKind.Sequential)] struct GameInfo { public IntPtr path, data; public nuint size; public IntPtr meta; }
    [StructLayout(LayoutKind.Sequential)] struct Geometry { public uint width, height, maxWidth, maxHeight; public float aspect; }
    [StructLayout(LayoutKind.Sequential)] struct Timing { public double fps, sampleRate; }
    [StructLayout(LayoutKind.Sequential)] struct AvInfo { public Geometry geometry; public Timing timing; }
    [StructLayout(LayoutKind.Sequential)] struct SystemInfo { public IntPtr name, version, extensions; public byte fullpath, blockExtract; }
    readonly ConcurrentQueue<Action> commands = new();
    readonly ConcurrentDictionary<uint, bool> keys = new();
    readonly Dictionary<string, IntPtr> strings = [];
    readonly Dictionary<string, string> options = new()
    {
        ["dosbox_pure_force60fps"] = "true", ["dosbox_pure_conf"] = "outside",
        ["dosbox_pure_menu_time"] = "99", ["dosbox_pure_mouse_wheel"] = "79/82",
        ["dosbox_pure_mouse_input"] = "virtual", ["dosbox_pure_aspect_correction"] = "true",
        ["dosbox_pure_auto_mapping"] = "false", ["dosbox_pure_latency"] = "default"
    };
    readonly Thread thread;
    readonly Env environment;
    readonly Video video;
    readonly Sample sample;
    readonly Batch batch;
    readonly Poll poll;
    readonly Input input;
    Keyboard? keyboard;
    IntPtr module;
    VoidCall? run, unload, deinit;
    volatile bool stopping, paused;
    int pixelFormat;
    double aspect = 4.0 / 3, fps = 60;
    long sequence;
    int dx, dy, wheel, mouseButtons;
    bool loaded, ready;
    readonly string config;
    readonly string saveDirectory;
    WaveAudio? audio;
    AudioSettings audioSettings = new();
    public VideoFrame? Latest;
    public string Status = "Starting DOS…";
    public string? Error;
    public bool IsReady => ready;
    public bool IsPaused => paused;
    public long AudioFrames;
    public double ActualAudioGain => audio == null ? double.NaN : Volatile.Read(ref audio.LastGain);
    internal bool AudioOutputAvailable => audio?.OutputAvailable == true;
    public string CoreVersion = "";
    public long MemoryBytes;
    public readonly ConcurrentDictionary<string,string> OptionDefinitions = new();
    public Action<string>? OnMessage;
    T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(module, name));
    IntPtr Text(string value)
    {
        if (!strings.TryGetValue(value, out var pointer)) strings[value] = pointer = Marshal.StringToCoTaskMemUTF8(value);
        return pointer;
    }
    bool variablesDirty;
    public bool LiveCpuCycles { get; }
    public string RequestedCpuCycles = "";
    public long CpuCycleUpdates;
    public DosCore(string configPath, string savePath, bool sound = true, Dictionary<string,string>? coreOptions = null, string? initialCycles = null)
    {
        config = configPath; saveDirectory = savePath;
        if(coreOptions!=null)foreach(var setting in coreOptions) { if(setting.Key=="dosbox_pure_conf" && setting.Value!="outside")throw new ArgumentException("The host manages dosbox_pure_conf=outside to load environment settings.");options[setting.Key]=setting.Value; }
        LiveCpuCycles = initialCycles != null && CpuCycles.IsSimple(initialCycles);
        if (LiveCpuCycles) SetCycleOptions(initialCycles!);
        Directory.CreateDirectory(saveDirectory);
        environment = EnvironmentCallback; video = VideoCallback; sample = (l, r) => audio?.Write([l, r]);
        batch = (data, frames) => { AudioFrames += (long)frames; audio?.Write(data, checked((int)frames)); return frames; };
        poll = () => { };
        input = (_, device, _, id) =>
        {
            if (paused) return 0;
            if (device == 3) return (short)(keys.ContainsKey(id) ? 1 : 0);
            if (device != 2) return 0;
            return id switch { 0 => (short)Math.Clamp(Interlocked.Exchange(ref dx, 0), short.MinValue, short.MaxValue),
                1 => (short)Math.Clamp(Interlocked.Exchange(ref dy, 0), short.MinValue, short.MaxValue),
                2 => (short)(mouseButtons & 1), 3 => (short)((mouseButtons >> 1) & 1),
                4 => (short)(Interlocked.CompareExchange(ref wheel, 0, 1) == 1 ? 1 : 0),
                5 => (short)(Interlocked.CompareExchange(ref wheel, 0, -1) == -1 ? 1 : 0), 6 => (short)((mouseButtons >> 2) & 1), _ => 0 };
        };
        thread = new Thread(() => Loop(sound)) { IsBackground = true, Name = "DOS emulation" };
        thread.Start();
    }
    byte EnvironmentCallback(uint command, IntPtr data)
    {
        try
        {
            switch (command)
            {
                case 1: return 1; // rotation=0 only
                case 3: Marshal.WriteByte(data, 0); return 1; // overscan
                case 6: Marshal.WriteByte(data, 1); return 1; // duplicate frames
                case 7: Status = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(data)) ?? Status; return 1;
                case 8: stopping = true; return 1;
                case 9: Marshal.WriteIntPtr(data, Text(Path.Combine(Paths.Root, "system"))); return 1;
                case 10: pixelFormat = Marshal.ReadInt32(data); return (byte)(pixelFormat <= 2 ? 1 : 0);
                case 12: keyboard = Marshal.GetDelegateForFunctionPointer<Keyboard>(Marshal.ReadIntPtr(data)); return 1;
                case 15:
                    var key = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(data))!;
                    if (!options.TryGetValue(key, out var value)) return 0;
                    Marshal.WriteIntPtr(data, IntPtr.Size, Text(value)); return 1;
                case 16:
                    for (var p = data; Marshal.ReadIntPtr(p) != IntPtr.Zero; p += IntPtr.Size * 2)
                    {
                        var name = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(p))!;
                        var definition = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(p, IntPtr.Size))!;
                        OptionDefinitions[name] = definition;
                        if (!options.ContainsKey(name)) options[name] = definition[(definition.IndexOf(';') + 1)..].Trim().Split('|')[0];
                    }
                    return 1;
                case 17: Marshal.WriteByte(data, variablesDirty ? (byte)1 : (byte)0); if (variablesDirty) Interlocked.Increment(ref CpuCycleUpdates); variablesDirty = false; return 1;
                case 18: return 1;
                case 31: Marshal.WriteIntPtr(data, Text(saveDirectory)); return 1;
                case 32:
                    var info = Marshal.PtrToStructure<AvInfo>(data); ApplyAv(info); return 1;
                case 37:
                    var geo = Marshal.PtrToStructure<Geometry>(data); if (geo.aspect > 0) aspect = geo.aspect; return 1;
                case 39: return 0; // optional hardware renderer is deliberately unsupported
                case 52: Marshal.WriteInt32(data, 0); return 1; // request stable legacy options
                case 59: Marshal.WriteInt32(data, 0); return 1; // English
                case 65587: return 1; // input bitmask support
                default: return 0;
            }
        }
        catch (Exception e) { Error = e.Message; return 0; }
    }
    void ApplyAv(AvInfo info)
    {
        if (info.geometry.aspect > 0) aspect = info.geometry.aspect;
        if (info.timing.fps > 0 && info.timing.fps < 1000) fps = info.timing.fps;
    }
    unsafe void VideoCallback(IntPtr data, uint width, uint height, nuint pitch)
    {
        if (data == IntPtr.Zero || data == new IntPtr(-1) || width == 0 || height == 0 || width > 4096 || height > 4096) return;
        var pixels = new byte[checked((int)(width * height * 4))];
        fixed (byte* dst = pixels)
        {
            for (int y = 0; y < height; y++)
            {
                byte* src = (byte*)data + (nuint)y * pitch;
                for (int x = 0; x < width; x++)
                {
                    int offset = (y * (int)width + x) * 4;
                    if (pixelFormat == 1) { dst[offset] = src[x * 4]; dst[offset + 1] = src[x * 4 + 1]; dst[offset + 2] = src[x * 4 + 2]; }
                    else
                    {
                        ushort v = ((ushort*)src)[x];
                        dst[offset] = (byte)((v & 31) * 255 / 31);
                        dst[offset + 1] = (byte)(((v >> 5) & (pixelFormat == 2 ? 63 : 31)) * 255 / (pixelFormat == 2 ? 63 : 31));
                        dst[offset + 2] = (byte)(((v >> (pixelFormat == 2 ? 11 : 10)) & 31) * 255 / 31);
                    }
                    dst[offset + 3] = 255;
                }
            }
        }
        Volatile.Write(ref Latest, new((int)width, (int)height, pixels, aspect, ++sequence));
    }
    void Loop(bool sound)
    {
        try
        {
            Directory.CreateDirectory(Path.Combine(Paths.Root, "system"));
            module = NativeLibrary.Load(Path.Combine(Paths.Runtime, "dosbox_pure_libretro.dll"));
            Export<GetSystem>("retro_get_system_info")(out var systemInfo);
            CoreVersion = (Marshal.PtrToStringUTF8(systemInfo.name) ?? "DOSBox") + " " + Marshal.PtrToStringUTF8(systemInfo.version);
            Export<Setter>("retro_set_environment")(Marshal.GetFunctionPointerForDelegate(environment));
            Export<Setter>("retro_set_video_refresh")(Marshal.GetFunctionPointerForDelegate(video));
            Export<Setter>("retro_set_audio_sample")(Marshal.GetFunctionPointerForDelegate(sample));
            Export<Setter>("retro_set_audio_sample_batch")(Marshal.GetFunctionPointerForDelegate(batch));
            Export<Setter>("retro_set_input_poll")(Marshal.GetFunctionPointerForDelegate(poll));
            Export<Setter>("retro_set_input_state")(Marshal.GetFunctionPointerForDelegate(input));
            run = Export<VoidCall>("retro_run"); unload = Export<VoidCall>("retro_unload_game"); deinit = Export<VoidCall>("retro_deinit");
            Export<VoidCall>("retro_init")();
            var game = new GameInfo { path = Text(config) };
            if (Export<Load>("retro_load_game")(ref game) == 0) throw new InvalidOperationException("DOSBox could not load the environment.");
            loaded = true;
            if (NativeLibrary.TryGetExport(module, "retro_get_memory_size", out var memorySize)) MemoryBytes = checked((long)Marshal.GetDelegateForFunctionPointer<MemorySize>(memorySize)(2));
            Export<GetAv>("retro_get_system_av_info")(out var info); ApplyAv(info);
            if (sound) { audio = new WaveAudio((int)info.timing.sampleRate, Volatile.Read(ref audioSettings)); audio.Settings = Volatile.Read(ref audioSettings); }
            ready = true; Status = "DOS running";
            var clock = Stopwatch.StartNew(); var next = clock.Elapsed.TotalSeconds;
            while (!stopping)
            {
                while (commands.TryDequeue(out var action)) { try { action(); } catch (Exception e) { OnMessage?.Invoke(e.Message); } }
                if (paused) { Thread.Sleep(10); next = clock.Elapsed.TotalSeconds; continue; }
                run(); next += 1 / fps;
                var wait = next - clock.Elapsed.TotalSeconds;
                if (wait > .002) Thread.Sleep((int)(wait * 1000));
                if (wait < -.1) next = clock.Elapsed.TotalSeconds;
            }
        }
        catch (Exception e) { Error = e.ToString(); Status = "DOS stopped: " + e.Message; }
        finally
        {
            ready = false;
            if (loaded) unload?.Invoke();
            deinit?.Invoke(); audio?.Dispose();
            if (module != IntPtr.Zero) NativeLibrary.Free(module);
            foreach (var p in strings.Values) Marshal.FreeCoTaskMem(p);
        }
    }
    public void Key(uint key, bool down, uint character = 0, ushort modifiers = 0) => commands.Enqueue(() =>
    {
        if (down) keys[key] = true; else keys.TryRemove(key, out _);
        keyboard?.Invoke((byte)(down ? 1 : 0), key, character, modifiers);
    });
    public void ReleaseKeys() => commands.Enqueue(() => { foreach (var key in keys.Keys) keyboard?.Invoke(0, key, 0, 0); keys.Clear(); mouseButtons = 0; dx = dy = wheel = 0; });
    public void MouseMove(int x, int y) { Interlocked.Add(ref dx, x); Interlocked.Add(ref dy, y); }
    public void MouseButton(int mask, bool down) { if (down) Interlocked.Or(ref mouseButtons, mask); else Interlocked.And(ref mouseButtons, ~mask); }
    public void MouseWheel(int direction) => Interlocked.Exchange(ref wheel, direction);
    public void Pause(bool value) { ReleaseKeys(); paused = value; audio?.Clear(); }
    public void SetAudio(AudioSettings settings) { Volatile.Write(ref audioSettings, settings); if (audio != null) Volatile.Write(ref audio.Settings, settings); }
    public void Reset() => commands.Enqueue(() => Export<VoidCall>("retro_reset")());
    void SetCycleOptions(string value)
    {
        options["dosbox_pure_cycles"] = value; options["dosbox_pure_cycles_scale"] = "1.0"; options["dosbox_pure_cycles_max"] = "none";
        Volatile.Write(ref RequestedCpuCycles, value);
    }
    public Task SetCpuCycles(string value)
    {
        if (!LiveCpuCycles || !CpuCycles.IsSimple(value)) throw new InvalidOperationException("Reload DOS to replace the advanced CPU setting.");
        return Execute(() => { SetCycleOptions(value); variablesDirty = true; });
    }
    public Task SaveState(string path) => Execute(() =>
    {
        var size = Export<SizeCall>("retro_serialize_size")();
        if (size == 0 || size > 256 * 1024 * 1024) throw new InvalidOperationException("This session cannot save a state.");
        var memory = Marshal.AllocHGlobal(checked((int)size));
        try
        {
            if (Export<Serialize>("retro_serialize")(memory, size) == 0) throw new InvalidOperationException("DOSBox could not save a state here. Try during a running game; DOS shell and BAT pause screens may not support states.");
            var bytes = new byte[(int)size]; Marshal.Copy(memory, bytes, 0, bytes.Length);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path + ".tmp", bytes); File.Move(path + ".tmp", path, true);
        }
        finally { Marshal.FreeHGlobal(memory); }
    });
    public Task LoadState(string path) => Execute(() =>
    {
        var bytes = File.ReadAllBytes(path); var p = Marshal.AllocHGlobal(bytes.Length);
        try { Marshal.Copy(bytes, 0, p, bytes.Length); if (Export<Serialize>("retro_unserialize")(p, (nuint)bytes.Length) == 0) throw new InvalidOperationException("State does not match this environment/core configuration."); audio?.Clear(); }
        finally { Marshal.FreeHGlobal(p); }
    });
    Task Execute(Action action)
    {
        if (!ready) return Task.FromException(new InvalidOperationException("DOS is not running."));
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        commands.Enqueue(() => { try { action(); result.SetResult(); } catch (Exception e) { result.SetException(e); } });
        return result.Task;
    }
    public async Task<byte[]> ReadMemory(int offset, int count, CancellationToken cancellation)
    {
        if (offset < 0 || count < 1 || count > 4096 || (long)offset + count > MemoryBytes) throw new ArgumentException("Choose an available RAM range of 1–4096 bytes.");
        byte[] bytes = new byte[count];
        await Execute(() => { var memory = Export<MemoryData>("retro_get_memory_data")(2); if (memory == IntPtr.Zero) throw new InvalidOperationException("This core does not expose RAM."); Marshal.Copy(memory + offset, bytes, 0, count); }).WaitAsync(TimeSpan.FromSeconds(10), cancellation);
        return bytes;
    }
    public async Task<object> ReadText(CancellationToken cancellation)
    {
        if (MemoryBytes < 0xc0000) throw new InvalidOperationException("This core does not expose text memory. Use dosbox_screenshot.");
        var bios = await ReadMemory(0x400, 256, cancellation); int mode = bios[0x49];
        if (mode is not (2 or 3 or 7)) return new { textMode = false, videoMode = mode, message = "Graphics mode: use dosbox_screenshot to read the screen." };
        int columns = Math.Clamp((int)BitConverter.ToUInt16(bios, 0x4a), 1, 160), rows = Math.Clamp(bios[0x84] + 1, 1, 100);
        int address = (mode == 7 ? 0xb0000 : 0xb8000) + BitConverter.ToUInt16(bios, 0x4e);
        var characters = new List<byte>(); int length = columns * rows * 2;
        for (int offset = 0; offset < length; offset += 4096) characters.AddRange(await ReadMemory(address + offset, Math.Min(4096, length-offset), cancellation));
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var encoding = System.Text.Encoding.GetEncoding(437); var lines = new List<string>();
        for (int y = 0; y < rows; y++) { var row = new byte[columns]; for (int x = 0; x < columns; x++) { var value = characters[(y*columns+x)*2]; row[x] = value < 32 ? (byte)32 : value; } lines.Add(encoding.GetString(row).TrimEnd()); }
        return new { textMode = true, videoMode = mode, columns, rows, text = string.Join("\n", lines).TrimEnd() };
    }
    public void Dispose() { stopping = true; if (!thread.Join(10000)) throw new InvalidOperationException("DOS is still shutting down."); }
}
