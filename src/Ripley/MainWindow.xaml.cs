using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
namespace Ripley;

public partial class MainWindow : Window
{
    readonly ObservableCollection<string> folders = new();
    readonly ObservableCollection<Job> jobs = new();
    readonly string stateRoot;
    string SettingsPath => Path.Combine(stateRoot,"settings.json");
    string QueuePath => Path.Combine(stateRoot,"queue.json");
    CancellationTokenSource? encodingCancel, discCancel;
    bool encoding, discBusy, adding, paused, namingReady, updatingSeason;
    string lastSeasonLabel="";
    DiscInfo? inspected;
    string inspectedSource = "";
    SeriesIdentity? series, compressionSeries;
    List<Job> lastRipFiles=new();
    bool pauseAfterCurrent;
    public MainWindow() : this(AppStorage.Prepare(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))) { }
    public MainWindow(string stateDirectory,Func<List<OpticalDrive>>? driveReader=null)
    {
        readOpticalDrives=driveReader??ReadOpticalDrives;
        stateRoot = Path.GetFullPath(stateDirectory);
        InitializeComponent(); Folders.ItemsSource = folders; QueueGrid.ItemsSource = jobs;RipGrid.ItemsSource=ripQueue.Files;
        RawTemplate.Text=FolderNames.DefaultRaw; OutputTemplate.Text=FolderNames.DefaultCompressed;
        var bundled = Path.Combine(AppContext.BaseDirectory,"handbrake","HandBrakeCLI.exe");
        Cli.Text = File.Exists(bundled) ? bundled : "HandBrakeCLI.exe";
        MakeMkv.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),"MakeMKV","makemkvcon64.exe");
        var hb = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"HandBrake");
        PresetFile.Text = Path.Combine(hb,"presets.json"); GuiSettings.Text = Path.Combine(hb,"settings.json");
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        ScoutFolder.Text = Path.Combine(local,Directory.Exists(Path.Combine(local,"Episcout")) ? "Episcout" : "Scout");
        string? preferred = null;
        try {
            if(File.Exists(SettingsPath)) {
                var s = JsonSerializer.Deserialize<Saved>(File.ReadAllText(SettingsPath));
                if(s != null) {
                    if(!string.IsNullOrWhiteSpace(s.Cli) && s.Cli != @"C:\tools\HandBrakeCLI.exe") Cli.Text=s.Cli;
                    if(!string.IsNullOrWhiteSpace(s.MakeMkv)) MakeMkv.Text=s.MakeMkv;
                    if(!string.IsNullOrWhiteSpace(s.Json)) PresetFile.Text=s.Json;
                    if(!string.IsNullOrWhiteSpace(s.GuiSettings)) GuiSettings.Text=s.GuiSettings;
                    if(!string.IsNullOrWhiteSpace(s.ScoutFolder)) ScoutFolder.Text=s.ScoutFolder;
                    RawFolder.Text=s.Raw ?? ""; Destination.Text=s.Output ?? ""; Minimum.Text=(s.Minimum ?? 120).ToString();
                    Hardware.SelectedIndex=Math.Clamp(s.Hardware,0,3); Recursive.IsChecked=s.Recursive; Season.Text=s.Season ?? "";
                    series=s.Series; compressionSeries=s.CompressionSeries??s.Series; CompressionPrefix.Text=s.CompressionPrefix??s.FilenamePrefix??""; preferred=s.Preset; foreach(var f in s.Folders ?? []) folders.Add(f);
                    AutoSeason.IsChecked=s.AutoDetectSeason??true;
                    Volume.Text=s.VolumeNumber??""; DiscNumber.Text=s.DiscNumber??""; FilenamePrefix.Text=s.FilenamePrefix??"";
                    RawTemplate.Text=s.RawFormat??FolderNames.DefaultRaw;OutputTemplate.Text=s.CompressedFormat??FolderNames.DefaultCompressed;
                }
            }
            foreach(var job in QueueStore.Read(QueuePath)) jobs.Add(job);
            paused=jobs.Any(j=>j.Status=="Paused");
            if(paused) Status.Text="Saved queue restored. Resume when ready; no work starts automatically.";
        } catch(Exception ex) { Error(ex); }
        ReloadPresets(preferred); DisplaySeries(); DisplayCompressionSeries(); UpdateQueue(); namingReady=true; UpdateNamingPreview(); InitializeHistory(); InitializeShell();
        Closing += (_, e) => { if(encoding || discBusy || adding || clearingQueue) { e.Cancel=true; MessageBox.Show(this,"Pause compression and cancel any disc operation before closing. Raw files are kept.","Ripley is working"); } else { Save(); PersistQueue(); } };
    }
    record Saved(string? Cli,string? Preset,string? Json,string? Output,bool Recursive,string[]? Folders,string? MakeMkv,string? Raw,int? Minimum,string? ScoutFolder,SeriesIdentity? Series,string? Season,string? GuiSettings,int Hardware,string? RawFormat=null,string? CompressedFormat=null,bool? AutoDetectSeason=null,string? VolumeNumber=null,string? DiscNumber=null,string? FilenamePrefix=null,string? CompressionPrefix=null,SeriesIdentity? CompressionSeries=null);
    bool Save() { try { SaveHistory();QueueStore.Write(SettingsPath,new Saved(Cli.Text,(PresetChoice.SelectedItem as HandBrakePreset)?.Name,PresetFile.Text,Destination.Text,Recursive.IsChecked==true,folders.ToArray(),MakeMkv.Text,RawFolder.Text,int.TryParse(Minimum.Text,out var m)?m:120,ScoutFolder.Text,series,Season.Text,GuiSettings.Text,Hardware.SelectedIndex,RawTemplate.Text,OutputTemplate.Text,AutoSeason.IsChecked,Volume.Text,DiscNumber.Text,FilenamePrefix.Text,CompressionPrefix.Text,compressionSeries)); return true; } catch(Exception ex) { Error(new IOException("Could not save settings: "+ex.Message,ex)); return false; } }
    void PersistQueue() => QueueStore.Write(QueuePath,jobs.ToList());
    void Append(string text) { Log.AppendText(text+Environment.NewLine); if(Log.Text.Length>45000) Log.Text=Log.Text[^30000..]; Log.ScrollToEnd(); }
    void Error(Exception ex) { Status.Text=ex.Message; Append(ex.Message); }
    Action<string> DiscLog => s=>Dispatcher.Invoke(()=>{ Append(s); if(Disc.Progress(s) is double p) RipProgress.Value=p; });
    void UpdateQueue() {
        var completed=jobs.Count(j=>j.Status=="Completed");var waiting=jobs.Count(j=>j.Status is "Waiting" or "Queued" or "Paused");var failed=jobs.Count(j=>j.Status=="Failed");
        QueueTitle.Text=$"Compression · {completed} done · {waiting} waiting";
        SidebarQueueSummary.Text=jobs.Count==0?"No files queued":$"{waiting} waiting · {completed} done"+(paused?" · paused":encoding?" · encoding":"")+(failed>0?$" · {failed} failed":"");
        UpdateQueueGroups();RefreshShell();
    }
    void SetDiscBusy(bool value) { discBusy=value; RipSetup.IsEnabled=InspectButton.IsEnabled=RipButton.IsEnabled=!value; CancelRipButton.IsEnabled=value; RefreshShell();if(!value)ScheduleDriveRefresh(); }
    void SetAdding(bool value) { adding=value; StartButton.IsEnabled=PreviewButton.IsEnabled=!value;RefreshShell();if(!value)ScheduleDriveRefresh(); }
    Options Snapshot()
    {
        if(PresetChoice.SelectedItem is not HandBrakePreset p) throw new IOException("Choose a preset from the installed HandBrake GUI or an exported preset file.");
        if(!Path.IsPathFullyQualified(Destination.Text.Trim())) throw new IOException("Choose an absolute compressed-output directory.");
        var hardware=Hardware.SelectedIndex switch {1=>"software",2=>"nvdec",3=>"qsv",_=>Presets.GuiHardware(GuiSettings.Text.Trim())};
        return Presets.Snapshot(PresetFile.Text.Trim(),p,Cli.Text.Trim(),Destination.Text.Trim(),Path.Combine(stateRoot,"presets"),hardware);
    }
    int MinSeconds() => int.TryParse(Minimum.Text,out var value)&&value>=0&&value<=86400?value:throw new IOException("Minimum title length must be 0–86400 seconds.");
    string NumberValue(TextBox box,string label) { var s=box.Text.Trim(); if(s.Length>0&&(!int.TryParse(s,out var n)||n<0)) throw new IOException(label+" must be a non-negative number or blank."); return s; }
    string SeasonValue() => NumberValue(Season,"Season");
    FolderContext NamingContext() => new(series,SeasonValue(),DiscLabel.Text.Trim(),DateTime.Now,NumberValue(Volume,"Volume number"),NumberValue(DiscNumber,"Disc number"));
    void ReloadPresets(string? preferred=null)
    {
        try { var presets=Presets.Load(PresetFile.Text.Trim()); PresetChoice.ItemsSource=presets; PresetChoice.SelectedItem=presets.FirstOrDefault(p=>p.Name==preferred)??presets.FirstOrDefault(); }
        catch(Exception ex) { PresetChoice.ItemsSource=null; PresetSummary.Text=ex.Message; }
    }
    void PresetChanged(object sender,SelectionChangedEventArgs e) { if(PresetSummary!=null&&PresetChoice.SelectedItem is HandBrakePreset p) PresetSummary.Text=$"{p.Group}{p.Name}\nEncoder: {p.Data["VideoEncoder"]} · quality: {p.Data["VideoQualitySlider"]} · {p.Container}\nAll preset settings are snapshotted when queued."; }
    void LoadPresets(object sender,RoutedEventArgs e) => ReloadPresets((PresetChoice.SelectedItem as HandBrakePreset)?.Name);
    void FolderBrowse(TextBox box,string title) { var d=new OpenFolderDialog{Title=title}; if(d.ShowDialog(this)==true) box.Text=d.FolderName; }
    void FileBrowse(TextBox box,string filter) { var d=new OpenFileDialog{Filter=filter}; if(d.ShowDialog(this)==true) box.Text=d.FileName; }
    void BrowseRaw(object s,RoutedEventArgs e)=>FolderBrowse(RawFolder,"Raw-rip destination · originals are kept");
    void BrowseOutput(object s,RoutedEventArgs e)=>FolderBrowse(Destination,"Compressed output for Scout");
    void BrowseScout(object s,RoutedEventArgs e)=>FolderBrowse(ScoutFolder,"Scout data folder containing scout.db");
    void BrowseCli(object s,RoutedEventArgs e)=>FileBrowse(Cli,"HandBrakeCLI|*.exe");
    void BrowseMakeMkv(object s,RoutedEventArgs e)=>FileBrowse(MakeMkv,"MakeMKV console|*.exe");
    void BrowseGuiSettings(object s,RoutedEventArgs e)=>FileBrowse(GuiSettings,"HandBrake settings|*.json");
    void BrowseJson(object s,RoutedEventArgs e) { FileBrowse(PresetFile,"HandBrake presets|*.json"); ReloadPresets(); }
    void AddFolders(object s,RoutedEventArgs e) { var d=new OpenFolderDialog{Multiselect=true,Title="Choose existing ripped-disc folders"}; if(d.ShowDialog(this)==true) foreach(var f in d.FolderNames) if(!folders.Contains(f,StringComparer.OrdinalIgnoreCase)) folders.Add(f); }
    void RemoveFolders(object s,RoutedEventArgs e) { foreach(var f in Folders.SelectedItems.Cast<string>().ToArray()) folders.Remove(f); }
    async void CheckRuntime(object s,RoutedEventArgs e) { try { var o=Snapshot(); Status.Text="Checking preset import and hardware support…"; await Presets.Validate(o,CancellationToken.None); Status.Text=$"Preset imported successfully · encoder {o.Encoder} · decoding {(o.HardwareDecode.Length>0?o.HardwareDecode:"preset/default")}."; Save(); } catch(Exception ex) { Error(ex); } }
    async void SearchSeries(object s,RoutedEventArgs e)
    {
        SearchButton.IsEnabled=false; SearchStatus.Text="Searching TVDB…";
        try { var result=await Tvdb.Search(SeriesQuery.Text.Trim(),ScoutFolder.Text.Trim(),CancellationToken.None); SeriesResults.ItemsSource=result; SearchStatus.Text=$"{result.Count} matches · select the correct series/year. Powered by TheTVDB."; }
        catch(Exception ex) { SearchStatus.Text=ex.Message; } finally { SearchButton.IsEnabled=true; }
    }
    void SeriesChanged(object s,SelectionChangedEventArgs e) { if(SeriesResults.SelectedItem is SeriesIdentity identity) { series=identity; DisplaySeries(); Save(); } }
    void ClearSeries(object s,RoutedEventArgs e) { series=null; SeriesResults.SelectedItem=null; DisplaySeries(); Save(); }
    void DisplaySeries() {
        SelectedSeriesText.Text=series?.Display??"No series selected · optional"; SeriesOverview.Text=series?.Overview??"";
        UpdateNamingPreview();CaptureHistory();
    }
    void NamingChanged(object sender,TextChangedEventArgs e)
    {
        if(namingReady && ReferenceEquals(sender,DiscLabel)) DetectSeason();
        UpdateNamingPreview();CaptureHistory();
    }
    void SeasonEdited(object sender,TextChangedEventArgs e)
    {
        if(namingReady && !updatingSeason) SeasonHint.Text="Manual values for this disc. Auto detection resumes when the disc label changes.";
        UpdateNamingPreview();CaptureHistory();
    }
    void AutoSeasonChanged(object sender,RoutedEventArgs e) { if(namingReady) { lastSeasonLabel="";DetectSeason();UpdateNamingPreview(); } }
    void DetectSeason()
    {
        var label=DiscLabel.Text.Trim();
        if(AutoSeason.IsChecked!=true) { SeasonHint.Text="Manual values · update them when changing discs.";return; }
        if(label==lastSeasonLabel) return;
        lastSeasonLabel=label;var hints=DiscLabelHints.Parse(label);
        updatingSeason=true;
        try {
            Season.Text=hints.Season?.ToString(System.Globalization.CultureInfo.InvariantCulture)??"";
            Volume.Text=hints.Volume?.ToString(System.Globalization.CultureInfo.InvariantCulture)??"";
            DiscNumber.Text=hints.DiscNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture)??"";
        }
        finally { updatingSeason=false; }
        SeasonHint.Text="Values found in the disc label are filled automatically. Fill in missing values or edit them for this disc.";
    }
    void SeriesQueryKeyDown(object sender,System.Windows.Input.KeyEventArgs e) { if(e.Key==System.Windows.Input.Key.Enter && SearchButton.IsEnabled) { e.Handled=true; SearchSeries(sender,new()); } }
    void UpdateNamingPreview()
    {
        if(!namingReady) return;
        UpdateOutputSummary();
        FolderContext context;
        try { context=NamingContext(); } catch(Exception ex) { RawPathPreview.Text=FilenamePreview.Text=ex.Message;return; }
        string PreviewPath(string root,string format) { try { return FolderNames.Resolve(root.Trim(),format,context,false); } catch(Exception ex) { return ex.Message; } }
        RawPathPreview.Text=PreviewPath(RawFolder.Text,RawTemplate.Text);
        UpdateOutputSummary();
        try {
            var original=inspected?.Titles.FirstOrDefault(t=>t.Qualifies(MinSeconds())&&t.Value(27).Length>0)?.Value(27);
            FilenamePreview.Text=(original==null?"Example: ":"Filename: ")+FileNames.WithPrefix(FilenamePrefix.Text,original??"title_t00.mkv",context);
        } catch(Exception ex) { FilenamePreview.Text=ex.Message; }
    }
    async void InspectDisc(object s,RoutedEventArgs e)
    {
        if(RipLocked){Status.Text=LockedRipMessage;return;}
        if(discBusy||driveScanRunning)return;
        if(DriveChoice.SelectedItem is not OpticalDrive drive) { RipStatus.Text="Refresh and select an optical drive first."; return; }
        EnsureRipSession();SetDiscBusy(true); discCancel=new(); RipProgress.Value=0;SetRipSessionStatus("Inspecting");
        try { RipStatus.Text="Inspecting title and track metadata…"; inspected=await Disc.Inspect(MakeMkv.Text.Trim(),drive.Source,DiscLog,discCancel.Token); inspectedSource=drive.Source;
            if(inspected.Label.Length>0) DiscLabel.Text=inspected.Label;
            UpdateNamingPreview();
            var min=MinSeconds();PrepareRipQueue(inspected,min);RipSetupScroll.ScrollToTop(); RipStatus.Text=$"{inspected.Label} · {inspected.Titles.Count} titles · {inspected.Titles.Count(t=>t.Qualifies(min))} meet the {min}s cutoff.";
        } catch(OperationCanceledException) { RipStatus.Text="Inspection cancelled."; } catch(Exception ex) { RipStatus.Text=ex.Message; } finally { SetRipSessionStatus("Draft");discCancel.Dispose();discCancel=null;SetDiscBusy(false); }
    }
    void ShowDiscDetails(object s,RoutedEventArgs e)
    {
        if(inspected==null) { RipStatus.Text="Inspect a disc first."; return; }
        int minimum; try { minimum=MinSeconds(); } catch(Exception ex) { Error(ex);return; }
        var text=$"Disc: {inspected.Label}\nAll qualifying titles are ripped. Episode names are identified later in Scout.\n\n"+string.Join("\n\n",inspected.Titles.Select(t=>$"Title {t.Id} · {t.Name}\n{t.Duration} · {t.Chapters} chapters · {t.Size} · {(t.Qualifies(minimum)?"RIP":"SKIP: under cutoff")}\n{t.Tracks}"));
        new Window{Owner=this,Title="Disc metadata · before ripping",Width=850,Height=600,WindowStartupLocation=WindowStartupLocation.CenterOwner,Content=new TextBox{Text=text,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Padding=new Thickness(18)}}.ShowDialog();
    }
    void CancelRip(object s,RoutedEventArgs e) { discCancel?.Cancel(); CancelRipButton.IsEnabled=false; RipStatus.Text="Stopping disc operation. Any raw files already written are kept."; }
    async void RipDisc(object s,RoutedEventArgs e) => await RipDiscCore();
    async Task RipDiscCore()
    {
        if(RipLocked){Status.Text=LockedRipMessage;return;}
        if(discBusy||driveScanRunning) return;
        if(DriveChoice.SelectedItem is not OpticalDrive drive) { RipStatus.Text="Refresh and select an optical drive first.";return; }
        EnsureRipSession();SetDiscBusy(true); discCancel=new(); var token=discCancel.Token; string? folder=null;RawRip? activeRip=null;
        RipQueuePanel.Visibility=Visibility.Collapsed;lastRipFiles.Clear();SetRipSessionStatus("Ripping");RefreshShell();
        try {
            var raw=RawFolder.Text.Trim();
            if(!Path.IsPathFullyQualified(raw)) throw new IOException("Choose an absolute raw-rip folder.");
            var min=MinSeconds(); var season=SeasonValue(); var identity=series;
            var cli=MakeMkv.Text.Trim(); var label=DiscLabel.Text.Trim();
            var rawFormat=RawTemplate.Text;var filenamePrefix=FilenamePrefix.Text;var started=DateTime.Now;
            var volume=NumberValue(Volume,"Volume number");var discNumber=NumberValue(DiscNumber,"Disc number");
            Save(); RipStatus.Text="Inspecting the inserted disc…";
            var current=await Disc.Inspect(cli,drive.Source,DiscLog,token);
            if(inspected!=null&&inspectedSource==drive.Source&&inspected.Fingerprint!=current.Fingerprint) { inspected=null; throw new IOException("The disc changed since inspection. Inspect it again before ripping."); }
            inspected=current; inspectedSource=drive.Source;
            if(!current.Titles.Any(t=>t.Qualifies(min))) throw new IOException("No titles meet the minimum length.");
            PrepareRipQueue(current,min);RipSetupScroll.ScrollToTop();
            if(label.Length==0) {
                label=current.Label;DiscLabel.Text=label;
                if(AutoSeason.IsChecked==true) { season=SeasonValue();volume=NumberValue(Volume,"Volume number");discNumber=NumberValue(DiscNumber,"Disc number"); }
            }
            var context=new FolderContext(identity,season,label,started,volume,discNumber);
            var rawPath=FolderNames.Resolve(raw,rawFormat,context);
            foreach(var title in current.Titles.Where(t=>t.Qualifies(min))) FileNames.WithPrefix(filenamePrefix,title.Value(27).Length>0?title.Value(27):"title_t00.mkv",context);
            var rip=RawRip.Create(rawPath,new { series_identity=identity,season,volume_number=volume,disc_number=discNumber,disc_label=label,rip_date=started,minimum_title_seconds=min,source=drive.Source,disc=current,raw_format=rawFormat,filename_prefix=filenamePrefix });
            folder=rip.Session;
            activeRip=rip;lastRipFilesRead=DateTime.MinValue;
            RawPathPreview.Text=rawPath;UpdateNamingPreview();
            RipStatus.Text="Ripping to "+rawPath; RipProgress.Value=0;
            using(var log=new StreamWriter(rip.Log)) {
                var gate=new object(); await Disc.Rip(cli,drive.Source,min,rip.Files,l=>{ lock(gate) log.WriteLine(l); Dispatcher.Invoke(()=>{Append(l);TrackRip(l,rip,filenamePrefix,context);}); },token);
            }
            TrackRip("",rip,filenamePrefix,context,true);ripQueue.Publishing();RefreshRipQueue();
            RipStatus.Text="Applying filenames and keeping raw files…";
            var result=await Task.Run(()=>rip.Publish(filenamePrefix,context,null,token));
            folder=rawPath;lastRipFiles=result;
            ripQueue.Complete(result);RefreshRipQueue();
            RipProgress.Value=100;
            RipStatus.Text=$"Rip complete · {result.Count} raw files saved. Add these files to the Compression Queue whenever you are ready.";
            SetRipSessionStatus("Completed");inspected=null;
        } catch(OperationCanceledException) { if(activeRip!=null){ripQueue.Stop(true);RefreshRipQueue();} RipStatus.Text="Rip cancelled. Raw files kept; this disc was not queued.";SetRipSessionStatus("Cancelled"); }
        catch(Exception ex) { if(activeRip!=null){ripQueue.Stop(false);RefreshRipQueue();} RipStatus.Text=ex.Message; Append(ex.Message);SetRipSessionStatus("Failed"); }
        finally { if(folder!=null) Append("Raw files retained: "+folder); discCancel.Dispose();discCancel=null;SetDiscBusy(false); }
    }
    async void Preview(object s,RoutedEventArgs e)
    {
        SetAdding(true);
        try { if(folders.Count==0) throw new IOException("Add source folders first."); var o=Snapshot(); var roots=folders.ToArray();var recursive=Recursive.IsChecked==true;
            var format=OutputTemplate.Text;var fallbackSeries=ExistingUseSeries.IsChecked==true?compressionSeries:null;var filenamePrefix=CompressionPrefix.Text;
            var result=await Task.Run(()=>FolderNames.PlanExisting(roots,o,recursive,format,fallbackSeries,filenamePrefix));
            new Window{Owner=this,Title=$"Queue preview · {result.Count} MKVs",Width=1000,Height=600,WindowStartupLocation=WindowStartupLocation.CenterOwner,Content=new TextBox{Text=string.Join("\n\n",result.Select(j=>$"Source: {j.Source}\nOutput: {Path.Combine(j.Encoding!.Output,j.Name)}")),IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Padding=new Thickness(16)}}.ShowDialog();
        } catch(Exception ex) { ExistingFeedback.Text=ex.Message;Error(ex); } finally { SetAdding(false); }
    }
    async void Start(object s,RoutedEventArgs e)
    {
        if(adding) return;
        try { await QueueExisting(); }
        catch(Exception ex) { ExistingFeedback.Text=ex.Message;Error(ex); }
    }
    async Task QueueExisting()
    {
        SetAdding(true);
        ExistingFeedback.Text="Checking the preset and scanning source folders…";Status.Text=ExistingFeedback.Text;
        try { if(folders.Count==0) throw new IOException("Add source folders first."); var o=Snapshot();var preset=((HandBrakePreset)PresetChoice.SelectedItem).Name;
            var roots=folders.ToArray();var recursive=Recursive.IsChecked==true;
            var format=OutputTemplate.Text;var fallbackSeries=ExistingUseSeries.IsChecked==true?compressionSeries:null;var filenamePrefix=CompressionPrefix.Text;
            var result=await Task.Run(()=>FolderNames.PlanExisting(roots,o,recursive,format,fallbackSeries,filenamePrefix));
            if(result.Count==0) throw new IOException("No MKVs found.");
            await Presets.Validate(o,CancellationToken.None);
            var added=0;
            foreach(var group in result.GroupBy(j=>j.Encoding!.Output,StringComparer.OrdinalIgnoreCase)) added+=Enqueue(group.ToList(),group.First().Encoding!,preset);
            var existing=result.Count-added;
            QueueNotice.Text=added==0?$"All {existing} selected {(existing==1?"file is":"files are")} already in the compression queue.":$"Added {added} {(added==1?"file":"files")} to the compression queue."+(existing>0?$" {existing} already queued.":"");
            QueueNotice.Visibility=Visibility.Visible;ExistingFeedback.Text=QueueNotice.Text;
            Save();ShowCompressionQueue();
            var selected=jobs.FirstOrDefault(j=>result.Any(r=>j.Source.Equals(r.Source,StringComparison.OrdinalIgnoreCase)&&j.Encoding?.Output.Equals(r.Encoding!.Output,StringComparison.OrdinalIgnoreCase)==true));
            if(selected!=null) { QueueGrid.SelectedItem=selected;QueueGrid.ScrollIntoView(selected); }
            Status.Text=QueueNotice.Text;
        } finally { SetAdding(false); }
    }
    int Enqueue(List<Job> incoming,Options options,string preset)
    {
        var added=0;
        var names=jobs.Where(j=>j.Encoding?.Output.Equals(options.Output,StringComparison.OrdinalIgnoreCase)==true).Select(j=>j.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach(var job in incoming) {
            if(jobs.Any(j=>j.Source.Equals(job.Source,StringComparison.OrdinalIgnoreCase)&&j.Encoding?.Output.Equals(options.Output,StringComparison.OrdinalIgnoreCase)==true)) continue;
            job.Name=Engine.Unique(options.Output,job.Name,names);job.Encoding=options;job.PresetDisplay=preset;job.Status=AcceptsRunningJobs?"Queued":"Waiting";jobs.Add(job);added++;
        }
        PersistQueue();LockQueuedRips();UpdateQueue();return added;
    }
    void Stop(object s,RoutedEventArgs e) { paused=true;pauseAfterCurrent=false;encodingCancel?.Cancel();foreach(var j in jobs.Where(j=>j.Status=="Queued")) j.Status="Paused";Status.Text="Stopping compression; pending jobs are retained.";PersistQueue();UpdateQueue(); }
    async void Resume(object s,RoutedEventArgs e)
    {
        if(encoding||clearingQueue||adding) return;
        try {await StartQueues(null);} catch(Exception ex) {Error(ex);}
    }
    async Task Pump()
    {
        if(encoding||paused||clearingQueue) return; encoding=true;encodingStopped=new(TaskCreationOptions.RunContinuationsAsynchronously);var stopped=encodingStopped;encodingCancel=new();UpdateQueue();var token=encodingCancel.Token;
        try {
            while(!token.IsCancellationRequested && (jobs.FirstOrDefault(j=>j.Status=="Queued"&&j.QueueId==activeQueueId)??jobs.FirstOrDefault(j=>j.Status=="Queued")) is Job job) {
                activeQueueId=job.QueueId;
                var o=job.Encoding??throw new IOException("Queued job has no preset settings.");job.Status="Encoding";PersistQueue();UpdateQueue();Progress.Value=0;Status.Text="Compressing "+job.Name;if(QueueGrid.Items.Contains(job))QueueGrid.ScrollIntoView(job);
                try {
                    Directory.CreateDirectory(o.Output);var logDir=Path.Combine(o.Output,".ripley-logs");Directory.CreateDirectory(logDir);
                    using var log=new StreamWriter(Path.Combine(logDir,Guid.NewGuid().ToString("N")+".log"));var gate=new object();
                    job.Name=await Engine.Encode(job,o,line=>{lock(gate)log.WriteLine(line);Dispatcher.Invoke(()=>{Append(line);var m=Regex.Match(line,@"(\d+(?:\.\d+)?)\s*%");if(m.Success&&double.TryParse(m.Groups[1].Value,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var p))Progress.Value=p;});},token,name=>Dispatcher.Invoke(()=>{job.Name=name;job.Status="Publishing";PersistQueue();}));
                    job.Status="Completed";PersistQueue();
                    try { QueueStore.Write(Path.Combine(o.Output,job.Name+".ripley.json"),new {version=2,series_identity=job.Series,season=job.Season,source=job.Source,original_filename=job.OriginalFilename,disc_metadata=job.DiscMetadata,preset=job.PresetDisplay,output=Path.Combine(o.Output,job.Name)}); }
                    catch(Exception ex) { job.Error="Video complete; sidecar could not be saved: "+ex.Message; }
                } catch(OperationCanceledException) { job.Status="Paused"; }
                catch(Exception ex) { job.Status="Failed";job.Error=ex.Message;Append(ex.Message); }
                PersistQueue();UpdateQueue();
                if(pauseAfterCurrent) { paused=true;pauseAfterCurrent=false;foreach(var waiting in jobs.Where(j=>j.Status=="Queued")) waiting.Status="Paused";PersistQueue();break; }
            }
            Status.Text=paused?"Compression paused. Resume in Compression Queue when ready.":$"Batch finished · {jobs.Count(j=>j.Status=="Completed")} completed · {jobs.Count(j=>j.Status=="Failed")} failed · {jobs.Count(j=>j.Status=="Waiting")} waiting for Start all queues.";
        } catch(Exception ex) { paused=true;Error(ex); }
        finally { encodingCancel.Dispose();encodingCancel=null;encoding=false;activeQueueId="";UpdateQueue();stopped.TrySetResult(); }
    }
}
