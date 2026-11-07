using Reel;
using System.Text.Json.Nodes;
if(args.Length>1 && args[0]=="--tvdb") { var results=await Tvdb.Search("iCarly",args[1],CancellationToken.None); Console.WriteLine("TVDB search returned "+results.Count+" series: "+string.Join("; ",results.Select(r=>r.Display))); return; }
var temp=Path.Combine(Path.GetTempPath(),"ReelTests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);int passed=0;
void Check(bool value,string name){if(!value)throw new Exception(name);Console.WriteLine("PASS "+name);passed++;}
try {
 var a=Directory.CreateDirectory(Path.Combine(temp,"ICARLY_S1_V1_D1")).FullName;var b=Directory.CreateDirectory(Path.Combine(temp,"ICARLY_S1_V1_D2")).FullName;var output=Directory.CreateDirectory(Path.Combine(temp,"out")).FullName;
 File.WriteAllText(Path.Combine(a,"C1_t00.mkv"),"raw one");File.WriteAllText(Path.Combine(b,"C1_t00.mkv"),"raw two");var jobs=Engine.Scan([a,b,a],output,true,".mkv");
 Check(jobs.Count==2&&jobs.Select(j=>j.Name).Distinct().Count()==2,"Disc context and overlapping source deduplication");
 File.WriteAllText(Path.Combine(output,jobs[0].Name),"existing");Check(Engine.Scan([a],output,true,".mkv")[0].Name.EndsWith(" (2).mkv"),"Existing-name collision");
 var stage=Path.Combine(temp,"encoded");File.WriteAllText(stage,"new");var final=Engine.Publish(stage,output,jobs[0].Name);Check(final.EndsWith(" (2).mkv")&&File.ReadAllText(Path.Combine(output,jobs[0].Name))=="existing","Publication never overwrites");
 Check(Engine.Scan([temp],output,true,".mkv").Count==2,"Output subtree excluded");
 bool rejected=false;try{Engine.Scan([output],output,true,".mkv");}catch(IOException){rejected=true;}Check(rejected,"Overlapping destination rejected");
 var o=new Options("missing.exe","space preset","C:\\preset files\\p.json",false,output,".mkv");Check(Engine.Command(o.Cli,Engine.PresetArgs(o)).ArgumentList[1]==o.Json,"Arguments preserve spaces");
 File.AppendAllText(jobs[0].Source,"still ripping");rejected=false;try{await Engine.Encode(jobs[0],o,_=>{},CancellationToken.None);}catch(IOException){rejected=true;}Check(rejected,"Changing source rejected");
 using(var stop=new CancellationTokenSource()){stop.Cancel();rejected=false;try{await Engine.Run(Engine.Command("missing.exe",[]),_=>{},stop.Token);}catch(OperationCanceledException){rejected=true;}Check(rejected,"Cancelled work never launches");}
 var robot=new[]{"DRV:0,2,999,1,\"DVD drive\",\"ICARLY_S1_V1_D1\",\"E:\"","CINFO:2,0,\"ICARLY_S1_V1_D1\"","TINFO:0,27,0,\"C1_t00.mkv\"","TINFO:0,9,0,\"0:02:00\"","TINFO:0,8,0,\"5\"","TINFO:1,9,0,\"0:01:59\"","SINFO:0,1,1,6202,\"Audio\"","SINFO:0,1,4,0,\"English\""};var disc=Disc.Parse(robot);
 Check(disc.Drives.Single().Source=="dev:E:"&&disc.Label=="ICARLY_S1_V1_D1"&&disc.Titles[0].Tracks.Contains("English"),"Disc, title and track metadata parsed");
 Check(disc.Titles[0].Qualifies(120)&&!disc.Titles[1].Qualifies(120),"120-second cutoff includes exact boundary");
 Check(Disc.Fields("1,\"Name, with \\\"quotes\\\"\",\"E:\\\\folder\"")[1]=="Name, with \"quotes\"","Robot CSV escapes and commas");
 Check(Disc.RipArguments("dev:E:",120,a).Contains("--minlength=120"),"MakeMKV command receives minimum length");
 Check(Disc.RipSucceeded(0,["MSG:5005,0,0,\"Done\""])&&!Disc.RipSucceeded(0,["MSG:5004,0,0,\"Partial\""])&&!Disc.RipSucceeded(0,[])&&!Disc.RipSucceeded(1,["MSG:5005,0,0,\"Done\""]),"Partial/ambiguous/failed rip is never considered complete");
 var raw1=Disc.NewFolder(temp,"A/B");var raw2=Disc.NewFolder(temp,"A/B");Check(raw1!=raw2&&Path.GetDirectoryName(raw1)==temp,"Repeated or unsafe disc label gets unique contained folder");
 var qfile=Path.Combine(temp,"queue.json");jobs[0].Status="Encoding";jobs[1].Status="Completed";QueueStore.Write(qfile,jobs);var recovered=QueueStore.Read(qfile);Check(recovered[0].Status=="Paused"&&recovered[1].Status=="Completed","Queue restart pauses interrupted work and preserves completions");
 var json=Path.Combine(temp,"presets.json");File.WriteAllText(json,"""{"VersionMajor":72,"VersionMinor":0,"VersionMicro":0,"PresetList":[{"PresetName":"Custom Presets","Folder":true,"Type":1,"ChildrenArray":[{"PresetName":"My preset","Folder":false,"Type":1,"FileFormat":"av_mkv","VideoEncoder":"x265_10bit","AudioList":[{"AudioEncoder":"copy:ac3"}],"VideoQualitySlider":18}]}]}""");
 var preset=Presets.Load(json).Single();var snapshot=Presets.Snapshot(json,preset,"HandBrakeCLI.exe",output,temp,"nvdec");var frozen=JsonNode.Parse(File.ReadAllText(snapshot.Json))!["PresetList"]![0]!;
 Check(preset.Custom&&snapshot.Extension==".mkv"&&snapshot.HardwareDecode=="nvdec"&&frozen["VideoEncoder"]!.ToString()=="x265_10bit"&&frozen["AudioList"]![0]!["AudioEncoder"]!.ToString()=="copy:ac3"&&snapshot.Preset!=preset.Name,"Custom preset frozen without changing encoder or audio");
 File.WriteAllText(json,"{}");Check(File.Exists(snapshot.Json)&&frozen["VideoQualitySlider"]!.ToString()=="18","Queued preset independent of later GUI edits");
 Check(Tvdb.ReadResults(JsonNode.Parse("""{"data":[{"type":"series","tvdb_id":"82493","name":"iCarly","year":"2007"},{"type":"movie","tvdb_id":"1"}]}""")).Single().Id=="82493","TVDB search keeps explicit series IDs and excludes movies");

 var context=new FolderContext(new SeriesIdentity("tvdb","82493","iCarly","2007",""),"1","ICARLY_S1_V1_D1",new DateTime(2026,11,6));
 var format=@"{series} ({year}) [tvdb-{tvdb_id}]\Season {season:00}\{disc}";
 var expected=Path.Combine("iCarly (2007) [tvdb-82493]","Season 01","ICARLY_S1_V1_D1");
 Check(FolderNames.Expand(format,context)==expected,"Series/year/TVDB/season/disc interpolation");
 Check(FolderNames.Expand("{date:yyyyMMdd} - {{tvdb-{tvdb_id}}}",context)=="20261106 - {tvdb-82493}","Date formatting and literal braces");
 var unsafeSeries=context with {Series=context.Series! with {Name=@"A/B: C\D"}};
 Check(FolderNames.Expand("{series}",unsafeSeries)=="A_B_ C_D","Metadata separators sanitized as filename characters");
 foreach(var invalid in new[]{@"..\escape",@"C:\escape",@"\\server\share",@"x\..\y","{unknown}","{series:00}","{season:bad}","{series",@"x\\y"}) {
  rejected=false;try{FolderNames.Resolve(output,invalid,context);}catch(IOException){rejected=true;}Check(rejected,"Invalid template rejected: "+invalid);
 }
 rejected=false;try{FolderNames.Expand("{series}",context with {Series=null});}catch(IOException){rejected=true;}Check(rejected,"Missing series requires selection or token removal");
 Check(FolderNames.Expand("CON",context)=="_CON","Windows reserved folder names handled");
 var desired=FolderNames.Resolve(output,format,context);var first=FolderNames.CreateRaw(desired);File.WriteAllText(Path.Combine(first,"kept.txt"),"original");var second=FolderNames.CreateRaw(desired);
 Check(first==desired&&second==desired+" (2)"&&File.ReadAllText(Path.Combine(first,"kept.txt"))=="original","Raw folder format honored and repeated disc kept separate");
 var planned=FolderNames.PlanExisting([a,b],o,true,@"{series}\{disc}",context);
 Check(planned.Count==2&&planned.Select(j=>j.Encoding!.Output).Distinct().Count()==2&&planned.All(j=>j.Encoding!.Output.StartsWith(Path.Combine(output,"iCarly"))),"Existing rips use per-disc compressed folder templates");
 var fixedOutput=planned[0].Encoding!.Output;context=context with{Series=context.Series! with{Name="Different show"}};
 Check(planned[0].Encoding!.Output==fixedOutput,"Queued paths remain fixed after series selection changes");

 var hints=DiscLabelHints.Parse("ICARLY_S1_V2_D2");
 Check(hints.Season==1&&hints.Volume==2&&hints.DiscNumber==2,"iCarly season, volume and disc parsed independently");
 Check(DiscLabelHints.Parse("ICARLY_S2_V1_D1").Season==2,"Explicit S2 label identifies next season");
 Check(DiscLabelHints.Parse("SHOW_DISC_5").Season==null,"Disc five does not imply season two");
 Check(DiscLabelHints.Parse("SHOW_SEASON_1-2").Season==null&&DiscLabelHints.Parse("SHOW_S1_S2").Season==null,"Multi-season labels remain ambiguous");
 Check(DiscLabelHints.Parse("SHOW_SEASON_03_VOLUME_2_DISK_1")==new DiscLabelHints(3,2,1),"Long-form disc markers supported");
 Check(FolderNames.Expand(@"Season {season:00}\Volume {volume:00}\Disc {disc_number:00}",context with{Season="1",Disc="ICARLY_S1_V2_D2"})==@"Season 01\Volume 02\Disc 02","Season, volume and disc-number folder tokens");
 if(args.Length>0){
  var fake=Path.GetFullPath(args[0]);var success=Disc.NewFolder(temp,"SUCCESS");await Disc.Rip(fake,"disc:0",120,success,_=>{},CancellationToken.None);Check(File.Exists(Path.Combine(success,"title_t00.mkv")),"Successful rip retains raw output");
  Environment.SetEnvironmentVariable("REEL_TEST_MODE","partial");var partial=Disc.NewFolder(temp,"PARTIAL");rejected=false;try{await Disc.Rip(fake,"disc:0",120,partial,_=>{},CancellationToken.None);}catch(IOException){rejected=true;}Check(rejected&&File.Exists(Path.Combine(partial,"title_t00.mkv")),"Partial rip retained but rejected for compression");
  Environment.SetEnvironmentVariable("REEL_TEST_MODE","wait");var waiting=Disc.NewFolder(temp,"WAITING");using var stop=new CancellationTokenSource();var ripping=Disc.Rip(fake,"disc:0",120,waiting,_=>{},stop.Token);
  var encode=Engine.Scan([b],output,true,".mkv").Single();var encoded=await Engine.Encode(encode,new Options(fake,"test","",false,output,".mkv"),_=>{},CancellationToken.None);Check(!ripping.IsCompleted&&File.Exists(Path.Combine(output,encoded)),"Compression completes while another rip is active");
  stop.Cancel();rejected=false;try{await ripping;}catch(OperationCanceledException){rejected=true;}Check(rejected&&File.Exists(Path.Combine(waiting,"title_t00.mkv")),"Cancelling rip preserves raw files");Environment.SetEnvironmentVariable("REEL_TEST_MODE",null);
 }
 if(args.Length>2){
  var presets=Presets.Load(args[2]);var actual=Presets.Snapshot(args[2],presets[0],Path.GetFullPath(args[1]),output,temp,"nvdec");await Presets.Validate(actual,CancellationToken.None);Check(true,"Installed custom preset imports and NVIDIA decode is detected");
  if(args.Length>3){var sample=Path.GetFullPath(args[3]);var stat=new FileInfo(sample);var job=new Job{Source=sample,SourceSize=stat.Length,SourceModified=stat.LastWriteTimeUtc,Name="real-encode.mkv"};await Engine.Encode(job,actual,_=>{},CancellationToken.None);Check(new FileInfo(Path.Combine(output,job.Name)).Length>0,"Real HandBrake encode with installed custom preset");}
 }
 Console.WriteLine($"{passed} checks passed");
}finally{Environment.SetEnvironmentVariable("REEL_TEST_MODE",null);Directory.Delete(temp,true);}
