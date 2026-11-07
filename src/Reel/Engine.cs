using System.IO;
using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.CompilerServices;
namespace Reel;

public sealed class Job : INotifyPropertyChanged
{
    public Options? Encoding { get; set; }
    public string Error { get; set; } = "";
    public SeriesIdentity? Series { get; set; }
    public string Season { get; set; } = "";
    public string DiscMetadata { get; set; } = "";
    public string PresetDisplay { get; set; } = "";
    public bool Include { get; set; } = true;
    public long SourceSize { get; set; }
    public DateTime SourceModified { get; set; }
    public string Source { get; set; } = "";
    string name = "", status = "Queued";
    public string Name { get => name; set { name = value; Changed(); } }
    public string Status { get => status; set { status = value; Changed(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    void Changed([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new(n));
}
public record Options(string Cli, string Preset, string Json, bool Gui, string Output, string Extension, string HardwareDecode = "", string Encoder = "");
public static class Engine
{
    public static string Unique(string directory, string name, HashSet<string>? reserved = null)
    {
        var stem = Path.GetFileNameWithoutExtension(name); var ext = Path.GetExtension(name);
        for (int i = 1; ; i++) {
            var candidate = i == 1 ? name : $"{stem} ({i}){ext}";
            if (!File.Exists(Path.Combine(directory, candidate)) && !File.Exists(Path.Combine(directory, candidate + ".reel.json")) && !Directory.Exists(Path.Combine(directory, candidate)) && !(reserved?.Contains(candidate) ?? false)) {
                reserved?.Add(candidate); return candidate;
            }
        }
    }
    public static List<Job> Scan(IEnumerable<string> roots, string output, bool recursive, string extension)
    {
        var dest = Path.GetFullPath(output).TrimEnd(Path.DirectorySeparatorChar);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<Job>();
        foreach (var root in roots) {
            var full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            if (full.Equals(dest, StringComparison.OrdinalIgnoreCase) || full.StartsWith(dest + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Source folders cannot be inside the output directory.");
            var opts = new EnumerationOptions { RecurseSubdirectories = recursive, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var file in Directory.EnumerateFiles(full, "*", opts).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) {
                if (!Path.GetExtension(file).Equals(".mkv", StringComparison.OrdinalIgnoreCase) || file.StartsWith(dest + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !seen.Add(file)) continue;
                var context = new DirectoryInfo(full).Name + " - " + Path.ChangeExtension(Path.GetRelativePath(full, file), null).Replace(Path.DirectorySeparatorChar, '_');
                foreach (var c in Path.GetInvalidFileNameChars()) context = context.Replace(c, '_');
                context = context.Trim().TrimEnd('.');
                if (context.Length > 160) context = context[..160];
                var stat = new FileInfo(file);
                result.Add(new Job { Source = file, Name = Unique(dest, context + extension, names), SourceSize = stat.Length, SourceModified = stat.LastWriteTimeUtc });
            }
        }
        return result;
    }
    public static ProcessStartInfo Command(string cli, IEnumerable<string> args)
    {
        var p = new ProcessStartInfo(cli) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) p.ArgumentList.Add(arg);
        return p;
    }
    public static List<string> PresetArgs(Options o)
    {
        var args = new List<string>();
        if (o.Gui) args.Add("--preset-import-gui");
        if (o.Json.Length > 0) { args.Add("--preset-import-file"); args.Add(o.Json); }
        return args;
    }
    public static async Task<int> Run(ProcessStartInfo info, Action<string> line, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var p = new Process { StartInfo = info };
        p.Start();
        void Kill() { try { if(!p.HasExited) p.Kill(true); } catch(InvalidOperationException) { } catch(System.ComponentModel.Win32Exception) { } }
        using var registration = token.Register(Kill);
        Exception? callbackError = null;
        async Task Drain(StreamReader reader) { while (await reader.ReadLineAsync() is { } s) { try { line(s); } catch(Exception ex) { Interlocked.CompareExchange(ref callbackError,ex,null); Kill(); } } }
        await Task.WhenAll(Drain(p.StandardOutput), Drain(p.StandardError), p.WaitForExitAsync());
        token.ThrowIfCancellationRequested();
        if(callbackError != null) throw new IOException("Could not record process output.",callbackError);
        return p.ExitCode;
    }
    public static string Publish(string temp, string output, string wanted, Action<string>? beforeMove = null)
    {
        while (true) {
            var name = Unique(output, wanted);
            beforeMove?.Invoke(name);
            try { File.Move(temp, Path.Combine(output, name), false); return name; }
            catch (IOException) when (File.Exists(Path.Combine(output, name)) || Directory.Exists(Path.Combine(output, name))) { }
        }
    }
    public static async Task<string> Encode(Job job, Options o, Action<string> line, CancellationToken token, Action<string>? beforeMove = null)
    {
        void CheckSource() { var stat = new FileInfo(job.Source); if(stat.Length != job.SourceSize || stat.LastWriteTimeUtc != job.SourceModified) throw new IOException("Source changed since scanning; let MakeMKV finish, then preview again."); }
        CheckSource();
        var scratch = Path.Combine(o.Output, ".reel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var temp = Path.Combine(scratch, "encoding" + o.Extension);
        try {
            var args = PresetArgs(o);
            args.AddRange(["-Z", o.Preset, "-i", job.Source, "-o", temp]);
            if(o.HardwareDecode is "nvdec" or "qsv") args.AddRange(["--enable-hw-decoding",o.HardwareDecode]);
            else if(o.HardwareDecode == "software") args.Add("--disable-hw-decoding");
            var code = await Run(Command(o.Cli, args), line, token);
            if (code != 0) throw new IOException($"HandBrake exited with code {code}. See activity log.");
            if (!File.Exists(temp) || new FileInfo(temp).Length == 0) throw new IOException("HandBrake did not produce an output file.");
            token.ThrowIfCancellationRequested();
            CheckSource();
            return Publish(temp, o.Output, job.Name, beforeMove);
        }
        finally { try { Directory.Delete(scratch, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
