using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace Reel;
public record SeriesIdentity(string Provider, string Id, string Name, string Year, string Overview)
{
    public string Display => $"{Name} ({Year}) · TVDB {Id}";
}
public static class Tvdb
{
    public static async Task<List<SeriesIdentity>> Search(string query, string scoutFolder, CancellationToken token)
    {
        if(string.IsNullOrWhiteSpace(query) || query.Length > 200) throw new IOException("Enter a series name (up to 200 characters).");
        var (key,pin) = ScoutCredentials.Read(scoutFolder);
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
        const string root = "https://api4.thetvdb.com/v4/";
        var login = new Dictionary<string,string> { ["apikey"] = key }; if(!string.IsNullOrWhiteSpace(pin)) login["pin"] = pin;
        using var auth = await http.PostAsJsonAsync(root + "login", login, token);
        Check(auth);
        var authBody = JsonNode.Parse(await auth.Content.ReadAsStringAsync(token));
        var bearer = authBody?["data"]?["token"]?.ToString();
        if(string.IsNullOrEmpty(bearer)) throw new IOException("TVDB did not return a login token.");
        using var request = new HttpRequestMessage(HttpMethod.Get,root + "search?type=series&limit=12&query=" + Uri.EscapeDataString(query));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",bearer);
        using var response = await http.SendAsync(request,token); Check(response);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(token));
        return ReadResults(body);
    }
    public static List<SeriesIdentity> ReadResults(JsonNode? body) => (body?["data"] as JsonArray ?? new()).Where(n=>n?["type"]?.ToString() == "series" && long.TryParse(n["tvdb_id"]?.ToString(),out var id) && id > 0).Select(n=>new SeriesIdentity("tvdb",n!["tvdb_id"]!.ToString(),n["name"]?.ToString()??"",n["year"]?.ToString()??"",n["overview"]?.ToString()??"")).ToList();
    static void Check(HttpResponseMessage response) { if(!response.IsSuccessStatusCode) throw new IOException($"TVDB returned HTTP {(int)response.StatusCode}. Check Scout's TVDB key/PIN and your connection."); }
}
// Read-only adapter for Scout's protected credentials, including its SQLite store.
// Keys/PINs stay in memory and are never copied into Reel settings or logs.
public static class ScoutCredentials
{
    [StructLayout(LayoutKind.Sequential)] struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll",SetLastError=true)] static extern bool CryptUnprotectData(ref Blob input,IntPtr description,IntPtr entropy,IntPtr reserved,IntPtr prompt,int flags,out Blob output);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path,out IntPtr db,int flags,IntPtr vfs);
    [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_close(IntPtr db);
    [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_prepare_v2(IntPtr db,[MarshalAs(UnmanagedType.LPUTF8Str)] string sql,int bytes,out IntPtr stmt,IntPtr tail);
    [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_step(IntPtr stmt);
    [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)] static extern IntPtr sqlite3_column_blob(IntPtr stmt,int col);
    [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_column_bytes(IntPtr stmt,int col);
    [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_finalize(IntPtr stmt);
    public static (string Key,string? Pin) Read(string folder)
    {
        foreach(var data in Blobs(folder)) {
            var input = new Blob { Size = data.Length, Data = Marshal.AllocHGlobal(data.Length) }; Blob output = default;
            byte[]? plain = null;
            try {
                Marshal.Copy(data,0,input.Data,data.Length);
                if(!CryptUnprotectData(ref input,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,1,out output)) continue;
                plain = new byte[output.Size]; Marshal.Copy(output.Data,plain,0,plain.Length);
                var keys = JsonSerializer.Deserialize<Dictionary<string,string>>(plain);
                if(keys?.TryGetValue("metadata|tvdb",out var key) == true && !string.IsNullOrWhiteSpace(key)) return(key,keys.GetValueOrDefault("metadata|tvdb-pin"));
            } finally { if(plain != null) CryptographicOperations.ZeroMemory(plain); Marshal.Copy(new byte[data.Length],0,input.Data,data.Length); Marshal.FreeHGlobal(input.Data); if(output.Data != IntPtr.Zero) { Marshal.Copy(new byte[output.Size],0,output.Data,output.Size); LocalFree(output.Data); } }
        }
        throw new IOException("No readable TVDB key found in Scout. Save it in Scout > Settings > Show discovery, or choose Scout's data folder in Reel.");
    }
    static IEnumerable<byte[]> Blobs(string folder)
    {
        var dbFile = Path.Combine(folder,"scout.db");
        if(File.Exists(dbFile)) {
            IntPtr db = IntPtr.Zero, stmt = IntPtr.Zero;
            try {
                if(sqlite3_open_v2(dbFile,out db,1,IntPtr.Zero) != 0) throw new IOException("Cannot open Scout's database for reading. Try closing Scout and retrying.");
                if(sqlite3_prepare_v2(db,"SELECT content FROM documents WHERE kind='credentials.dpapi' ORDER BY path",-1,out stmt,IntPtr.Zero) != 0) throw new IOException("Cannot read Scout's credential database. Check access to its folder, or close Scout and retry.");
                int code; while((code = sqlite3_step(stmt)) == 100) { var size = sqlite3_column_bytes(stmt,0); if(size is <= 0 or > 1048576) continue; var bytes = new byte[size]; Marshal.Copy(sqlite3_column_blob(stmt,0),bytes,0,size); yield return bytes; }
                if(code != 101) throw new IOException("Scout's credential database could not be read.");
            } finally { if(stmt != IntPtr.Zero) sqlite3_finalize(stmt); if(db != IntPtr.Zero) sqlite3_close(db); }
        }
        foreach(var p in new[]{ Path.Combine(folder,"credentials.dpapi"),Path.Combine(folder,"UI-Trial","credentials.dpapi") }) if(File.Exists(p)) yield return File.ReadAllBytes(p);
    }
}
