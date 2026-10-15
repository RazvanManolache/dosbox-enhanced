using System.IO;
using System.Text.Json.Nodes;

namespace DosboxEnhanced;

public static class GraphicsConfiguration
{
    public static string Load(string root)
    {
        var config = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "graphics.json")))!.AsObject();
        void Resolve(JsonObject obj, string key)
        {
            if (obj[key]?.GetValue<string>() is { Length: > 0 } path && !Path.IsPathRooted(path)) obj[key] = Path.GetFullPath(Path.Combine(root, path));
        }
        foreach (var key in new[] { "nr_dll", "sr_dll", "streamline", "native_bundle", "cache" }) Resolve(config, key);
        if (config["weight_source"] is JsonObject source) Resolve(source, "path");
        return config.ToJsonString();
    }
}
