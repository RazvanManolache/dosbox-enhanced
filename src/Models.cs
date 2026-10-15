using System.IO;
using System.Text;
using System.Text.Json;
using System.Collections.ObjectModel;

namespace DosboxEnhanced;

public static class Paths
{
    public static string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
    public static string Runtime => Path.Combine(Root, "runtime");
    public static string Data => Path.Combine(Root, "data");
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static void AtomicWrite(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", content, new UTF8Encoding(false));
        File.Move(path + ".tmp", path, true);
    }
}
public record EffectSettings(bool Enabled = true, double Intensity = .65, double Tone = 1,
    double Structure = 1, int Style = 0, bool Compare = false, int Passes = 1,
    string Renderer = "phoenix", bool NvidiaUpscaling = true, bool NvidiaFrameGeneration = true,
    string UpscaleOrder = "after_nr", int UpscaleFactor = 4,
    bool InputResize = false, double InputScale = 2, string InputFilter = "nearest");
public readonly record struct GraphicsLayout(int SourceWidth, int SourceHeight, int InputWidth, int InputHeight, int NeuralWidth, int NeuralHeight,
    int OutputWidth, int OutputHeight, bool Upscaling, bool UpscaleFirst, int Factor, double InputFactor, string InputFilter)
{
    public static int ScaleDimension(int pixels, double scale) => Math.Max(1, (int)Math.Floor(pixels * scale + .5));
    public static GraphicsLayout For(int width, int height, EffectSettings settings)
    {
        if (width is < 1 or > 4096 || height is < 1 or > 4096) throw new ArgumentException("Invalid DOS resolution.");
        int factor = settings.NvidiaUpscaling ? Math.Clamp(settings.UpscaleFactor, 2, 4) : 1;
        double inputFactor = settings.InputResize ? settings.InputScale : 1;
        if (!double.IsFinite(inputFactor) || inputFactor is < .25 or > 4)
            throw new ArgumentException("Input resize must be between 0.25× and 4×.");
        int inputWidth = ScaleDimension(width, inputFactor), inputHeight = ScaleDimension(height, inputFactor);
        int outputWidth = inputWidth * factor, outputHeight = inputHeight * factor;
        if (outputWidth > 8192 || outputHeight > 8192 || (long)outputWidth * outputHeight > 16777216)
            throw new ArgumentException("Requested graphics output is too large. Choose a smaller upscale factor.");
        bool first = settings.NvidiaUpscaling && settings.UpscaleOrder == "before_nr";
        return new(width, height, inputWidth, inputHeight, first ? outputWidth : inputWidth, first ? outputHeight : inputHeight,
            outputWidth, outputHeight, settings.NvidiaUpscaling, first, factor, inputFactor,
            settings.InputResize && settings.InputFilter == "linear" ? "linear" : "nearest");
    }
}
public record AudioSettings(double Volume = 100, bool Muted = false)
{
    public double Gain => Muted ? 0 : Math.Clamp(Volume, 0, 100) / 100;
}
public class ApplicationPreferences
{
    public MouseSettings Mouse { get; set; } = new();
    public AudioSettings Audio { get; set; } = new();
    public McpSettings Mcp { get; set; } = new();
    public bool EnvironmentVisible { get; set; } = true;
    public bool EffectsVisible { get; set; } = true;
    public static ApplicationPreferences Load()
    {
        var file = Path.Combine(Paths.Data, "preferences.json");
        return File.Exists(file) ? JsonSerializer.Deserialize<ApplicationPreferences>(File.ReadAllText(file))! : new();
    }
    public void Save() => Paths.AtomicWrite(Path.Combine(Paths.Data, "preferences.json"), JsonSerializer.Serialize(this, Paths.Json));
}
public class Drive
{
    public string Letter { get; set; } = "C";
    public string Folder { get; set; } = "";
    public string Type { get; set; } = "dir";
    public string Label { get; set; } = "";
    public Dictionary<string, string> DisplayNames { get; set; } = [];
    public Dictionary<string, string> Launchers { get; set; } = [];
    public override string ToString() => $"{Letter}:   {(string.IsNullOrEmpty(Label) ? Folder : Label)}";
}
public sealed record BrowserEntry(FileSystemInfo Item, string Name);
public class EnvironmentProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New environment";
    public ObservableCollection<Drive> Drives { get; set; } = [];
    public string Startup { get; set; } = "";
    public string Cycles { get; set; } = "auto";
    public int ManualCycles { get; set; } = 10000;
    public int Memory { get; set; } = 32;
    public EffectSettings Effects { get; set; } = new();
    public bool DiscoverPrograms { get; set; } = true;
    public List<ProgramEntry> Programs { get; set; } = [];
    public Dictionary<string, Dictionary<string, string>> DosboxSettings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public override string ToString() => Name;
}
public class ProgramEntry
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Visible { get; set; } = true;
    [System.Text.Json.Serialization.JsonIgnore] public bool Available { get; set; }
    public override string ToString() => $"{Name}  ·  {Path}";
}
public class Library
{
    public ObservableCollection<EnvironmentProfile> Environments { get; set; } = [];
    public static Library Load()
    {
        var path = Path.Combine(Paths.Data, "environments.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<Library>(File.ReadAllText(path))! : new();
    }
    public void Save() => Paths.AtomicWrite(Path.Combine(Paths.Data, "environments.json"), JsonSerializer.Serialize(this, Paths.Json));
}
public static class Configuration
{
    public static EnvironmentProfile Quick(string executable)
    {
        executable = Path.GetFullPath(executable);
        if (!File.Exists(executable) || !new[] { ".exe", ".com", ".bat" }.Contains(Path.GetExtension(executable).ToLowerInvariant()))
            throw new ArgumentException("Choose an existing DOS EXE, COM or BAT file.");
        // The actual host file is mounted by its parent. DOSBox assigns its 8.3 name;
        // reject ambiguous long names rather than running another executable.
        var name = Path.GetFileName(executable);
        if (Path.GetFileNameWithoutExtension(name).Length > 8 || name.Contains(' '))
            throw new ArgumentException("Quick run needs a DOS 8.3 filename. Mount its folder in an environment for long filenames.");
        return new() { Name = Path.GetFileNameWithoutExtension(executable), Drives = [new() { Folder = Path.GetDirectoryName(executable)! }], Startup = "C:\\" + name };
    }
    public static string Generate(EnvironmentProfile profile, bool liveCpu = false)
    {
        if (profile.Memory is < 1 or > 1024) throw new ArgumentException("Memory must be 1–1024 MB.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(profile.Cycles, "^(auto|max|[1-9][0-9]*)$"))
            throw new ArgumentException("CPU cycles must be auto, max or a positive number.");
        var sections = DosboxIni.Effective(profile); sections.Remove("libretro");
        string hostCpu = sections["cpu"]["cycles"];
        if (liveCpu && CpuCycles.IsSimple(hostCpu)) sections["cpu"].Remove("cycles");
        var output = new StringBuilder((liveCpu ? "# Host CPU cycles: " + hostCpu + "\r\n" : "") + DosboxIni.Format(sections) + "[autoexec]\r\n@echo off\r\n");
        var letters = new HashSet<string>();
        foreach (var drive in profile.Drives)
        {
            var letter = drive.Letter.ToUpperInvariant();
            if (!System.Text.RegularExpressions.Regex.IsMatch(letter, "^[A-Y]$") || !letters.Add(letter))
                throw new ArgumentException("Drive letters must be unique A–Y; Z is DOSBox's built-in drive.");
            var path = Path.GetFullPath(drive.Folder);
            if (!Directory.Exists(path) || path.IndexOfAny(['\r', '\n', '"']) >= 0)
                throw new ArgumentException("Each drive needs an existing folder with a valid path.");
            if (path.Any(c => c > 127)) throw new ArgumentException("DOSBox Pure currently needs ASCII host paths. Use a folder junction with an ASCII name.");
            if (drive.Type != "dir" && drive.Type != "cdrom") throw new ArgumentException("Drive type must be dir or cdrom.");
            output.AppendLine($"mount {letter} \"{path}\"" + (drive.Type == "cdrom" ? " -t cdrom" : ""));
        }
        if (letters.Contains("C")) output.AppendLine("C:");
        var startup = profile.Startup.Trim();
        if (startup.Length > 0)
        {
            if (startup.IndexOfAny(['\r', '\n', '"', '&', '|', '<', '>']) >= 0)
                throw new ArgumentException("Startup must be one DOS executable path. Use a BAT file for multiple commands.");
            if (startup.Length < 3 || startup[1] != ':' || !letters.Contains(startup[..1].ToUpperInvariant()))
                throw new ArgumentException("Startup must name a mounted drive, for example C:\\GAME.EXE.");
            if (!new[] { ".exe", ".com", ".bat" }.Contains(Path.GetExtension(startup).ToLowerInvariant()))
                throw new ArgumentException("Startup must be an EXE, COM or BAT file.");
            var directory = startup.Replace('/', '\\');
            var split = directory.LastIndexOf('\\');
            output.AppendLine(directory[..2]);
            output.AppendLine("cd \\" + (split > 2 ? directory[3..split] : ""));
            output.AppendLine(split >= 0 ? directory[(split + 1)..] : directory[2..]);
        }
        return output.ToString();
    }
}
public sealed class TopEdgeDwell
{
    double x = double.NaN, y, since;
    public bool Update(double px, double py, double seconds, bool atTop)
    {
        if (!atTop) { x = double.NaN; return false; }
        if (double.IsNaN(x) || Math.Abs(px - x) > 2 || Math.Abs(py - y) > 2)
        { x = px; y = py; since = seconds; return false; }
        return seconds - since >= 1.0;
    }
    public void Reset() => x = double.NaN;
}
public record VideoFrame(int Width, int Height, byte[] Pixels, double Aspect, long Sequence, long Epoch = 0, int SourceWidth = 0, int SourceHeight = 0);

public static class DriveFolders
{
    public static string Create(string driveRoot, string parent, string folderName)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(driveRoot));
        parent = Path.GetFullPath(parent);
        if (!Directory.Exists(parent) ||
            (!parent.Equals(root, StringComparison.OrdinalIgnoreCase) &&
             !parent.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Choose a folder inside the mapped drive.");
        var name = folderName.Trim();
        if (string.IsNullOrEmpty(name) || name is "." or ".." ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.'))
            throw new ArgumentException("Enter a valid folder name without path separators.");
        var path = Path.Combine(parent, name);
        if (Directory.Exists(path) || File.Exists(path)) throw new ArgumentException("A folder or file with that name already exists.");
        Directory.CreateDirectory(path);
        return path;
    }
}

