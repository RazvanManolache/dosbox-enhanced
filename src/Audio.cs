using System.Runtime.InteropServices;

namespace DosboxEnhanced;

// Bounded PCM queue and reusable native buffers. Dropping old audio when a
// frontend stalls prevents seconds of delayed sound after a pause.
public sealed class WaveAudio : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] struct Format
    { public ushort tag, channels; public uint sampleRate, bytesPerSecond; public ushort alignment, bits, extra; }
    [StructLayout(LayoutKind.Sequential)] struct Header
    { public IntPtr data; public uint length, recorded; public nuint user; public uint flags, loops; public IntPtr next; public nuint reserved; }
    [DllImport("winmm.dll")] static extern uint waveOutOpen(out IntPtr handle, uint device, ref Format format, IntPtr callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll")] static extern uint waveOutPrepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] static extern uint waveOutUnprepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] static extern uint waveOutWrite(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] static extern uint waveOutReset(IntPtr handle);
    [DllImport("winmm.dll")] static extern uint waveOutClose(IntPtr handle);
    readonly object gate = new();
    readonly Queue<short> samples = new();
    readonly IntPtr handle;
    readonly Thread worker;
    volatile bool stopping;
    public AudioSettings Settings;
    public double LastGain = double.NaN;
    readonly int chunk;
    readonly uint headerSize = (uint)Marshal.SizeOf<Header>();
    public WaveAudio(int rate, AudioSettings? settings = null)
    {
        Settings = settings ?? new();
        rate = rate > 0 ? rate : 48000; chunk = rate / 100 * 2;
        var format = new Format { tag = 1, channels = 2, sampleRate = (uint)rate, bytesPerSecond = (uint)rate * 4, alignment = 4, bits = 16 };
        if (waveOutOpen(out handle, uint.MaxValue, ref format, IntPtr.Zero, IntPtr.Zero, 0) != 0) { handle = IntPtr.Zero; }
        worker = new Thread(Loop) { IsBackground = true, Name = "DOS audio" }; worker.Start();
    }
    public void Write(IntPtr data, int frames) { var pcm = new short[frames * 2]; Marshal.Copy(data, pcm, 0, pcm.Length); Write(pcm); }
    public void Write(short[] pcm)
    {
        if (handle == IntPtr.Zero) return;
        lock (gate) { foreach (var sample in pcm) samples.Enqueue(sample); while (samples.Count > chunk * 12) samples.Dequeue(); }
    }
    public void Clear() { lock (gate) samples.Clear(); if (handle != IntPtr.Zero) waveOutReset(handle); }
    void Loop()
    {
        if (handle == IntPtr.Zero) return;
        var buffers = new List<(IntPtr header, IntPtr data)>();
        try
        {
            for (int i = 0; i < 6; i++)
            {
                var data = Marshal.AllocHGlobal(chunk * 2); var header = Marshal.AllocHGlobal((int)headerSize);
                Marshal.StructureToPtr(new Header { data = data, length = (uint)chunk * 2 }, header, false);
                waveOutPrepareHeader(handle, header, headerSize); buffers.Add((header, data));
            }
            var busy = new HashSet<IntPtr>();
            while (!stopping)
            {
                foreach (var b in buffers)
                {
                    var header = Marshal.PtrToStructure<Header>(b.header);
                    if (busy.Contains(b.header) && (header.flags & 1) == 0) continue;
                    short[]? pcm = null;
                    lock (gate) { if (samples.Count >= chunk) { pcm = new short[chunk]; for (int i = 0; i < chunk; i++) pcm[i] = samples.Dequeue(); } }
                    if (pcm == null) continue;
                    var currentSettings = Volatile.Read(ref Settings);
                    ApplyGain(pcm, currentSettings); Volatile.Write(ref LastGain, currentSettings.Gain);
                    Marshal.Copy(pcm, 0, b.data, chunk); waveOutWrite(handle, b.header, headerSize); busy.Add(b.header);
                }
                Thread.Sleep(3);
            }
        }
        finally
        {
            waveOutReset(handle);
            foreach (var b in buffers) { waveOutUnprepareHeader(handle, b.header, headerSize); Marshal.FreeHGlobal(b.header); Marshal.FreeHGlobal(b.data); }
            waveOutClose(handle);
        }
    }
    public static void ApplyGain(short[] pcm, AudioSettings settings)
    {
        var gain = settings.Gain;
        if (gain == 1) return;
        for (int i = 0; i < pcm.Length; i++) pcm[i] = (short)Math.Clamp((int)Math.Round(pcm[i] * gain), short.MinValue, short.MaxValue);
    }
    public void Dispose() { stopping = true; worker.Join(); }
}
