using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
namespace Reel;

public record FolderContext(SeriesIdentity? Series, string Season, string Disc, DateTime Date);
public static class FolderNames
{
    public const string DefaultRaw = "{series} ({year}) [tvdb-{tvdb_id}]\\{disc}";
    public const string DefaultCompressed = "{series} ({year}) [tvdb-{tvdb_id}]";
    public static string Expand(string template, FolderContext context)
    {
        if(string.IsNullOrWhiteSpace(template)) throw new IOException("Enter a folder format, for example {disc} or {series} ({year}).");
        var expanded = Regex.Replace(template, @"\{\{|\}\}|\{(?<name>[a-zA-Z_]+)(?::(?<format>[^{}]+))?\}|[{}]", m => {
            if(m.Value == "{{") return "{"; if(m.Value == "}}") return "}";
            if(!m.Groups["name"].Success) throw new IOException("Unmatched brace in folder format. Use {{ and }} for literal braces.");
            var token=m.Groups["name"].Value.ToLowerInvariant(); var format=m.Groups["format"].Value;
            string? value = token switch {
                "series" or "series_name" => context.Series?.Name,
                "year" => context.Series?.Year,
                "tvdb_id" => context.Series?.Provider == "tvdb" ? context.Series.Id : null,
                "disc" or "disc_label" => context.Disc,
                "season" => context.Season,
                "volume" => DiscLabelHints.Parse(context.Disc).Volume?.ToString(CultureInfo.InvariantCulture),
                "disc_number" => DiscLabelHints.Parse(context.Disc).DiscNumber?.ToString(CultureInfo.InvariantCulture),
                "date" => context.Date.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),
                _ => throw new IOException("Unknown folder token: {" + token + "}.")
            };
            if(string.IsNullOrWhiteSpace(value)) throw new IOException("Missing value for {" + token + "}. Choose a series, enter the season/disc label, or remove that token. Volume and disc number require V/Volume and D/Disc markers in the label.");
            if(format.Length>0) {
                if(token is "season" or "volume" or "disc_number" && Regex.IsMatch(format,@"^0{1,4}$") && int.TryParse(value,out var season) && season>=0) value=season.ToString(format,CultureInfo.InvariantCulture);
                else if(token=="date" && format is "yyyyMMdd" or "yyyy-MM-dd") value=context.Date.ToString(format,CultureInfo.InvariantCulture);
                else throw new IOException("Unsupported format for {"+token+"}. Use {season:00}, {date:yyyyMMdd}, or an unformatted token.");
            }
            // Metadata is one component, never a path or a second template.
            foreach(var c in Path.GetInvalidFileNameChars()) value=value.Replace(c,'_');
            return value.Trim().TrimEnd('.');
        });
        if(Path.IsPathRooted(expanded) || expanded.Contains(':')) throw new IOException("Folder formats must be relative to the chosen base folder.");
        var parts=expanded.Replace('/','\\').Split('\\');
        for(int i=0;i<parts.Length;i++) {
            var part=parts[i].Trim();
            if(part.Length==0 || part is "." or "..") throw new IOException("Folder formats cannot contain empty folders, . or ...");
            part=part.TrimEnd('.');
            if(part.Length==0 || part.IndexOfAny(Path.GetInvalidFileNameChars())>=0) throw new IOException("Folder format contains an invalid Windows filename character.");
            if(part.Length>240) throw new IOException("A folder name is too long. Shorten the format.");
            if(Regex.IsMatch(part,@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)",RegexOptions.IgnoreCase)) part="_"+part;
            parts[i]=part;
        }
        return Path.Combine(parts);
    }
    public static string Resolve(string root,string template,FolderContext context,bool checkLinks=true)
    {
        if(!Path.IsPathFullyQualified(root)) throw new IOException("Choose an absolute base folder.");
        var fullRoot=Path.GetFullPath(root); var result=Path.GetFullPath(Path.Combine(fullRoot,Expand(template,context)));
        if(!result.StartsWith(Path.TrimEndingDirectorySeparator(fullRoot)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) throw new IOException("Folder format must stay inside the base folder.");
        if(checkLinks) CheckLinks(fullRoot,result); return result;
    }
    static void CheckLinks(string root,string destination)
    {
        var path=root;
        foreach(var part in Path.GetRelativePath(root,destination).Split(Path.DirectorySeparatorChar)) {
            path=Path.Combine(path,part);
            if((Directory.Exists(path)||File.Exists(path))&&(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0) throw new IOException("A generated folder crosses a shortcut/junction. Choose a different base folder or format.");
        }
    }
    public static string NextAvailable(string desired)
    {
        for(int i=1;;i++) { var candidate=i==1?desired:$"{desired} ({i})"; if(!Directory.Exists(candidate)&&!File.Exists(candidate)) return candidate; }
    }
    public static List<Job> PlanExisting(string[] roots,Options options,bool recursive,string format,FolderContext context)
    {
        var jobs=Engine.Scan(roots,options.Output,recursive,options.Extension);
        var names=new Dictionary<string,HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach(var job in jobs) {
            var parent=Path.GetDirectoryName(job.Source)!;var label=new DirectoryInfo(parent).Name;
            var metadata=Path.Combine(parent,"reel-disc.json");
            if(File.Exists(metadata)) {
                using var doc=System.Text.Json.JsonDocument.Parse(File.ReadAllText(metadata));
                if(doc.RootElement.TryGetProperty("disc_label",out var value)) label=value.GetString()??label;
                job.DiscMetadata=metadata;
            }
            var output=Resolve(options.Output,format,context with {Disc=label});
            if(!names.TryGetValue(output,out var reserved)) names[output]=reserved=new(StringComparer.OrdinalIgnoreCase);
            job.Name=Engine.Unique(output,job.Name,reserved);job.Encoding=options with {Output=output};
        }
        return jobs;
    }
    public static string CreateRaw(string desired)
    {
        var parent=Path.GetDirectoryName(desired) ?? throw new IOException("Invalid raw folder.");
        Directory.CreateDirectory(parent);
        var staging=Path.Combine(parent,".reel-new-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(staging);
        try { while(true) { var candidate=NextAvailable(desired); try { Directory.Move(staging,candidate); return candidate; } catch(IOException) when(Directory.Exists(candidate)||File.Exists(candidate)) { } } }
        finally { if(Directory.Exists(staging)) Directory.Delete(staging); }
    }
}
