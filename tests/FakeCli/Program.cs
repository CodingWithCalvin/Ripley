if(args.Contains("mkv")) {
    var folder=args[^1]; File.WriteAllText(Path.Combine(folder,"title_t00.mkv"),"test raw content");
    Console.WriteLine("PRGV:50,50,100");
    if(Environment.GetEnvironmentVariable("REEL_TEST_MODE")=="wait") await Task.Delay(30000);
    Console.WriteLine(Environment.GetEnvironmentVariable("REEL_TEST_MODE")=="partial"?"MSG:5004,0,0,\"Partial\",\"Partial\"":"MSG:5005,0,0,\"Done\",\"Done\"");
    return;
}
var index=Array.IndexOf(args,"-o");
if(index>=0) { File.WriteAllText(args[index+1],"test encoded content"); Console.WriteLine("Encoding: 100.00 %"); return; }
Console.WriteLine("Fake runtime for automated tests only");
