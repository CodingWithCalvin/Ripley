using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
namespace Ripley;

public record OpticalDrive(int Index, string Name, string Label, string Device)
{
    public string Display => $"{(Device.Length > 0 ? Device : "Drive " + Index)} · {Name} · {(Label.Length > 0 ? Label : "No disc label")}";
    public string Source => Device.Length > 0 ? "dev:" + Device : "disc:" + Index;
}
public class DiscTitle
{
    public int Id { get; set; }
    public Dictionary<int, string> Attributes { get; set; } = new();
    public Dictionary<int, Dictionary<int, string>> Streams { get; set; } = new();
    public string Value(int key) => Attributes.GetValueOrDefault(key, "");
    public string Name => Value(27).Length > 0 ? Value(27) : $"Title {Id}";
    public string Duration => Value(9);
    public string Chapters => Value(8);
    public string Size => Value(10);
    public double? Seconds => TimeSpan.TryParse(Duration, CultureInfo.InvariantCulture, out var t) ? t.TotalSeconds : null;
    public string Tracks => string.Join("; ", Streams.OrderBy(x => x.Key).Select(x => string.Join(" ", new[]{1,4,7,14,19,21}.Select(k => x.Value.GetValueOrDefault(k, "")).Where(s => s.Length > 0))));
    public bool Qualifies(int minimum) => Seconds == null || Seconds >= minimum;
}
public class DiscInfo
{
    public Dictionary<int, string> Attributes { get; set; } = new();
    public List<DiscTitle> Titles { get; set; } = new();
    public List<OpticalDrive> Drives { get; set; } = new();
    public string Label => Attributes.GetValueOrDefault(32, Attributes.GetValueOrDefault(2, ""));
    public string Fingerprint => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Label + "|" + string.Join("|", Titles.OrderBy(t => t.Id).Select(t => $"{t.Id}:{t.Value(9)}:{t.Value(11)}:{t.Value(16)}:{t.Value(26)}:{t.Value(27)}")))));
}
public static class Disc
{
    // MakeMKV's robot protocol is CSV with backslash-escaped quoted strings.
    public static string[] Fields(string text)
    {
        var result = new List<string>(); var field = new StringBuilder(); bool quoted = false;
        for(int i = 0; i < text.Length; i++) {
            char c = text[i];
            if(c == '\\' && quoted && i + 1 < text.Length) { char n = text[++i]; field.Append(n switch { 'n' => '\n', 'r' => '\r', 't' => '\t', _ => n }); }
            else if(c == '"') quoted = !quoted;
            else if(c == ',' && !quoted) { result.Add(field.ToString()); field.Clear(); }
            else field.Append(c);
        }
        result.Add(field.ToString()); return result.ToArray();
    }
    public static DiscInfo Parse(IEnumerable<string> lines)
    {
        var info = new DiscInfo(); var titles = new Dictionary<int, DiscTitle>();
        foreach(var line in lines) {
            var colon = line.IndexOf(':'); if(colon < 0) continue;
            var type = line[..colon]; var f = Fields(line[(colon+1)..]);
            if(type == "DRV" && f.Length >= 6 && int.TryParse(f[0], out var drive) && f[1] != "256" && f[4].Length > 0) info.Drives.Add(new(drive,f[4],f[5],f.Length > 6 ? f[6] : ""));
            if(type == "CINFO" && f.Length >= 3 && int.TryParse(f[0], out var key)) info.Attributes[key] = f[2];
            if((type == "TINFO" || type == "SINFO") && f.Length >= 4 && int.TryParse(f[0], out var id)) {
                if(!titles.TryGetValue(id,out var title)) titles[id] = title = new DiscTitle { Id = id };
                if(type == "TINFO" && int.TryParse(f[1],out var attr)) title.Attributes[attr] = f[3];
                if(type == "SINFO" && f.Length >= 5 && int.TryParse(f[1],out var stream) && int.TryParse(f[2],out attr)) {
                    if(!title.Streams.TryGetValue(stream,out var attrs)) title.Streams[stream] = attrs = new();
                    attrs[attr] = f[4];
                }
            }
        }
        info.Titles = titles.Values.OrderBy(t=>t.Id).ToList(); return info;
    }
    public static double? Progress(string line)
    {
        if(!line.StartsWith("PRGV:")) return null;
        var f = Fields(line[5..]);
        return f.Length >= 3 && double.TryParse(f[1],out var current) && double.TryParse(f[2],out var max) && max > 0 ? Math.Clamp(current / max * 100,0,100) : null;
    }
    public static string[] RipArguments(string source, int minimum, string folder) => ["-r", "--messages=-stdout", "--progress=-same", "--minlength=" + minimum, "mkv", source, "all", folder];
    public static async Task<DiscInfo> Inspect(string cli, string source, Action<string> log, CancellationToken token)
    {
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var code = await Engine.Run(Engine.Command(cli,["-r", "--messages=-stdout", "--progress=-same", "--minlength=0", "info", source]), s => { lines.Enqueue(s); log(s); },token);
        if(code != 0) throw new IOException($"MakeMKV inspection failed (exit {code}). See log.");
        var info = Parse(lines);
        if(info.Titles.Count == 0) throw new IOException("No readable titles found. Check the disc, drive access, and MakeMKV activation. See log.");
        return info;
    }
    public static string NewFolder(string root, string label)
    {
        if(!Path.IsPathFullyQualified(root)) throw new IOException("Choose an absolute raw-rip folder.");
        foreach(var c in Path.GetInvalidFileNameChars()) label = label.Replace(c,'_');
        label = label.Trim().TrimEnd('.'); if(label.Length == 0) label = "DISC";
        if(label.Length > 80) label = label[..80];
        // A session suffix guarantees repeated disc labels never share raw files.
        var path = Path.Combine(root, label + "__" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(path); return path;
    }
    public static bool RipSucceeded(int code, IEnumerable<string> lines)
    {
        var messages = lines.Where(l=>l.StartsWith("MSG:")).Select(l=>Fields(l[4..])).ToList();
        // APP_DUMP_DONE=5005; APP_DUMP_DONE_PARTIAL=5004 (MakeMKV apdefs.h).
        return code == 0 && messages.Any(f=>f[0] == "5005") && !messages.Any(f=>f[0] == "5004" || (f.Length > 1 && int.TryParse(f[1],out var flags) && (flags & 3854) == 516));
    }
    public static async Task Rip(string cli, string source, int minimum, string folder, Action<string> log, CancellationToken token)
    {
        var messages = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var code = await Engine.Run(Engine.Command(cli, RipArguments(source,minimum,folder)), s=>{ if(s.StartsWith("MSG:")) messages.Enqueue(s); log(s); },token);
        if(!RipSucceeded(code,messages)) throw new IOException("MakeMKV did not confirm a complete rip. Raw files were kept, but this disc was not queued for compression. See makemkv.log.");
        if(!Directory.EnumerateFiles(folder,"*.mkv").Any()) throw new IOException("No MKVs were produced; nothing was queued.");
    }
}
