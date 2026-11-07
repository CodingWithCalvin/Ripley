using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
namespace Ripley;

public record FolderContext(SeriesIdentity? Series, string Season, string Disc, DateTime Date, string? Volume = null, string? DiscNumber = null);
public static class FolderNames
{
    public const string DefaultRaw = "{series} ({year}) [tvdb-{tvdb_id}]\\{disc_label}";
    public const string DefaultCompressed = "{series} ({year}) [tvdb-{tvdb_id}]";
    public static string ExpandTokens(string template, FolderContext context, string kind = "folder")
    {
        return Regex.Replace(template, @"\{\{|\}\}|\{(?<name>[a-zA-Z_]+)(?::(?<format>[^{}]+))?\}|[{}]", m => {
            if(m.Value == "{{") return "{"; if(m.Value == "}}") return "}";
            if(!m.Groups["name"].Success) throw new IOException($"Unmatched brace in {kind} format. Use {{{{ and }}}} for literal braces.");
            var token=m.Groups["name"].Value.ToLowerInvariant(); var format=m.Groups["format"].Value;
            string? value = token switch {
                "series" or "series_name" => context.Series?.Name,
                "year" => context.Series?.Year,
                "tvdb_id" => context.Series?.Provider == "tvdb" ? context.Series.Id : null,
                "disc_label" => context.Disc,
                "season" => context.Season,
                "volume_number" => context.Volume ?? DiscLabelHints.Parse(context.Disc).Volume?.ToString(CultureInfo.InvariantCulture),
                "disc_number" => context.DiscNumber ?? DiscLabelHints.Parse(context.Disc).DiscNumber?.ToString(CultureInfo.InvariantCulture),
                "date" => context.Date.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),
                _ => throw new IOException("Unknown " + kind + " token: {" + token + "}.")
            };
            if(string.IsNullOrWhiteSpace(value)) throw new IOException("Missing value for {" + token + "}. Choose a series, fill in the season, volume number, disc number or disc label, or remove that token.");
            if(format.Length>0) {
                if(token is "season" or "volume_number" or "disc_number" && Regex.IsMatch(format,@"^0{1,4}$") && int.TryParse(value,out var season) && season>=0) value=season.ToString(format,CultureInfo.InvariantCulture);
                else if(token=="date" && format is "yyyyMMdd" or "yyyy-MM-dd") value=context.Date.ToString(format,CultureInfo.InvariantCulture);
                else throw new IOException("Unsupported format for {"+token+"}. Use {season:00}, {date:yyyyMMdd}, or an unformatted token.");
            }
            // Metadata is one component, never a path or a second template.
            foreach(var c in Path.GetInvalidFileNameChars()) value=value.Replace(c,'_');
            return value.Trim().TrimEnd('.');
        });
    }
    public static string Expand(string template, FolderContext context)
    {
        if(string.IsNullOrWhiteSpace(template)) throw new IOException("Enter a folder format, for example {disc_label} or {series} ({year}).");
        var expanded = ExpandTokens(template, context);
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
    public static List<Job> PlanExisting(string[] roots,Options options,bool recursive,string format,SeriesIdentity? fallbackSeries=null,string filenamePrefix="")
    {
        return PlanFiles(Engine.Scan(roots,options.Output,recursive,options.Extension),roots,options,format,fallbackSeries,filenamePrefix);
    }
    public static List<Job> PlanFiles(IEnumerable<Job> files,string[] roots,Options options,string format,SeriesIdentity? fallbackSeries=null,string filenamePrefix="")
    {
        var jobs=files.Select(j=>new Job{Source=j.Source,SourceSize=j.SourceSize,SourceModified=j.SourceModified}).ToList();
        var destination=Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.Output));
        if(jobs.Any(j=>j.Source.StartsWith(destination+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))) throw new IOException("Source files cannot be inside the output directory.");
        var names=new Dictionary<string,HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var manifests=new Dictionary<string,Dictionary<string,RawSource>>(StringComparer.OrdinalIgnoreCase);
        foreach(var job in jobs) {
            var parent=Path.GetDirectoryName(job.Source)!;
            if(!manifests.TryGetValue(parent,out var sources)) manifests[parent]=sources=RawRip.ReadSources(parent);
            sources.TryGetValue(Path.GetFileName(job.Source),out var source);
            JsonObject? disc=source?.Disc;
            var metadata=DataFiles.FindDiscMetadata(parent);
            if(source==null && File.Exists(metadata)) {
                disc=JsonNode.Parse(File.ReadAllText(metadata))!.AsObject();
                job.DiscMetadata=metadata;
            }
            if(disc!=null) {
                if(disc["status"]?.GetValue<string>()!="Ripped") throw new IOException($"Incomplete Ripley rip: {job.Source}. Finish/re-rip that disc before importing it.");
            }
            var fileContext=ExistingContext(job.Source,roots,disc,fallbackSeries);
            string output,wanted;
            try {
                output=Resolve(options.Output,format,fileContext);
                // Ripley rips already have their prefix; never prefix them again during import.
                wanted=source!=null?Path.GetFileName(job.Source):FileNames.WithPrefix(filenamePrefix,Path.GetFileName(job.Source),fileContext);
            }
            catch(IOException ex) {
                var message=ex.Message;
                if(message.StartsWith("Missing value for ",StringComparison.Ordinal)) {
                    message=message[..(message.IndexOf('.')+1)]+" This file has no saved or detected value. For missing series details, enable the selected-series fallback; otherwise remove the token or supply explicit markers in this source folder's name. Current disc fields are not used for batch compression.";
                }
                throw new IOException($"Cannot plan {job.Source}: {message}",ex);
            }
            if(!names.TryGetValue(output,out var reserved)) names[output]=reserved=new(StringComparer.OrdinalIgnoreCase);
            job.OriginalFilename=source?.OriginalName??Path.GetFileName(job.Source);
            if(source!=null) job.DiscMetadata=source.Metadata;
            job.QueueId=job.DiscMetadata.Length>0?Path.GetFullPath(job.DiscMetadata):Path.GetFullPath(parent);
            job.QueueName=(fileContext.Series?.Name is {Length:>0} seriesName?seriesName+" · ":"")+fileContext.Disc;
            job.Name=Engine.Unique(output,Path.ChangeExtension(wanted,options.Extension),reserved);job.Encoding=options with {Output=output};job.Series=fileContext.Series;job.Season=fileContext.Season;
        }
        return jobs;
    }

    static FolderContext ExistingContext(string file,string[] roots,JsonObject? disc,SeriesIdentity? fallbackSeries)
    {
        var parent=new DirectoryInfo(Path.GetDirectoryName(file)!);
        var label=disc?["disc_label"]?.GetValue<string>()??parent.Name;
        var candidates=new List<string>{label,Path.GetFileNameWithoutExtension(file)};
        // Read explicit markers only within the selected tree, never from unrelated ancestors.
        var selectedRoot=roots.Select(Path.GetFullPath).Select(root=>root.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar))
            .Where(root=>file.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
            .OrderBy(root=>root.Length).First();
        for(var folder=parent;folder!=null&&folder.FullName.StartsWith(selectedRoot+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);folder=folder.Parent)
            if(folder.Parent!=null) candidates.Add(folder.Parent.Name);
        var hints=candidates.Select(DiscLabelHints.Parse).ToArray();
        string Number(string key,Func<DiscLabelHints,int?> select) {
            var saved=disc?[key]?.ToString();
            return !string.IsNullOrWhiteSpace(saved)?saved:hints.Select(select).FirstOrDefault(n=>n.HasValue)?.ToString(CultureInfo.InvariantCulture)??"";
        }
        var identity=disc?["series_identity"]?.Deserialize<SeriesIdentity>()??fallbackSeries;
        var date=File.GetLastWriteTime(file);
        if(DateTime.TryParse(disc?["rip_date"]?.ToString(),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out var ripped)) date=ripped;
        else if(DateTime.TryParse(disc?["completed_utc"]?.ToString(),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out var completed)) date=completed.ToLocalTime();
        return new(identity,Number("season",h=>h.Season),label,date,Number("volume_number",h=>h.Volume),Number("disc_number",h=>h.DiscNumber));
    }
}
