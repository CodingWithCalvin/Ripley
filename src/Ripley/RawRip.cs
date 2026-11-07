using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace Ripley;

public record RawSource(string Metadata, string OriginalName, JsonObject Disc);
public record RawRip(string Output, string Session)
{
    public string Files => Path.Combine(Session, "files");
    public string Metadata => Path.Combine(Session, DataFiles.DiscMetadata);
    public string Log => Path.Combine(Session, "makemkv.log");

    public static RawRip Create(string output, object details)
    {
        // Isolate MakeMKV's own filenames until a complete rip can be published without overwrites.
        Directory.CreateDirectory(output);
        var sessions = Path.Combine(output, DataFiles.Sessions);
        if(Directory.Exists(sessions) && (File.GetAttributes(sessions) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The raw-rip metadata folder is a junction. Choose a different destination.");
        var rip = new RawRip(output, Path.Combine(sessions, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(rip.Files);
        var doc = JsonSerializer.SerializeToNode(details)!.AsObject();
        doc["version"] = 3; doc["status"] = "Ripping";
        QueueStore.Write(rip.Metadata, doc);
        return rip;
    }

    public List<Job> Publish(string prefix, FolderContext context, Options? options, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var doc = JsonNode.Parse(File.ReadAllText(Metadata))!.AsObject();
        var sources = Directory.GetFiles(Files, "*.mkv").OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        if(sources.Length == 0) throw new IOException("No MKVs were produced; nothing was queued.");
        // Validate every name before moving any raw files. Record planned/final names durably for recovery.
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new JsonArray();
        foreach(var source in sources) {
            var original = Path.GetFileName(source);
            var name = Engine.Unique(Output, FileNames.WithPrefix(prefix, original, context), reserved);
            files.Add(new JsonObject { ["original_filename"] = original, ["raw_filename"] = name });
        }
        doc["files"] = files; doc["status"] = "Publishing";
        QueueStore.Write(Metadata, doc);
        var jobs = new List<Job>();
        var outputNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for(int i = 0; i < sources.Length; i++) {
            token.ThrowIfCancellationRequested();
            var file = files[i]!.AsObject();
            var name = Engine.Publish(sources[i], Output, file["raw_filename"]!.GetValue<string>(), candidate => {
                file["raw_filename"] = candidate; QueueStore.Write(Metadata, doc);
            });
            var source = Path.Combine(Output, name); var stat = new FileInfo(source);
            jobs.Add(new Job { Source = source, OriginalFilename = Path.GetFileName(sources[i]),
                Name = options == null ? name : Engine.Unique(options.Output, Path.ChangeExtension(name, options.Extension), outputNames),
                SourceSize = stat.Length, SourceModified = stat.LastWriteTimeUtc, DiscMetadata = Metadata, Encoding = options, Series = context.Series, Season = context.Season });
        }
        token.ThrowIfCancellationRequested();
        doc["status"] = "Ripped"; doc["completed_utc"] = DateTime.UtcNow;
        QueueStore.Write(Metadata, doc);
        return jobs;
    }

    public static Dictionary<string, RawSource> ReadSources(string folder)
    {
        var result = new Dictionary<string, RawSource>(StringComparer.OrdinalIgnoreCase);
        foreach(var session in DataFiles.SessionFolders.Select(name => Path.Combine(folder, name)).Where(Directory.Exists).SelectMany(Directory.EnumerateDirectories)) {
            var metadata = DataFiles.FindDiscMetadata(session);
            if(!File.Exists(metadata)) continue;
            var doc = JsonNode.Parse(File.ReadAllText(metadata))!.AsObject();
            if(doc["files"] is not JsonArray files) continue;
            foreach(var file in files) {
                var name = file!["raw_filename"]!.GetValue<string>(); FileNames.Validate(name);
                var original = file["original_filename"]!.GetValue<string>(); FileNames.Validate(original);
                // A planned name does not belong to this session until its source has been moved.
                if(File.Exists(Path.Combine(session, "files", original))) continue;
                if(!result.TryAdd(name, new(metadata, original, doc)))
                    throw new IOException("More than one rip manifest claims " + name + ". Check the raw-rip metadata before importing.");
            }
        }
        return result;
    }

    public static bool IsStagingFile(string file) => file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => DataFiles.SessionFolders.Contains(part, StringComparer.OrdinalIgnoreCase));
}
