using System.IO;
using System.Text.Json.Nodes;
namespace Ripley;

public static class AppStorage
{
    public static string Prepare(string localAppData)
    {
        var current = Path.Combine(localAppData, "Ripley");
        var previous = Path.Combine(localAppData, "Reel");
        if(Directory.Exists(current)) return current;
        if(!Directory.Exists(previous)) { Directory.CreateDirectory(current); return current; }
        var staging = Path.Combine(localAppData, ".ripley-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try {
            void CopyJson(string source, string relative)
            {
                var doc = JsonNode.Parse(File.ReadAllText(source)) ?? throw new IOException("Empty saved state: " + source);
                RebasePaths(doc, previous, current);
                QueueStore.Write(Path.Combine(staging, relative), doc);
            }
            foreach(var name in new[] { "settings.json", "queue.json" })
                if(File.Exists(Path.Combine(previous, name))) CopyJson(Path.Combine(previous, name), name);
            var presets = Path.Combine(previous, "presets");
            if(Directory.Exists(presets)) foreach(var file in Directory.GetFiles(presets, "*.json")) CopyJson(file, Path.Combine("presets", Path.GetFileName(file)));
            QueueStore.Write(Path.Combine(staging, "migration.json"), new { from = previous, migrated_utc = DateTime.UtcNow });
            Directory.Move(staging, current);
            return current;
        }
        finally { if(Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    static void RebasePaths(JsonNode node, string previous, string current)
    {
        string Rebase(string value) => value.StartsWith(previous + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? current + value[previous.Length..] : value;
        if(node is JsonObject obj) foreach(var (key, value) in obj.ToArray()) {
            if(value is JsonValue scalar && scalar.TryGetValue<string>(out var text)) obj[key] = Rebase(text);
            else if(value != null) RebasePaths(value, previous, current);
        }
        else if(node is JsonArray array) for(int i = 0; i < array.Count; i++) {
            if(array[i] is JsonValue scalar && scalar.TryGetValue<string>(out var text)) array[i] = Rebase(text);
            else if(array[i] != null) RebasePaths(array[i]!, previous, current);
        }
    }
}

public static class DataFiles
{
    public const string DiscMetadata = "ripley-disc.json";
    public const string Sidecar = ".ripley.json";
    public const string Sessions = ".ripley-rips";
    // Existing user media is never renamed just because the application was renamed.
    public static readonly string[] SessionFolders = [Sessions, ".reel-rips"];
    public static readonly string[] Sidecars = [Sidecar, ".reel.json"];
    public static string FindDiscMetadata(string folder) => new[] { DiscMetadata, "reel-disc.json" }
        .Select(name => Path.Combine(folder, name)).FirstOrDefault(File.Exists) ?? Path.Combine(folder, DiscMetadata);
}
