if(args.Contains("--preset-list")) {
    var import=Array.IndexOf(args,"--preset-import-file");
    using var preset=System.Text.Json.JsonDocument.Parse(File.ReadAllText(args[import+1]));
    Console.WriteLine(preset.RootElement.GetProperty("PresetList")[0].GetProperty("PresetName").GetString());
    return;
}
if(args.Contains("--help")) {
    Console.WriteLine("x265_10bit\nx265\nx264");
    return;
}
if(args.Contains("info")) {
    Console.WriteLine("CINFO:32,0,\"ICARLY_S1_V2_D2\"\nTINFO:0,9,0,\"0:22:00\"\nTINFO:0,27,0,\"title_t00.mkv\"");
    return;
}
if(args.Contains("mkv")) {
    var folder=args[^1]; File.WriteAllText(Path.Combine(folder,"title_t00.mkv"),"test raw content");
    Console.WriteLine("PRGT:5024,0,\"Saving all titles to MKV files\"\nMSG:5014,131072,2,\"Saving titles\",\"Saving titles\",\"1\",\"folder\"\nPRGC:5017,0,\"Saving to MKV file\"");
    Console.WriteLine("PRGV:50,50,100");
    if(Environment.GetEnvironmentVariable("RIPLEY_TEST_MODE")=="wait") await Task.Delay(30000);
    Console.WriteLine(Environment.GetEnvironmentVariable("RIPLEY_TEST_MODE")=="partial"?"MSG:5004,0,0,\"Partial\",\"Partial\"":"MSG:5005,0,0,\"Done\",\"Done\"");
    return;
}
var index=Array.IndexOf(args,"-o");
if(index>=0) {
    var mode=Environment.GetEnvironmentVariable("RIPLEY_TEST_MODE");
    if(mode is "encode-wait" or "encode-delay") {
        File.WriteAllText(args[index+1],"incomplete encoded content");Console.WriteLine("Encoding: 1.00 %");await Task.Delay(mode=="encode-wait"?30000:1500);
    }
    File.WriteAllText(args[index+1],"test encoded content"); Console.WriteLine("Encoding: 100.00 %"); return;
}
Console.WriteLine("Fake runtime for automated tests only");
