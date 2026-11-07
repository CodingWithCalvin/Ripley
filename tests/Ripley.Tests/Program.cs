using Ripley;
using System.Text.Json.Nodes;
if(args.Length>1 && args[0]=="--tvdb") { var results=await Tvdb.Search("iCarly",args[1],CancellationToken.None); Console.WriteLine("TVDB search returned "+results.Count+" series: "+string.Join("; ",results.Select(r=>r.Display))); return; }
var temp=Path.Combine(Path.GetTempPath(),"RipleyTests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);int passed=0;
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
 var ripProgress=new RipQueue();ripProgress.Reset(4);ripProgress.Read("PRGV:65536,65536,65536");
 Check(ripProgress.Overall==0&&ripProgress.Files.All(f=>f.Status=="Waiting"),"Disc scanning progress does not mark raw files as ripped");
 ripProgress.Read("MSG:5014,131072,2,\"Localized message\",\"Localized format\",\"3\",\"folder\"");
 ripProgress.Read("PRGC:5017,0,\"Saving to MKV file\"");ripProgress.Read("PRGV:25,10,100");
 ripProgress.ObserveFiles(new[]{("C1_t00.mkv",1024L*1024)},name=>"2_2_"+name);
 Check(ripProgress.Files.Count==3&&ripProgress.Current!.Progress==25&&ripProgress.Overall==10&&ripProgress.Current.Name=="2_2_C1_t00.mkv"&&ripProgress.Current.Size=="1.0 MB","Ripping queue uses MakeMKV's actual count, per-file progress, overall progress and observed filename");
 ripProgress.Read("PRGC:5057,1,\"Analyzing seamless segments\"");ripProgress.Read("PRGV:90,35,100");
 Check(ripProgress.Files[0].Status=="Written"&&ripProgress.Files[0].Progress==100&&ripProgress.Current!.Progress==0,"Moving to the next file marks written files without treating segment analysis as file-writing progress");
 ripProgress.Read("PRGC:5017,1,\"Saving to MKV file\"");ripProgress.Read("PRGV:50,55,100");
 ripProgress.ObserveFiles(new[]{("C1_t00.mkv",2048L), ("C2_t01.mkv",3072L)},name=>"2_2_"+name);
 Check(ripProgress.Current!.OriginalName=="C2_t01.mkv"&&ripProgress.Current.Progress==50&&ripProgress.Summary.Contains("1 written"),"Subsequent raw files get their own queue row and current progress");
 ripProgress.Read("PRGV:bad,NaN,0");Check(Math.Abs(ripProgress.Overall-55)<0.001,"Malformed progress does not replace valid progress values");
 ripProgress.Stop(true);Check(ripProgress.Files.Take(2).All(f=>f.Status=="Kept · unverified")&&ripProgress.Files[2].Status=="Not started"&&ripProgress.Files.All(f=>f.Status!="Saved"),"Cancelled ripping distinguishes retained partial files from files never started");
 ripProgress.Reset(1);ripProgress.Complete(new[]{new Job{Source=Path.Combine(temp,"final (2).mkv"),OriginalFilename="title_t00.mkv",SourceSize=99}});
 Check(ripProgress.Files.Single().Name=="final (2).mkv"&&ripProgress.Files.Single().Status=="Saved"&&ripProgress.Overall==100,"Only successful final publication marks a rip saved with its actual collision-safe filename");
 Check(Disc.RipArguments("dev:E:",120,a).Contains("--minlength=120"),"MakeMKV command receives minimum length");
 Check(Disc.RipSucceeded(0,["MSG:5005,0,0,\"Done\""])&&!Disc.RipSucceeded(0,["MSG:5004,0,0,\"Partial\""])&&!Disc.RipSucceeded(0,[])&&!Disc.RipSucceeded(1,["MSG:5005,0,0,\"Done\""]),"Partial/ambiguous/failed rip is never considered complete");
 var raw1=Disc.NewFolder(temp,"A/B");var raw2=Disc.NewFolder(temp,"A/B");Check(raw1!=raw2&&Path.GetDirectoryName(raw1)==temp,"Repeated or unsafe disc label gets unique contained folder");
 var qfile=Path.Combine(temp,"queue.json");jobs[0].Status="Encoding";jobs[1].Status="Completed";QueueStore.Write(qfile,jobs);var recovered=QueueStore.Read(qfile);Check(recovered[0].Status=="Paused"&&recovered[1].Status=="Completed","Queue restart pauses interrupted work and preserves completions");
 var local=Path.Combine(temp,"local-state");var oldState=Path.Combine(local,"Reel");Directory.CreateDirectory(Path.Combine(oldState,"presets"));
 var oldPreset=Path.Combine(oldState,"presets","Reel-frozen.json");File.WriteAllText(oldPreset,"""{"PresetList":[{"PresetName":"Reel-frozen"}]}""");
 QueueStore.Write(Path.Combine(oldState,"queue.json"),new[]{new Job{Status="Encoding",Source=jobs[0].Source,Name="kept.mkv",Encoding=o with{Json=oldPreset,Preset="Reel-frozen"}}});
 QueueStore.Write(Path.Combine(oldState,"settings.json"),new{Raw="R:\\MAKEMKV_RIPS",Output="R:\\COMPRESSED",FilenamePrefix="{volume_number}_{disc_number}_"});
 var originalQueue=File.ReadAllText(Path.Combine(oldState,"queue.json"));var newState=AppStorage.Prepare(local);var migrated=QueueStore.Read(Path.Combine(newState,"queue.json")).Single();
 Check(Path.GetFileName(newState)=="Ripley"&&migrated.Status=="Paused"&&migrated.Source==jobs[0].Source&&migrated.Encoding!.Json==Path.Combine(newState,"presets","Reel-frozen.json")&&File.Exists(migrated.Encoding.Json),"Rename migrates saved queue and frozen preset paths without moving media");
 Check(File.ReadAllText(Path.Combine(oldState,"queue.json"))==originalQueue&&migrated.Encoding!.Preset=="Reel-frozen"&&File.ReadAllText(migrated.Encoding.Json).Contains("Reel-frozen")&&File.Exists(Path.Combine(newState,"settings.json")),"Rename keeps original saved data and imported preset names intact");
 QueueStore.Write(Path.Combine(newState,"settings.json"),new{marker="current"});AppStorage.Prepare(local);
 Check(File.ReadAllText(Path.Combine(newState,"settings.json")).Contains("current"),"Existing Ripley preferences are never overwritten by migration");
 var badLocal=Path.Combine(temp,"bad-state");Directory.CreateDirectory(Path.Combine(badLocal,"Reel"));File.WriteAllText(Path.Combine(badLocal,"Reel","queue.json"),"{bad");
 rejected=false;try{AppStorage.Prepare(badLocal);}catch(System.Text.Json.JsonException){rejected=true;}
 Check(rejected&&!Directory.Exists(Path.Combine(badLocal,"Ripley"))&&File.ReadAllText(Path.Combine(badLocal,"Reel","queue.json"))=="{bad","Failed migration preserves old state and does not publish partial new state");
 File.WriteAllText(Path.Combine(output,"legacy.mkv.reel.json"),"{}");Check(Engine.Unique(output,"legacy.mkv")=="legacy (2).mkv","Legacy sidecars still reserve their output filenames");
 var json=Path.Combine(temp,"presets.json");File.WriteAllText(json,"""{"VersionMajor":72,"VersionMinor":0,"VersionMicro":0,"PresetList":[{"PresetName":"Custom Presets","Folder":true,"Type":1,"ChildrenArray":[{"PresetName":"My preset","Folder":false,"Type":1,"FileFormat":"av_mkv","VideoEncoder":"x265_10bit","AudioList":[{"AudioEncoder":"copy:ac3"}],"VideoQualitySlider":18}]}]}""");
 var preset=Presets.Load(json).Single();var snapshot=Presets.Snapshot(json,preset,"HandBrakeCLI.exe",output,temp,"nvdec");var frozen=JsonNode.Parse(File.ReadAllText(snapshot.Json))!["PresetList"]![0]!;
 Check(preset.Custom&&snapshot.Extension==".mkv"&&snapshot.HardwareDecode=="nvdec"&&frozen["VideoEncoder"]!.ToString()=="x265_10bit"&&frozen["AudioList"]![0]!["AudioEncoder"]!.ToString()=="copy:ac3"&&snapshot.Preset!=preset.Name,"Custom preset frozen without changing encoder or audio");
 File.WriteAllText(json,"{}");Check(File.Exists(snapshot.Json)&&frozen["VideoQualitySlider"]!.ToString()=="18","Queued preset independent of later GUI edits");
 Check(Tvdb.ReadResults(JsonNode.Parse("""{"data":[{"type":"series","tvdb_id":"82493","name":"iCarly","year":"2007"},{"type":"movie","tvdb_id":"1"}]}""")).Single().Id=="82493","TVDB search keeps explicit series IDs and excludes movies");

 var context=new FolderContext(new SeriesIdentity("tvdb","82493","iCarly","2007",""),"1","ICARLY_S1_V1_D1",new DateTime(2026,11,6));
 var format=@"{series} ({year}) [tvdb-{tvdb_id}]\Season {season:00}\{disc_label}";
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
 var planned=FolderNames.PlanExisting([a,b],o,true,@"{series}\{disc_label}",context.Series);
 Check(planned.Count==2&&planned.Select(j=>j.Encoding!.Output).Distinct().Count()==2&&planned.All(j=>j.Encoding!.Output.StartsWith(Path.Combine(output,"iCarly"))),"Existing rips use per-disc compressed folder templates");
 var fixedOutput=planned[0].Encoding!.Output;context=context with{Series=context.Series! with{Name="Different show"}};
 Check(planned[0].Encoding!.Output==fixedOutput,"Queued paths remain fixed after series selection changes");

 var hints=DiscLabelHints.Parse("ICARLY_S1_V2_D2");
 Check(hints.Season==1&&hints.Volume==2&&hints.DiscNumber==2,"iCarly season, volume and disc parsed independently");
 Check(DiscLabelHints.Parse("ICARLY_S2_V1_D1").Season==2,"Explicit S2 label identifies next season");
 Check(DiscLabelHints.Parse("SHOW_DISC_5").Season==null,"Disc five does not imply season two");
 Check(DiscLabelHints.Parse("SHOW_SEASON_1-2").Season==null&&DiscLabelHints.Parse("SHOW_S1_S2").Season==null,"Multi-season labels remain ambiguous");
 Check(DiscLabelHints.Parse("SHOW_SEASON_03_VOLUME_2_DISK_1")==new DiscLabelHints(3,2,1),"Long-form disc markers supported");
 Check(FolderNames.Expand(@"Season {season:00}\Volume {volume_number:00}\Disc {disc_number:00}",context with{Season="1",Disc="ICARLY_S1_V2_D2"})==@"Season 01\Volume 02\Disc 02","Season, volume and disc-number folder tokens");
 var naming=context with{Series=new SeriesIdentity("tvdb","82493","iCarly","2007",""),Disc="UNLABELLED",Volume="2",DiscNumber="3"};
 var seasonFormat=@"{series} {{tvdb-{tvdb_id}}}\Season {season:00}";
 Check(FolderNames.Expand(seasonFormat,naming)==@"iCarly {tvdb-82493}\Season 01","Requested folder format preserves literal TVDB braces");
 Check(FileNames.WithPrefix("{volume_number}_{disc_number}_","ICARLY_S1_t00.mkv",naming)=="2_3_ICARLY_S1_t00.mkv","Manual volume and disc number prefix the original filename");
 Check(FileNames.WithPrefix("V{volume_number:00}_D{disc_number:00}_","title_t00.mkv",naming with{Disc="SHOW_V9_D9"})=="V02_D03_title_t00.mkv","Manual disc values override detected values and support padding");
 Check(FileNames.WithPrefix("","title_t00.mkv",naming)=="title_t00.mkv","Blank prefix preserves MakeMKV filename");
 foreach(var invalid in new[]{"{disc}_","{disc_desc}_","{volume}_",@"..\escape_",@"C:\escape_","bad/","bad:","{unknown}_"}) {
  rejected=false;try{FileNames.WithPrefix(invalid,"title_t00.mkv",naming);}catch(IOException){rejected=true;}Check(rejected,"Invalid or retired filename token rejected: "+invalid);
 }
 rejected=false;try{FileNames.WithPrefix("{volume_number}_","title_t00.mkv",naming with{Volume=""});}catch(IOException){rejected=true;}Check(rejected,"Missing volume is rejected before ripping");
 Check(FileNames.WithPrefix("{series}_","title_t00.mkv",naming with{Series=naming.Series! with{Name="A/B"}})=="A_B_title_t00.mkv","Metadata in filename prefixes cannot create subfolders");
 var shared=FolderNames.Resolve(Path.Combine(temp,"raw-shared"),seasonFormat,naming);
 RawRip NewRip(FolderContext ctx)=>RawRip.Create(shared,new{disc_label=ctx.Disc,season=ctx.Season,volume_number=ctx.Volume,disc_number=ctx.DiscNumber,filename_prefix="{volume_number}_{disc_number}_"});
 var ripOne=NewRip(naming);File.WriteAllText(Path.Combine(ripOne.Files,"title_t00.mkv"),"disc one");
 var rawOptions=o with{Output=Path.Combine(temp,"compressed-shared"),Extension=".mp4"};Directory.CreateDirectory(rawOptions.Output);
 var one=ripOne.Publish("{volume_number}_{disc_number}_",naming,rawOptions,CancellationToken.None).Single();
 var nextDisc=naming with{DiscNumber="4"};var ripTwo=NewRip(nextDisc);File.WriteAllText(Path.Combine(ripTwo.Files,"title_t00.mkv"),"disc two");
 var two=ripTwo.Publish("{volume_number}_{disc_number}_",nextDisc,rawOptions,CancellationToken.None).Single();
 Check(Path.GetDirectoryName(one.Source)==shared&&Path.GetDirectoryName(two.Source)==shared&&File.ReadAllText(one.Source)=="disc one"&&File.ReadAllText(two.Source)=="disc two","Different discs share the exact raw folder without overwrites");
 Check(one.Name=="2_3_title_t00.mp4"&&two.Name=="2_4_title_t00.mp4"&&one.OriginalFilename=="title_t00.mkv","Compression keeps prefixed names with the preset extension");
 var repeat=NewRip(naming);File.WriteAllText(Path.Combine(repeat.Files,"title_t00.mkv"),"repeated disc");
 var repeated=repeat.Publish("{volume_number}_{disc_number}_",naming,rawOptions,CancellationToken.None).Single();
 Check(Path.GetFileName(repeated.Source)=="2_3_title_t00 (2).mkv"&&File.ReadAllText(one.Source)=="disc one","Repeated raw filenames get suffixes and retain the original file");
 var imported=FolderNames.PlanExisting([shared],rawOptions,true,seasonFormat,naming.Series,"CHANGED_");
 Check(imported.Count==3&&imported.Any(j=>j.Name=="2_3_title_t00.mp4")&&imported.All(j=>!j.Name.Contains("CHANGED_")&&j.OriginalFilename=="title_t00.mkv"&&File.Exists(j.DiscMetadata)),"Reimported Ripley rips keep filenames without adding the prefix again");
 var external=FolderNames.PlanExisting([a,b],rawOptions,true,seasonFormat,naming.Series,"{volume_number}_{disc_number}_");
 Check(external.Select(j=>j.Name).Order().SequenceEqual(new[]{"1_1_C1_t00.mp4","1_2_C1_t00.mp4"})&&File.Exists(Path.Combine(a,"C1_t00.mkv")),"Existing external rips use per-disc values without renaming raw sources");
 var unfinished=NewRip(naming);File.WriteAllText(Path.Combine(unfinished.Files,"title_t00.mkv"),"partial raw");
 Check(Engine.Scan([shared],rawOptions.Output,true,".mkv").Count==3&&FolderNames.PlanExisting([shared],rawOptions,true,seasonFormat,naming.Series).Count==3,"Unfinished staged rips are excluded from shared-folder imports");
 using(var cancelled=new CancellationTokenSource()){cancelled.Cancel();rejected=false;try{unfinished.Publish("{volume_number}_",naming,rawOptions,cancelled.Token);}catch(OperationCanceledException){rejected=true;}Check(rejected&&File.Exists(Path.Combine(unfinished.Files,"title_t00.mkv")),"Cancelled raw publication preserves staged files");}
 var unsafeRip=NewRip(naming);File.WriteAllText(Path.Combine(unsafeRip.Files,"a.mkv"),"first");File.WriteAllText(Path.Combine(unsafeRip.Files,new string('z',238)+".mkv"),"long");
 rejected=false;try{unsafeRip.Publish("prefix_",naming,rawOptions,CancellationToken.None);}catch(IOException){rejected=true;}Check(rejected&&Directory.GetFiles(unsafeRip.Files,"*.mkv").Length==2,"All raw filenames are validated before any file is moved");
 var interrupted=JsonNode.Parse(File.ReadAllText(ripOne.Metadata))!;interrupted["status"]="Publishing";QueueStore.Write(ripOne.Metadata,interrupted);
 rejected=false;try{FolderNames.PlanExisting([shared],rawOptions,true,seasonFormat,naming.Series);}catch(IOException){rejected=true;}Check(rejected,"Interrupted raw publication cannot enter compression");
 interrupted["status"]="Ripped";QueueStore.Write(ripOne.Metadata,interrupted);
 var pending=JsonNode.Parse(File.ReadAllText(unfinished.Metadata))!;pending["status"]="Publishing";
 pending["files"]=new JsonArray(new JsonObject{["original_filename"]="title_t00.mkv",["raw_filename"]="2_3_title_t00.mkv"});QueueStore.Write(unfinished.Metadata,pending);
 Check(FolderNames.PlanExisting([shared],rawOptions,true,seasonFormat,naming.Series).Count==3,"Unmoved filename plans cannot claim another disc's completed file");
 var legacyRaw=Directory.CreateDirectory(Path.Combine(temp,"legacy-raw")).FullName;var legacySession=Directory.CreateDirectory(Path.Combine(legacyRaw,".reel-rips","session")).FullName;
 File.WriteAllText(Path.Combine(legacyRaw,"2_2_title_t00.mkv"),"legacy raw");Directory.CreateDirectory(Path.Combine(legacySession,"files"));File.WriteAllText(Path.Combine(legacySession,"files","incomplete.mkv"),"partial");
 QueueStore.Write(Path.Combine(legacySession,"reel-disc.json"),new{status="Ripped",disc_label="ICARLY_S1_V2_D2",files=new[]{new{original_filename="title_t00.mkv",raw_filename="2_2_title_t00.mkv"}}});
 var legacyJobs=FolderNames.PlanExisting([legacyRaw],rawOptions,true,seasonFormat,naming.Series,"ADDED_");
 Check(legacyJobs.Count==1&&legacyJobs[0].Name=="2_2_title_t00.mp4"&&legacyJobs[0].OriginalFilename=="title_t00.mkv"&&legacyJobs[0].DiscMetadata.EndsWith("reel-disc.json"),"Existing Reel manifests and unfinished-rip exclusions remain readable");
 var batchRaw=Path.Combine(temp,"batch-raw");
 var batchContexts=new[]{naming with{Season="1",Volume="2",DiscNumber="2",Date=new DateTime(2025,1,2)},naming with{Season="2",Volume="1",DiscNumber="1",Date=new DateTime(2025,2,3)},naming with{Series=new SeriesIdentity("tvdb","78804","Drake & Josh","2004",""),Season="3",Volume="4",DiscNumber="5",Date=new DateTime(2025,3,4)}};
 var batchSources=new List<string>();
 foreach(var ctx in batchContexts) {
  var session=RawRip.Create(batchRaw,new{series_identity=ctx.Series,season=ctx.Season,volume_number=ctx.Volume,disc_number=ctx.DiscNumber,disc_label=ctx.Disc,rip_date=ctx.Date});
  File.WriteAllText(Path.Combine(session.Files,"title_t00.mkv"),"batch raw");
  batchSources.Add(session.Publish("{season}_{volume_number}_{disc_number}_",ctx,null,CancellationToken.None).Single().Source);
 }
 var batchFormat=@"{series} {{tvdb-{tvdb_id}}}\Season {season:00}\Volume {volume_number:00}\Disc {disc_number:00}\{date:yyyyMMdd}";
 var batch=FolderNames.PlanExisting([batchRaw],rawOptions,true,batchFormat,context.Series,"WRONG_");
 Check(batch.Count==3&&batch.All(j=>j.Encoding!.Output==FolderNames.Resolve(rawOptions.Output,batchFormat,batchContexts[batchSources.IndexOf(j.Source)])),"A shared raw folder resolves every disc's saved series, season, volume, number and date independently");
 Check(batch.All(j=>j.Series==batchContexts[batchSources.IndexOf(j.Source)].Series&&j.Season==batchContexts[batchSources.IndexOf(j.Source)].Season&&!j.Name.StartsWith("WRONG_")),"Batch jobs retain their own identities and existing prefixes despite a different selected series");
 var oneDestination=FolderNames.PlanExisting([batchRaw],rawOptions,true,"All shows",context.Series);
 Check(oneDestination.Select(j=>j.Encoding!.Output).Distinct().Count()==1&&oneDestination.Select(j=>j.Series!.Id).Distinct().Count()==2,"Distinct series metadata survives a shared compressed-output folder");
 var externalBatch=Path.Combine(temp,"external-batch");var seasonTwo=Directory.CreateDirectory(Path.Combine(externalBatch,"Season 02","Disc 03")).FullName;
 File.WriteAllText(Path.Combine(seasonTwo,"title_t00.mkv"),"external raw");
 var externalBatchJob=FolderNames.PlanExisting([externalBatch],rawOptions,true,@"{series}\Season {season:00}\Disc {disc_number:00}",naming.Series).Single();
 Check(externalBatchJob.Season=="2"&&externalBatchJob.Encoding!.Output.EndsWith(@"iCarly\Season 02\Disc 03"),"Legacy imports read explicit season and disc markers from the selected folder tree");
 var filenameBatch=Directory.CreateDirectory(Path.Combine(temp,"file-markers")).FullName;File.WriteAllText(Path.Combine(filenameBatch,"ICARLY_S3_V2_D1_t00.mkv"),"external raw");
 Check(FolderNames.PlanExisting([filenameBatch],rawOptions,true,@"Season {season:00}\Volume {volume_number:00}\Disc {disc_number:00}").Single().Encoding!.Output.EndsWith(@"Season 03\Volume 02\Disc 01"),"Explicit source-filename markers work when external folder names have no disc details");
 rejected=false;try{FolderNames.PlanExisting([filenameBatch],rawOptions,true,seasonFormat);}catch(IOException ex){rejected=ex.Message.Contains("ICARLY_S3_V2_D1_t00.mkv")&&ex.Message.Contains("{series}");}Check(rejected,"Missing series is reported against its source unless a fallback series is explicitly supplied");
 var unknownBatch=Directory.CreateDirectory(Path.Combine(temp,"unknown-batch")).FullName;var unknownSource=Path.Combine(unknownBatch,"title_t00.mkv");File.WriteAllText(unknownSource,"unknown disc");
 rejected=false;try{FolderNames.PlanExisting([unknownBatch],rawOptions,true,seasonFormat,naming.Series);}catch(IOException ex){rejected=ex.Message.Contains(unknownSource)&&ex.Message.Contains("{season}");}Check(rejected,"Unknown seasons cannot silently inherit the currently inserted disc's season");
 rejected=false;try{FolderNames.PlanExisting([unknownBatch],rawOptions,true,"Unsorted",naming.Series,"{disc_number}_");}catch(IOException ex){rejected=ex.Message.Contains(unknownSource)&&ex.Message.Contains("{disc_number}");}Check(rejected,"Unknown external filename-prefix tokens are reported per source");
 File.SetLastWriteTime(unknownSource,new DateTime(2024,6,7));
 Check(FolderNames.PlanExisting([unknownBatch],rawOptions,true,"{date:yyyyMMdd}").Single().Encoding!.Output.EndsWith("20240607"),"External files use their own modification date when rip dates are unavailable");
 if(args.Length>0){
  var fake=Path.GetFullPath(args[0]);var success=Disc.NewFolder(temp,"SUCCESS");await Disc.Rip(fake,"disc:0",120,success,_=>{},CancellationToken.None);Check(File.Exists(Path.Combine(success,"title_t00.mkv")),"Successful rip retains raw output");
  var flowContext=naming with{DiscNumber="5"};var flow=NewRip(flowContext);await Disc.Rip(fake,"disc:0",120,flow.Files,_=>{},CancellationToken.None);
  var flowOptions=rawOptions with{Cli=fake};var flowJob=flow.Publish("{volume_number}_{disc_number}_",flowContext,flowOptions,CancellationToken.None).Single();
  var flowName=await Engine.Encode(flowJob,flowOptions,_=>{},CancellationToken.None);
  Check(flowName=="2_5_title_t00.mp4"&&File.ReadAllText(flowJob.Source)=="test raw content"&&File.ReadAllText(Path.Combine(flowOptions.Output,flowName))=="test encoded content","Rip-to-compression workflow preserves raw data and requested filenames");
  Environment.SetEnvironmentVariable("RIPLEY_TEST_MODE","partial");var partial=Disc.NewFolder(temp,"PARTIAL");rejected=false;try{await Disc.Rip(fake,"disc:0",120,partial,_=>{},CancellationToken.None);}catch(IOException){rejected=true;}Check(rejected&&File.Exists(Path.Combine(partial,"title_t00.mkv")),"Partial rip retained but rejected for compression");
  Environment.SetEnvironmentVariable("RIPLEY_TEST_MODE","wait");var waiting=Disc.NewFolder(temp,"WAITING");using var stop=new CancellationTokenSource();var ripping=Disc.Rip(fake,"disc:0",120,waiting,_=>{},stop.Token);
  var encode=Engine.Scan([b],output,true,".mkv").Single();var encoded=await Engine.Encode(encode,new Options(fake,"test","",false,output,".mkv"),_=>{},CancellationToken.None);Check(!ripping.IsCompleted&&File.Exists(Path.Combine(output,encoded)),"Compression completes while another rip is active");
  stop.Cancel();rejected=false;try{await ripping;}catch(OperationCanceledException){rejected=true;}Check(rejected&&File.Exists(Path.Combine(waiting,"title_t00.mkv")),"Cancelling rip preserves raw files");Environment.SetEnvironmentVariable("RIPLEY_TEST_MODE",null);
 }
 if(args.Length>2){
  var presets=Presets.Load(args[2]);var actual=Presets.Snapshot(args[2],presets[0],Path.GetFullPath(args[1]),output,temp,"nvdec");await Presets.Validate(actual,CancellationToken.None);Check(true,"Installed custom preset imports and NVIDIA decode is detected");
  if(args.Length>3){var sample=Path.GetFullPath(args[3]);var stat=new FileInfo(sample);var job=new Job{Source=sample,SourceSize=stat.Length,SourceModified=stat.LastWriteTimeUtc,Name="real-encode.mkv"};await Engine.Encode(job,actual,_=>{},CancellationToken.None);Check(new FileInfo(Path.Combine(output,job.Name)).Length>0,"Real HandBrake encode with installed custom preset");}
 }
 Console.WriteLine($"{passed} checks passed");
}finally{Environment.SetEnvironmentVariable("RIPLEY_TEST_MODE",null);Directory.Delete(temp,true);}
