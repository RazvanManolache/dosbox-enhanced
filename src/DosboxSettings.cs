using System.Text;
using System.Text.RegularExpressions;

namespace DosboxEnhanced;

public static class DosboxIni
{
    public static Dictionary<string, Dictionary<string, string>> Effective(EnvironmentProfile profile)
    {
        var values = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["dosbox"] = new(StringComparer.OrdinalIgnoreCase) { ["memsize"] = profile.Memory.ToString() },
            ["cpu"] = new(StringComparer.OrdinalIgnoreCase) { ["cycles"] = profile.Cycles },
            ["render"] = new(StringComparer.OrdinalIgnoreCase) { ["scaler"] = "none", ["aspect"] = "true" }
        };
        foreach (var section in profile.DosboxSettings)
            foreach (var entry in section.Value) Set(values, section.Key, entry.Key, entry.Value);
        return values;
    }
    public static void Set(Dictionary<string, Dictionary<string, string>> settings, string section, string key, string value)
    {
        section = section.Trim().ToLowerInvariant(); key = key.Trim().ToLowerInvariant();
        if (!Regex.IsMatch(section, "^[a-z0-9_-]{1,64}$") || !Regex.IsMatch(key, "^[a-z0-9_. -]{1,80}$"))
            throw new ArgumentException("Use a DOSBox section name and setting name.");
        if (section == "autoexec") throw new ArgumentException("Autoexec contains commands. Use drive mappings, startup or a DOS BAT program for commands.");
        if(section=="libretro" && !key.StartsWith("dosbox_pure_"))throw new ArgumentException("DOSBox Pure frontend options begin with dosbox_pure_.");
        if(section=="libretro" && key=="dosbox_pure_conf" && value!="outside")throw new ArgumentException("The host manages dosbox_pure_conf=outside to load your environment settings.");
        if (value.Length > 4096 || value.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new ArgumentException("A DOSBox setting must be one line, up to 4096 characters.");
        if (!settings.TryGetValue(section, out var entries)) settings[section] = entries = new(StringComparer.OrdinalIgnoreCase);
        entries[key] = value;
    }
    public static string Format(Dictionary<string, Dictionary<string, string>> settings)
    {
        var text = new StringBuilder();
        foreach (var section in settings) { text.AppendLine($"[{section.Key}]"); foreach (var item in section.Value) text.AppendLine($"{item.Key}={item.Value}"); text.AppendLine(); }
        return text.ToString();
    }
    public static Dictionary<string, Dictionary<string, string>> Parse(string text)
    {
        if (text.Length > 65536) throw new ArgumentException("Settings are limited to 64 KB.");
        var settings = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase); string section = "";
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim(); if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';')) continue;
            if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1]; continue; }
            var separator = line.IndexOf('=');
            if (section.Length == 0 || separator <= 0) throw new ArgumentException("Use [section] headings and name=value settings.");
            Set(settings, section, line[..separator], line[(separator+1)..]);
        }
        return settings;
    }
}
