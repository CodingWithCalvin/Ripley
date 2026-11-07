using System.IO;
using System.Text.Json;
namespace Reel;
public static class QueueStore
{
    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        using(var stream = new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None)) {
            JsonSerializer.Serialize(stream,value,new JsonSerializerOptions { WriteIndented = true }); stream.Flush(true);
        }
        File.Move(temp,path,true);
    }
    public static List<Job> Read(string path)
    {
        if(!File.Exists(path)) return new();
        var jobs = JsonSerializer.Deserialize<List<Job>>(File.ReadAllText(path)) ?? new();
        foreach(var j in jobs) {
            // Never infer completion solely from an existing pathname.
            if(j.Status is "Encoding" or "Publishing" or "Queued") j.Status = "Paused";
        }
        return jobs;
    }
}
