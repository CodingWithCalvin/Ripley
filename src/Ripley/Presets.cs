using System.IO;
using System.Text.Json.Nodes;
namespace Ripley;
public record HandBrakePreset(string Name, string Group, bool Custom, string Container, JsonObject Data)
{
    public string Display => $"{Name} · {(Custom ? "Custom" : "Built-in")} · {Container}";
    public string Extension => Container switch { "av_mkv" or "mkv" => ".mkv", "av_mp4" or "mp4" => ".mp4", "av_webm" or "webm" => ".webm", _ => throw new IOException("Unsupported preset container: " + Container) };
}
public static class Presets
{
    public static List<HandBrakePreset> Load(string path)
    {
        var root = JsonNode.Parse(File.ReadAllText(path)) ?? throw new IOException("Empty preset file.");
        var result = new List<HandBrakePreset>();
        void Visit(JsonArray nodes,string group,bool custom) {
            foreach(var node in nodes.OfType<JsonObject>()) {
                var name = node["PresetName"]?.ToString() ?? "";
                var isCustom = custom || node["Type"]?.ToString() == "1";
                if(node["Folder"]?.GetValue<bool>() == true) Visit(node["ChildrenArray"] as JsonArray ?? new(),group + name + "/",isCustom);
                else if(name.Length > 0) result.Add(new(name,group,isCustom,node["FileFormat"]?.ToString()??"",(JsonObject)node.DeepClone()));
            }
        }
        Visit(root as JsonArray ?? root["PresetList"] as JsonArray ?? throw new IOException("No HandBrake PresetList found."),"",false);
        return result.OrderByDescending(p=>p.Custom).ThenBy(p=>p.Name).ToList();
    }
    public static Options Snapshot(string source, HandBrakePreset selected, string cli, string output, string directory, string hardware = "")
    {
        var root = JsonNode.Parse(File.ReadAllText(source)) as JsonObject ?? throw new IOException("Use an exported HandBrake preset JSON object.");
        // One uniquely named preset prevents a built-in name collision. Every
        // encoding option, including container, tracks and filters, is retained.
        var name = "Ripley-" + Guid.NewGuid().ToString("N");
        var preset = (JsonObject)selected.Data.DeepClone(); preset["PresetName"] = name; preset["Folder"] = false; preset["Type"] = 1;
        root["PresetList"] = new JsonArray(preset);
        var path = Path.Combine(directory,name + ".json"); QueueStore.Write(path,root);
        return new(cli,name,path,false,Path.GetFullPath(output),selected.Extension,hardware,selected.Data["VideoEncoder"]?.ToString()??"");
    }
    public static string GuiHardware(string settings)
    {
        if(!File.Exists(settings)) return "";
        var s = JsonNode.Parse(File.ReadAllText(settings));
        if(s?["EnableNvDecSupport"]?.GetValue<bool>() == true) return "nvdec";
        if(s?["UseQSVDecodeForNonQSVEnc"]?.GetValue<bool>() == true) return "qsv";
        return "";
    }
    public static async Task Validate(Options options, CancellationToken token)
    {
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var args = Engine.PresetArgs(options); args.Add("--preset-list");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        if(await Engine.Run(Engine.Command(options.Cli,args),lines.Enqueue,timeout.Token) != 0 || !lines.Any(l=>l.Trim() == options.Preset)) throw new IOException("HandBrakeCLI could not import the selected preset. Check that the CLI supports this HandBrake preset version.");
        lines.Clear();
        if(await Engine.Run(Engine.Command(options.Cli,["--help"]),lines.Enqueue,timeout.Token) != 0) throw new IOException("HandBrakeCLI could not report its capabilities.");
        if(options.Encoder.Length > 0 && !lines.Any(l=>l.Trim() == options.Encoder)) throw new IOException("The selected encoder is unavailable in this CLI: " + options.Encoder + ". Choose a compatible CLI or preset.");
        if(options.HardwareDecode == "nvdec" && !lines.Any(l=>l.Contains("nvdec: is available"))) throw new IOException("NVIDIA decoding is enabled, but this CLI cannot access NVDEC. Check the driver or select software decoding.");
        if(options.HardwareDecode == "qsv" && !lines.Any(l=>l.Contains("qsv: is available"))) throw new IOException("Intel decoding is enabled, but this CLI cannot access QSV. Check the driver or select software decoding.");
    }
}
