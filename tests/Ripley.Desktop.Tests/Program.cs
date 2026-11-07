using Ripley;
using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;

class Program
{
    static int passed;
    static void Check(bool condition, string name) { if(!condition) throw new Exception(name); Console.WriteLine("PASS " + name); passed++; }
    static readonly BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
    static object? Call(MainWindow window, string name, params object?[] args) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, args);
    static object? Field(MainWindow window, string name) => typeof(MainWindow).GetField(name, flags)!.GetValue(window);
    static void Set(MainWindow window, string name, object? value) => typeof(MainWindow).GetField(name, flags)!.SetValue(window, value);
    static T Control<T>(MainWindow window, string name) where T : FrameworkElement => (T)window.FindName(name);
    static async Task Until(Func<bool> condition) { var limit = DateTime.UtcNow.AddSeconds(10); while(!condition()) { if(DateTime.UtcNow > limit) throw new TimeoutException("Desktop operation did not finish."); await Task.Delay(25); } }

    [STAThread] static int Main(string[] args)
    {
        var temp = Path.Combine(Path.GetTempPath(), "RipleyDesktopTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try {
            var app = new App(); app.InitializeComponent();
            var window = new MainWindow(Path.Combine(temp, "state"));
            var task = window.Dispatcher.InvokeAsync(() => Run(window, temp, Path.GetFullPath(args[0]), args.Length > 1 ? args[1] : null)).Task.Unwrap();
            var frame = new DispatcherFrame();
            _ = task.ContinueWith(_ => window.Dispatcher.BeginInvoke(() => frame.Continue = false));
            Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult();
            Console.WriteLine($"{passed} desktop checks passed"); return 0;
        }
        catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { Environment.SetEnvironmentVariable("RIPLEY_TEST_MODE", null); Directory.Delete(temp, true); }
    }

    static async Task Run(MainWindow window,string temp,string fake,string? renders)
    {
        await CheckDriveNotifications(temp);
        Check(window.Title=="Ripley"&&window.WindowStyle==WindowStyle.None&&WindowChrome.GetWindowChrome(window).CaptionHeight==38,"Ripley keeps Scout's custom shell");
        foreach(var page in new[]{"Rip","Compress","Settings"}) {
            Call(window,"ShowPage",page);
            Check(Control<FrameworkElement>(window,page+"Page").Visibility==Visibility.Visible&&new[]{"Rip","Compress","Settings"}.Where(p=>p!=page).All(p=>Control<FrameworkElement>(window,p+"Page").Visibility==Visibility.Collapsed),"Navigation selects only "+page);
        }
        Text(window,"DiscLabel").Text="ICARLY_S1_V2_D2";
        Check(Text(window,"Season").Text=="1"&&Text(window,"Volume").Text=="2"&&Text(window,"DiscNumber").Text=="2","Disc label still fills season, volume and disc");
        Text(window,"DiscLabel").Text="UNKNOWN";
        Check(Text(window,"Season").Text==""&&Text(window,"Volume").Text==""&&Text(window,"DiscNumber").Text=="","A new unknown disc clears stale values");
        var preset=Path.Combine(temp,"preset.json");File.WriteAllText(preset,"""{"PresetList":[{"PresetName":"Custom test","FileFormat":"av_mkv","VideoEncoder":"x265_10bit","Type":1}]}""");
        Configure(window,temp,fake,preset);
        CheckHistory(window,temp);
        await CheckRip(window,temp,fake);
        await CheckQueues(window,temp,fake);
        await CheckStopAndPause(window,temp,fake);
        Call(window,"SavePreferences",window,new RoutedEventArgs());
        var saved=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(temp,"state","settings.json")))!;
        Check(saved["FilenamePrefix"]!.ToString()=="{volume_number}_{disc_number}_"&&saved["CompressionPrefix"]!.ToString()=="EXTERNAL_","Raw and compression prefixes save independently");
        var restored=new MainWindow(Path.Combine(temp,"state"));
        Check(Jobs(restored).Count==Jobs(window).Count&&!((bool)Field(restored,"encoding")!),"Restoring multiple queues never starts processing");
        var history=(ObservableCollection<RipSession>)Field(restored,"ripHistory")!;
        var completed=history.First(h=>h.Status=="Completed");Control<ListBox>(restored,"HistoryList").SelectedItem=completed;
        Check(((List<Job>)Field(restored,"lastRipFiles")!).Select(j=>j.Source).SequenceEqual(completed.Files.Select(j=>j.Source))&&!Control<Button>(restored,"RipButton").IsEnabled&&Control<Button>(restored,"QueueRipButton").Visibility==Visibility.Visible,"Completed history restores exact raw files for queueing without reripping");
        completed.Status="Ripping";Call(restored,"SaveHistory");
        var recovered=new MainWindow(Path.Combine(temp,"state"));
        Check(((ObservableCollection<RipSession>)Field(recovered,"ripHistory")!).Single(h=>h.Id==completed.Id).Status=="Interrupted"&&!(bool)Field(recovered,"discBusy")!,"Interrupted history is recovered without restarting a physical disc operation");
        if(renders!=null) {
            Directory.CreateDirectory(renders);
            Call(window,"ShowCompressionQueue");await Render(window,1440,920,Path.Combine(renders,"compress.png"));await Render(window,1080,740,Path.Combine(renders,"compress-minimum.png"));
            Call(window,"ShowSources",window,new RoutedEventArgs());await Render(window,1440,920,Path.Combine(renders,"compress-sources.png"));
            Call(window,"ShowCompressionSettings",window,new RoutedEventArgs());await Render(window,1440,920,Path.Combine(renders,"compress-output.png"));await Render(window,1080,740,Path.Combine(renders,"compress-options.png"));
            Call(window,"ShowPage","Rip");await Render(window,1080,740,Path.Combine(renders,"rip.png"));
        }
        await CheckClear(window,temp,fake);
    }
    static TextBox Text(MainWindow w,string name)=>Control<TextBox>(w,name);
    static ObservableCollection<Job> Jobs(MainWindow w)=>(ObservableCollection<Job>)Field(w,"jobs")!;
    static ObservableCollection<string> Folders(MainWindow w)=>(ObservableCollection<string>)Field(w,"folders")!;
    static async Task CheckDriveNotifications(string temp)
    {
        var drives=new List<OpticalDrive>{new(0,"DVD","ICARLY_S1_V2_D2","E:"),new(1,"DVD","","F:")};
        var reads=0;
        var w=new MainWindow(Path.Combine(temp,"drive-state"),()=>{Interlocked.Increment(ref reads);return drives.ToList();});
        Call(w,"NewRipley",w,new RoutedEventArgs());
        await (Task)Call(w,"RefreshDriveList")!;
        Check(Control<ComboBox>(w,"DriveChoice").Items.Count==2&&Text(w,"DiscLabel").Text=="ICARLY_S1_V2_D2"&&Text(w,"Volume").Text=="2","Windows drive discovery lists empty drives and fills the loaded disc without launching MakeMKV");
        Text(w,"Volume").Text="7";var inspected=new DiscInfo();Set(w,"inspected",inspected);
        await (Task)Call(w,"RefreshDriveList")!;
        Check(Text(w,"Volume").Text=="7"&&ReferenceEquals(Field(w,"inspected"),inspected),"Refreshing unchanged media preserves manual disc values and inspection");
        Call(w,"SavePreferences",w,new RoutedEventArgs());
        var restored=new MainWindow(Path.Combine(temp,"drive-state"),()=>drives.ToList());await (Task)Call(restored,"RefreshDriveList")!;
        Check(Text(restored,"Volume").Text=="7","Startup detection of the same disc preserves a saved draft's manual values");
        NotifyDrive(w,0x8000,1u<<5);
        drives[1]=drives[1] with {Label="OTHER_S3_D1"};await (Task)Call(w,"RefreshDriveList")!;
        Check(((OpticalDrive)Control<ComboBox>(w,"DriveChoice").SelectedItem).Device=="E:"&&Text(w,"Volume").Text=="7","Media in another drive does not replace the selected disc or its manual values");
        NotifyDrive(w,0x8004,1u<<4);drives[0]=drives[0] with {Label=""};await (Task)Call(w,"RefreshDriveList")!;
        Check(Control<ComboBox>(w,"DriveChoice").Items.Count==2&&Text(w,"DiscLabel").Text==""&&Text(w,"Season").Text==""&&Field(w,"inspected")==null,"Media removal retains the physical drive and clears stale draft metadata");
        NotifyDrive(w,0x8000,1u<<4);drives[0]=drives[0] with {Label="ICARLY_S2_V1_D1"};await (Task)Call(w,"RefreshDriveList")!;
        Check(Text(w,"Season").Text=="2"&&Text(w,"DiscNumber").Text=="1"&&!((bool)Field(w,"discBusy")!)&&Jobs(w).Count==0,"Insertion updates detected season and disc without starting ripping or compression");
        Call(w,"SetDiscBusy",true);var before=reads;
        NotifyDrive(w,0x8000,1u<<4);await (Task)Call(w,"RefreshDriveList")!;
        Check(reads==before&&(bool)Field(w,"driveRefreshPending")!&&Text(w,"Season").Text=="2","A media event during an active disc operation waits without scanning or changing its settings");
        Call(w,"SetDiscBusy",false);drives[0]=drives[0] with {Label="ICARLY_S3_V1_D1"};await (Task)Call(w,"RefreshDriveList")!;
        Check(reads==before+1&&Text(w,"Season").Text=="3","Deferred media refresh runs after the disc operation finishes");
        Call(w,"SetRipSessionStatus","Completed");var session=(RipSession)Field(w,"currentRipSession")!;
        NotifyDrive(w,0x8004,1u<<4);drives[0]=drives[0] with {Label=""};await (Task)Call(w,"RefreshDriveList")!;
        Check(session.DiscLabel=="ICARLY_S3_V1_D1"&&Text(w,"Season").Text=="3"&&((OpticalDrive)Control<ComboBox>(w,"DriveChoice").SelectedItem).Label=="","Disc changes refresh the physical drive while preserving completed history");
        NotifyDrive(w,0x8000,1u<<4,0);
        Check(!(bool)Field(w,"driveRefreshPending")!,"Non-media volume notifications are ignored");
        Call(w,"DriveNotificationHook",IntPtr.Zero,0x219,new IntPtr(7),IntPtr.Zero,false);
        drives.Clear();await (Task)Call(w,"RefreshDriveList")!;
        Check(Control<ComboBox>(w,"DriveChoice").Items.Count==0&&session.DiscLabel=="ICARLY_S3_V1_D1","Device disconnect notifications remove stale drives without changing saved history");
        var snapshot=new TaskCompletionSource<List<OpticalDrive>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var racing=new MainWindow(Path.Combine(temp,"drive-race"),()=>snapshot.Task.GetAwaiter().GetResult());
        var scanning=(Task)Call(racing,"RefreshDriveList")!;
        NotifyDrive(racing,0x8000,1u<<4);
        snapshot.SetResult([new(0,"DVD","STALE_S1_D1","E:")]);await scanning;
        Check(Text(racing,"DiscLabel").Text==""&&(bool)Field(racing,"driveRefreshPending")!,"A new media event discards an older in-flight drive scan");
        snapshot=new(TaskCreationOptions.RunContinuationsAsynchronously);snapshot.SetResult([new(0,"DVD","CURRENT_S2_D1","E:")]);
        await (Task)Call(racing,"RefreshDriveList")!;
        Check(Text(racing,"DiscLabel").Text=="CURRENT_S2_D1"&&!(bool)Field(racing,"driveRefreshPending")!,"The next settled scan applies the latest disc metadata");
    }
    static void NotifyDrive(MainWindow w,int change,uint mask,short flags=1)
    {
        var data=Marshal.AllocHGlobal(20);
        try {
            Marshal.WriteInt32(data,20);Marshal.WriteInt32(data,4,2);Marshal.WriteInt32(data,8,0);Marshal.WriteInt32(data,12,unchecked((int)mask));Marshal.WriteInt16(data,16,flags);
            Call(w,"DriveNotificationHook",IntPtr.Zero,0x219,new IntPtr(change),data,false);
        }
        finally {Marshal.FreeHGlobal(data);}
    }
    static void CheckHistory(MainWindow w,string temp)
    {
        var history=(ObservableCollection<RipSession>)Field(w,"ripHistory")!;
        Control<Button>(w,"NewRipleyButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var first=history[0];
        Check(history.Count==1&&first.Title=="New Ripley"&&Text(w,"DiscLabel").Text==""&&Text(w,"Season").Text==""&&Text(w,"RawFolder").Text==Path.Combine(temp,"raw"),"New Ripley creates a fresh history item and clears disc values while retaining folder preferences");
        Text(w,"DiscLabel").Text="ICARLY_S1_V2_D2";Text(w,"DiscNumber").Text="7";
        Control<Button>(w,"NewRipleyButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var second=history[0];Text(w,"DiscLabel").Text="ICARLY_S2_V3_D4";
        Control<ListBox>(w,"HistoryList").SelectedItem=first;
        Check(history.Count==2&&first.Id!=second.Id&&Text(w,"Season").Text=="1"&&Text(w,"DiscNumber").Text=="7","Switching history restores independent disc settings and manual values");
        Control<ListBox>(w,"HistoryList").SelectedItem=second;
        Check(Text(w,"Season").Text=="2"&&Text(w,"Volume").Text=="3"&&Text(w,"DiscNumber").Text=="4","A second history item retains its own detected disc values");
        Call(w,"SavePreferences",w,new RoutedEventArgs());var restored=new MainWindow(Path.Combine(temp,"state"));
        Check(((ObservableCollection<RipSession>)Field(restored,"ripHistory")!).Count==2&&((RipSession)Field(restored,"currentRipSession")!).Id==second.Id&&Text(restored,"Season").Text=="2","History and its selected draft survive reopening");
        Control<ListBox>(w,"HistoryList").SelectedItem=first;Text(w,"DiscNumber").Text="2";
        Call(w,"ShowCompressionQueue");Call(w,"OpenRipHistory",new Button{DataContext=first},new RoutedEventArgs());
        Check(Control<ScrollViewer>(w,"RipPage").Visibility==Visibility.Visible&&history.Count==2,"Reopening the selected history item returns from compression without creating a new session");
        Check(w.FindName("NavRip")==null&&w.FindName("NavCompress")==null&&Control<Button>(w,"SideCompress").Visibility==Visibility.Visible,"Sidebar navigation replaces the Rip and Compress header tabs");
    }
    static void Configure(MainWindow w,string temp,string fake,string preset)
    {
        Text(w,"RawFolder").Text=Path.Combine(temp,"raw");Text(w,"RawTemplate").Text=@"{series}\Season {season:00}";
        Text(w,"Destination").Text=Path.Combine(temp,"compressed");Text(w,"OutputTemplate").Text=@"{series}\Season {season:00}";
        Text(w,"FilenamePrefix").Text="{volume_number}_{disc_number}_";Text(w,"CompressionPrefix").Text="EXTERNAL_";
        Text(w,"MakeMkv").Text=fake;Text(w,"Cli").Text=fake;Text(w,"PresetFile").Text=preset;Control<ComboBox>(w,"Hardware").SelectedIndex=1;Call(w,"ReloadPresets",(object?)null);
        Set(w,"series",new SeriesIdentity("tvdb","82493","iCarly","2007",""));Call(w,"DisplaySeries");
        Set(w,"compressionSeries",new SeriesIdentity("tvdb","999","Different fallback","1999",""));Call(w,"DisplayCompressionSeries");
        var drive=Control<ComboBox>(w,"DriveChoice");drive.ItemsSource=new[]{new OpticalDrive(0,"DVD","ICARLY_S1_V2_D2","E:")};drive.SelectedIndex=0;drive.ApplyTemplate();
        Check(drive.Template.FindName("PART_Popup",drive)!=null,"Drive dropdown keeps its dark popup");
    }
    static async Task CheckRip(MainWindow w,string temp,string fake)
    {
        var preset=Control<ComboBox>(w,"PresetChoice").SelectedItem;
        Control<ComboBox>(w,"PresetChoice").SelectedItem=null;Text(w,"Cli").Text=Path.Combine(temp,"missing.exe");var output=Text(w,"Destination").Text;Text(w,"Destination").Text="";
        await (Task)Call(w,"RipDiscCore")!;
        var ripped=(List<Job>)Field(w,"lastRipFiles")!;
        Check(ripped.Count==1&&File.Exists(ripped[0].Source)&&Jobs(w).Count==0,"Rip saves raw files without HandBrake or compression setup and never queues them automatically");
        Check(Control<Button>(w,"QueueRipButton").Visibility==Visibility.Visible&&Control<Button>(w,"QueueRipButton").Content.ToString()=="Add to queue","Completed rips offer an explicit Add to queue action");
        var first=ripped[0].Source;
        await (Task)Call(w,"RipDiscCore")!;
        Check(File.Exists(first)&&((List<Job>)Field(w,"lastRipFiles")!)[0].Source!=first,"Repeated raw filenames remain collision safe");
        Control<ComboBox>(w,"PresetChoice").SelectedItem=preset;Text(w,"Cli").Text=fake;Text(w,"Destination").Text=output;
        await (Task)Call(w,"AddCompletedRip")!;
        Check(Jobs(w).Count==1&&Jobs(w)[0].Status=="Waiting"&&!Directory.Exists(output)&&Control<Grid>(w,"CompressPage").Visibility==Visibility.Visible,"Adding a completed rip queues only that disc's files and leaves idle compression stopped");
        await (Task)Call(w,"AddCompletedRip")!;Check(Jobs(w).Count==1,"Adding the same completed rip twice does not duplicate jobs");
        var progress=(RipQueue)Field(w,"ripQueue")!;Check(progress.Files.Single().Status=="Saved","Ripping queue retains confirmed filenames and completion");
        Environment.SetEnvironmentVariable("RIPLEY_TEST_MODE","partial");await (Task)Call(w,"RipDiscCore")!;Environment.SetEnvironmentVariable("RIPLEY_TEST_MODE",null);
        Check(((List<Job>)Field(w,"lastRipFiles")!).Count==0&&Jobs(w).Count==1&&Directory.GetFiles(Text(w,"RawFolder").Text,"title_t00.mkv",SearchOption.AllDirectories).Length==1,"An incomplete rip keeps raw data and offers no completed-rip queue action");
    }
    static List<Job> MakeDisc(string root,string label,string series,string id,string season,int count)
    {
        var ctx=new FolderContext(new SeriesIdentity("tvdb",id,series,"2007",""),season,label,new DateTime(2025,1,1),"1","1");
        var rip=RawRip.Create(root,new{series_identity=ctx.Series,season,volume_number=ctx.Volume,disc_number=ctx.DiscNumber,disc_label=label,rip_date=ctx.Date});
        for(var i=0;i<count;i++)File.WriteAllText(Path.Combine(rip.Files,$"title_t{i:00}.mkv"),"raw data");
        return rip.Publish("{season}_{disc_label}_",ctx,null,CancellationToken.None);
    }
    static async Task CheckQueues(MainWindow w,string temp,string fake)
    {
        var root=Path.Combine(temp,"mixed-rips");MakeDisc(root,"ICARLY_S2_D1","iCarly","82493","2",2);MakeDisc(root,"OTHER_S3_D1","Other series","123","3",1);
        Folders(w).Add(root);Text(w,"Season").Text="not a season";Text(w,"Volume").Text="not a volume";
        Call(w,"ShowSources",w,new RoutedEventArgs());await (Task)Call(w,"QueueExisting")!;
        Check(Jobs(w).Count==4&&Jobs(w).All(j=>j.Status=="Waiting")&&!((bool)Field(w,"encoding")!),"Adding existing rips does not start idle compression");
        Check(Jobs(w).Select(j=>j.QueueId).Distinct().Count()==3&&Jobs(w).Select(j=>j.Series!.Id).Distinct().Count()==2&&Jobs(w).Any(j=>j.Season=="3"),"Mixed discs become separate queues with their own series and season");
        var first=Jobs(w)[0].QueueId;
        var firstJob=Jobs(w)[0];var requestedOutput=firstJob.OutputPath;
        Directory.CreateDirectory(Path.GetDirectoryName(requestedOutput)!);File.WriteAllText(requestedOutput,"existing output");
        await (Task)Call(w,"StartQueues",first)!;
        Check(firstJob.OutputPath!=requestedOutput&&Path.IsPathFullyQualified(firstJob.Source)&&Path.IsPathFullyQualified(firstJob.OutputPath)&&File.Exists(firstJob.OutputPath)&&File.ReadAllText(requestedOutput)=="existing output","Displayed paths retain the full source and follow the final collision-safe output filename");
        Check(Jobs(w).Where(j=>j.QueueId==first).All(j=>j.Status=="Completed")&&Jobs(w).Where(j=>j.QueueId!=first).All(j=>j.Status=="Waiting"),"Start selected queue processes only that disc");
        var waiting=Jobs(w).Where(j=>j.Status=="Waiting").ToArray();
        var second=waiting.First(j=>j.QueueId==waiting[0].QueueId&&!ReferenceEquals(j,waiting[0]));Jobs(w).Remove(second);Jobs(w).Add(second);
        var order=new List<string>();foreach(var job in waiting)job.PropertyChanged+=(_,e)=>{if(e.PropertyName=="Status"&&job.Status=="Encoding")order.Add(job.QueueId);};
        await (Task)Call(w,"StartQueues",(object?)null)!;
        Check(order.Count==3&&order[0]==order[1]&&order[1]!=order[2],"Start all finishes each disc queue before moving to the next even when rows are interleaved");
        Check(Jobs(w).All(j=>j.Status=="Completed")&&Jobs(w).All(j=>File.Exists(Path.Combine(j.Encoding!.Output,j.Name))),"Start all queues processes every disc in order");
        Check(Jobs(w).All(j=>{var doc=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(j.Encoding!.Output,j.Name+".ripley.json")))!;return doc["season"]!.ToString()==j.Season&&doc["series_identity"]!["Id"]!.ToString()==j.Series!.Id;}),"Each compressed sidecar retains its source identity");
        var counts=Jobs(w).Count;await (Task)Call(w,"QueueExisting")!;Check(Jobs(w).Count==counts,"Repeated batch additions report existing entries without duplicates");
        File.WriteAllText(Path.Combine(root,"unknown.mkv"),"unknown");
        bool failed=false;try{await (Task)Call(w,"QueueExisting")!;}catch(IOException){failed=true;}
        Check(failed&&Jobs(w).Count==counts,"Missing per-file metadata rejects a batch without partially changing the queues");
        var unknown=Path.GetFullPath(Path.Combine(root,"unknown.mkv"));File.Delete(unknown);
    }
    static async Task AddDisc(MainWindow w,string temp,string label,int count=1)
    {
        var root=Path.Combine(temp,label);MakeDisc(root,label,"iCarly","82493","1",count);Folders(w).Clear();Folders(w).Add(root);await (Task)Call(w,"QueueExisting")!;
    }
    static async Task CheckStopAndPause(MainWindow w,string temp,string fake)
    {
        await AddDisc(w,temp,"PAUSE_S1_D1",2);
        Environment.SetEnvironmentVariable("RIPLEY_TEST_MODE","encode-delay");var running=(Task)Call(w,"StartQueues",(object?)null)!;
        await Until(()=>Jobs(w).Any(j=>j.Status=="Encoding"));
        Call(w,"PauseAfterCurrent",w,new RoutedEventArgs());
        Check(((bool)Field(w,"pauseAfterCurrent")!)&&Control<Button>(w,"StartButton").Content.ToString()=="Add to queue","Pause after current file changes new additions to waiting");
        await AddDisc(w,temp,"WAIT_FOR_PAUSE_D2");await running;Environment.SetEnvironmentVariable("RIPLEY_TEST_MODE",null);
        Check(Jobs(w).Where(j=>j.QueueName.Contains("WAIT_FOR_PAUSE_D2")).All(j=>j.Status=="Waiting"),"Files added while pausing do not extend the current run");
        var pausedJobs=Jobs(w).Where(j=>j.QueueName.Contains("PAUSE_S1_D1")).ToArray();
        Check(pausedJobs.Count(j=>j.Status=="Completed")==1&&pausedJobs.Count(j=>j.Status=="Paused")==1&&File.Exists(Path.Combine(pausedJobs[0].Encoding!.Output,pausedJobs[0].Name)),"Graceful pause saves the current file and leaves the next file paused");
        Environment.SetEnvironmentVariable("RIPLEY_TEST_MODE","encode-delay");running=(Task)Call(w,"StartQueues",(object?)null)!;await Until(()=>Jobs(w).Any(j=>j.Status=="Encoding"));
        Check(Control<Button>(w,"StartButton").Content.ToString()=="Add to running queue","Add action clearly identifies a running queue");
        await AddDisc(w,temp,"JOIN_S1_D2");await running;Environment.SetEnvironmentVariable("RIPLEY_TEST_MODE",null);
        Check(Jobs(w).Where(j=>j.QueueName.Contains("JOIN_S1_D2")).All(j=>j.Status=="Completed"),"Explicit Add to running queue includes a new disc without another start action");
        await AddDisc(w,temp,"STOP_S1_D3",2);
        Environment.SetEnvironmentVariable("RIPLEY_TEST_MODE","encode-wait");running=(Task)Call(w,"StartQueues",(object?)null)!;
        await Until(()=>Directory.GetFiles(Text(w,"Destination").Text,"encoding.mkv",SearchOption.AllDirectories).Length>0);
        Environment.SetEnvironmentVariable("RIPLEY_TEST_MODE","wait");Text(w,"Season").Text="1";Text(w,"Volume").Text="2";var ripping=(Task)Call(w,"RipDiscCore")!;
        var ripProgress=(RipQueue)Field(w,"ripQueue")!;await Until(()=>ripProgress.Current?.Progress==50);
        var current=Field(w,"currentRipSession");var history=(ObservableCollection<RipSession>)Field(w,"ripHistory")!;
        Control<ListBox>(w,"HistoryList").SelectedItem=history.First(h=>h!=current);
        Check(!Control<Button>(w,"NewRipleyButton").IsEnabled&&ReferenceEquals(Field(w,"currentRipSession"),current)&&ReferenceEquals(Control<ListBox>(w,"HistoryList").SelectedItem,current),"An active disc session cannot be replaced by new or historical settings");
        Call(w,"ShowCompressionQueue");Call(w,"OpenRipHistory",new Button{DataContext=current},new RoutedEventArgs());
        Check(Control<ScrollViewer>(w,"RipPage").Visibility==Visibility.Visible&&(bool)Field(w,"discBusy")!,"The active rip remains reachable from history while compression navigation is available");
        Check((bool)Field(w,"encoding")!&&Jobs(w).Count(j=>j.QueueName.Contains("STOP_S1_D3"))==2,"Ripping while compression runs does not extend the queue");
        Call(w,"StopAllOperations");await Task.WhenAll(running,ripping);Environment.SetEnvironmentVariable("RIPLEY_TEST_MODE",null);
        Check(!(bool)Field(w,"encoding")!&&!(bool)Field(w,"discBusy")!&&Jobs(w).Where(j=>j.QueueName.Contains("STOP_S1_D3")).All(j=>j.Status=="Paused"),"Stop all interrupts both operations and retains paused jobs");
        Check(Directory.GetFiles(Text(w,"Destination").Text,"encoding.mkv",SearchOption.AllDirectories).Length==0&&ripProgress.Files[0].Status=="Kept · unverified"&&Jobs(w).Where(j=>j.Status=="Completed").All(j=>File.Exists(Path.Combine(j.Encoding!.Output,j.Name))),"Stop all cleans unfinished compressed output while retaining raw and completed files");
    }
    static async Task CheckClear(MainWindow w,string temp,string fake)
    {
        var raw=Jobs(w)[0].Source;var done=Jobs(w).First(j=>j.Status=="Completed");var completed=Path.Combine(done.Encoding!.Output,done.Name);
        Environment.SetEnvironmentVariable("RIPLEY_TEST_MODE","encode-wait");var running=(Task)Call(w,"StartQueues",(object?)null)!;
        await Until(()=>Directory.GetFiles(Text(w,"Destination").Text,"encoding.mkv",SearchOption.AllDirectories).Length>0);
        using var disc=new CancellationTokenSource();Set(w,"discCancel",disc);Call(w,"SetDiscBusy",true);
        var clear=(Task)Call(w,"ClearQueueEntries")!;
        Environment.SetEnvironmentVariable("RIPLEY_TEST_MODE",null);
        var replacement=MakeDisc(Path.Combine(temp,"after-clear"),"AFTER_CLEAR_D1","iCarly","82493","1",1);
        var options=done.Encoding!;var planned=FolderNames.PlanFiles(replacement,[Path.GetDirectoryName(replacement[0].Source)!],options,"iCarly");
        Call(w,"Enqueue",planned,planned[0].Encoding!,"Custom test");await clear;await running;
        Check(!disc.IsCancellationRequested&&Jobs(w).Count==1&&Jobs(w)[0].Status=="Waiting"&&!((bool)Field(w,"encoding")!),"Clear stops encoding without cancelling ripping or starting files added during cleanup");
        Check(File.Exists(raw)&&File.Exists(completed),"Clearing queues preserves raw and completed media");
        Call(w,"SetDiscBusy",false);Set(w,"discCancel",null);
    }
    static async Task Render(MainWindow window,int width,int height,string path) {
        var root=(FrameworkElement)window.Content;root.Measure(new Size(width,height));root.Arrange(new Rect(0,0,width,height));root.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);root.UpdateLayout();
        var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(root);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(path);encoder.Save(stream);
    }
}
