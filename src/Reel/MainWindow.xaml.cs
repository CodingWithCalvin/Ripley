using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
namespace Reel;

public partial class MainWindow : Window
{
    readonly ObservableCollection<string> folders = new();
    readonly ObservableCollection<Job> jobs = new();
    readonly string stateRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Reel");
    string SettingsPath => Path.Combine(stateRoot,"settings.json");
    string QueuePath => Path.Combine(stateRoot,"queue.json");
    CancellationTokenSource? encodingCancel, discCancel;
    bool encoding, discBusy, adding, paused, namingReady, updatingSeason;
    string lastSeasonLabel="";
    DiscInfo? inspected;
    string inspectedSource = "";
    SeriesIdentity? series;
    public MainWindow()
    {
        InitializeComponent(); Folders.ItemsSource = folders; QueueGrid.ItemsSource = jobs;
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
                    series=s.Series; preferred=s.Preset; foreach(var f in s.Folders ?? []) folders.Add(f);
                    AutoSeason.IsChecked=s.AutoDetectSeason??true;
                    RawTemplate.Text=s.RawFormat??FolderNames.DefaultRaw;OutputTemplate.Text=s.CompressedFormat??FolderNames.DefaultCompressed;
                }
            }
            foreach(var job in QueueStore.Read(QueuePath)) jobs.Add(job);
            paused=jobs.Any(j=>j.Status=="Paused");
            if(paused) Status.Text="Saved queue restored. Resume when ready; no work starts automatically.";
        } catch(Exception ex) { Error(ex); }
        ReloadPresets(preferred); DisplaySeries(); UpdateQueue(); namingReady=true; UpdateNamingPreview();
        Closing += (_, e) => { if(encoding || discBusy || adding) { e.Cancel=true; MessageBox.Show(this,"Pause compression and cancel any disc operation before closing. Raw files are kept.","Reel is working"); } else { Save(); PersistQueue(); } };
    }
    record Saved(string? Cli,string? Preset,string? Json,string? Output,bool Recursive,string[]? Folders,string? MakeMkv,string? Raw,int? Minimum,string? ScoutFolder,SeriesIdentity? Series,string? Season,string? GuiSettings,int Hardware,string? RawFormat=null,string? CompressedFormat=null,bool? AutoDetectSeason=null);
    void Save() { try { QueueStore.Write(SettingsPath,new Saved(Cli.Text,(PresetChoice.SelectedItem as HandBrakePreset)?.Name,PresetFile.Text,Destination.Text,Recursive.IsChecked==true,folders.ToArray(),MakeMkv.Text,RawFolder.Text,int.TryParse(Minimum.Text,out var m)?m:120,ScoutFolder.Text,series,Season.Text,GuiSettings.Text,Hardware.SelectedIndex,RawTemplate.Text,OutputTemplate.Text,AutoSeason.IsChecked)); } catch(Exception ex) { Append("Could not save settings: "+ex.Message); } }
    void PersistQueue() => QueueStore.Write(QueuePath,jobs.ToList());
    void Append(string text) { Log.AppendText(text+Environment.NewLine); if(Log.Text.Length>45000) Log.Text=Log.Text[^30000..]; Log.ScrollToEnd(); }
    void Error(Exception ex) { Status.Text=ex.Message; Append(ex.Message); }
    Action<string> DiscLog => s=>Dispatcher.Invoke(()=>{ Append(s); if(Disc.Progress(s) is double p) RipProgress.Value=p; });
    void UpdateQueue() { QueueTitle.Text=$"Compression · {jobs.Count(j=>j.Status=="Completed")} done · {jobs.Count(j=>j.Status is "Queued" or "Paused")} waiting"; StopButton.IsEnabled=encoding; }
    void SetDiscBusy(bool value) { discBusy=value; RipSetup.IsEnabled=InspectButton.IsEnabled=RipButton.IsEnabled=!value; CancelRipButton.IsEnabled=value; }
    void SetAdding(bool value) { adding=value; StartButton.IsEnabled=PreviewButton.IsEnabled=!value; }
    static void ValidateFolders(string raw,string output)
    {
        if(!Path.IsPathFullyQualified(raw)||!Path.IsPathFullyQualified(output)) throw new IOException("Choose absolute raw-rip and compressed-output folders.");
        var a=Path.GetFullPath(raw).TrimEnd('\\'); var b=Path.GetFullPath(output).TrimEnd('\\');
        if(a.Equals(b,StringComparison.OrdinalIgnoreCase)||a.StartsWith(b+"\\",StringComparison.OrdinalIgnoreCase)||b.StartsWith(a+"\\",StringComparison.OrdinalIgnoreCase)) throw new IOException("Keep raw rips and compressed output in separate, non-nested folders.");
    }
    Options Snapshot()
    {
        if(PresetChoice.SelectedItem is not HandBrakePreset p) throw new IOException("Choose a preset from the installed HandBrake GUI or an exported preset file.");
        if(!Path.IsPathFullyQualified(Destination.Text.Trim())) throw new IOException("Choose an absolute compressed-output directory.");
        var hardware=Hardware.SelectedIndex switch {1=>"software",2=>"nvdec",3=>"qsv",_=>Presets.GuiHardware(GuiSettings.Text.Trim())};
        return Presets.Snapshot(PresetFile.Text.Trim(),p,Cli.Text.Trim(),Destination.Text.Trim(),Path.Combine(stateRoot,"presets"),hardware);
    }
    int MinSeconds() => int.TryParse(Minimum.Text,out var value)&&value>=0&&value<=86400?value:throw new IOException("Minimum title length must be 0–86400 seconds.");
    string SeasonValue() { var s=Season.Text.Trim(); if(s.Length>0&&(!int.TryParse(s,out var n)||n<0)) throw new IOException("Season must be a non-negative number or blank."); return s; }
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
    void DisplaySeries() { SelectedSeriesText.Text=series?.Display??"No series selected · optional"; SeriesOverview.Text=series?.Overview??""; UpdateNamingPreview(); }
    void NamingChanged(object sender,TextChangedEventArgs e)
    {
        if(namingReady && ReferenceEquals(sender,DiscLabel)) DetectSeason();
        UpdateNamingPreview();
    }
    void SeasonEdited(object sender,TextChangedEventArgs e)
    {
        if(namingReady && !updatingSeason) SeasonHint.Text="Manual season for this disc. Auto detection resumes when the disc label changes.";
        UpdateNamingPreview();
    }
    void AutoSeasonChanged(object sender,RoutedEventArgs e) { if(namingReady) { lastSeasonLabel="";DetectSeason();UpdateNamingPreview(); } }
    void DetectSeason()
    {
        var label=DiscLabel.Text.Trim();
        if(AutoSeason.IsChecked!=true) { SeasonHint.Text="Manual season · remember to update it for a new season.";return; }
        if(label==lastSeasonLabel) return;
        lastSeasonLabel=label;var hints=DiscLabelHints.Parse(label);
        updatingSeason=true;
        try { Season.Text=hints.Season?.ToString(System.Globalization.CultureInfo.InvariantCulture)??""; }
        finally { updatingSeason=false; }
        SeasonHint.Text=hints.Season is int season ? $"Detected season {season}"+(hints.Volume is int volume?$" · volume {volume}":"")+(hints.DiscNumber is int number?$" · disc {number}":"") : "No clear season in the disc label. Enter a season if needed; disc number alone is not enough.";
    }
    void SeriesQueryKeyDown(object sender,System.Windows.Input.KeyEventArgs e) { if(e.Key==System.Windows.Input.Key.Enter && SearchButton.IsEnabled) { e.Handled=true; SearchSeries(sender,new()); } }
    void UpdateNamingPreview()
    {
        if(!namingReady) return;
        var context=new FolderContext(series,Season.Text.Trim(),DiscLabel.Text.Trim(),DateTime.Now);
        string PreviewPath(string root,string format) { try { return FolderNames.Resolve(root.Trim(),format,context,false); } catch(Exception ex) { return ex.Message; } }
        RawPathPreview.Text=PreviewPath(RawFolder.Text,RawTemplate.Text);OutputPathPreview.Text=PreviewPath(Destination.Text,OutputTemplate.Text);
        OutputSummary.Text="Compressed folder: "+OutputPathPreview.Text;
    }
    void DriveChanged(object s,SelectionChangedEventArgs e) { inspected=null; if(DriveChoice.SelectedItem is OpticalDrive d) DiscLabel.Text=d.Label; }
    async void RefreshDrives(object s,RoutedEventArgs e)
    {
        SetDiscBusy(true); discCancel=new(); var lines=new System.Collections.Concurrent.ConcurrentQueue<string>();
        try { RipStatus.Text="Finding optical drives…"; using var timeout=CancellationTokenSource.CreateLinkedTokenSource(discCancel.Token); timeout.CancelAfter(TimeSpan.FromSeconds(60));
            await Engine.Run(Engine.Command(MakeMkv.Text.Trim(),["-r","--cache=1","info","disc:9999"]),l=>{lines.Enqueue(l);DiscLog(l);},timeout.Token);
            var drives=Disc.Parse(lines).Drives; DriveChoice.ItemsSource=drives; DriveChoice.SelectedIndex=drives.Count>0?0:-1;
            RipStatus.Text=drives.Count>0?"Select a drive, then inspect or rip.":"MakeMKV found no optical drives. Check the drive connection and close any other app using it.";
        } catch(OperationCanceledException) { RipStatus.Text="Drive detection cancelled or timed out."; } catch(Exception ex) { RipStatus.Text=ex.Message; } finally { discCancel.Dispose();discCancel=null;SetDiscBusy(false); }
    }
    async void InspectDisc(object s,RoutedEventArgs e)
    {
        if(DriveChoice.SelectedItem is not OpticalDrive drive) { RipStatus.Text="Refresh and select an optical drive first."; return; }
        SetDiscBusy(true); discCancel=new(); RipProgress.Value=0;
        try { RipStatus.Text="Inspecting title and track metadata…"; inspected=await Disc.Inspect(MakeMkv.Text.Trim(),drive.Source,DiscLog,discCancel.Token); inspectedSource=drive.Source;
            if(inspected.Label.Length>0) DiscLabel.Text=inspected.Label;
            var min=MinSeconds(); RipStatus.Text=$"{inspected.Label} · {inspected.Titles.Count} titles · {inspected.Titles.Count(t=>t.Qualifies(min))} meet the {min}s cutoff."; ShowDiscDetails(this,new());
        } catch(OperationCanceledException) { RipStatus.Text="Inspection cancelled."; } catch(Exception ex) { RipStatus.Text=ex.Message; } finally { discCancel.Dispose();discCancel=null;SetDiscBusy(false); }
    }
    void ShowDiscDetails(object s,RoutedEventArgs e)
    {
        if(inspected==null) { RipStatus.Text="Inspect a disc first."; return; }
        int minimum; try { minimum=MinSeconds(); } catch(Exception ex) { Error(ex);return; }
        var text=$"Disc: {inspected.Label}\nAll qualifying titles are ripped. Episode names are identified later in Scout.\n\n"+string.Join("\n\n",inspected.Titles.Select(t=>$"Title {t.Id} · {t.Name}\n{t.Duration} · {t.Chapters} chapters · {t.Size} · {(t.Qualifies(minimum)?"RIP":"SKIP: under cutoff")}\n{t.Tracks}"));
        new Window{Owner=this,Title="Disc metadata · before ripping",Width=850,Height=600,WindowStartupLocation=WindowStartupLocation.CenterOwner,Content=new TextBox{Text=text,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Padding=new Thickness(18)}}.ShowDialog();
    }
    void CancelRip(object s,RoutedEventArgs e) { discCancel?.Cancel(); CancelRipButton.IsEnabled=false; RipStatus.Text="Stopping disc operation. Any raw files already written are kept."; }
    async void RipDisc(object s,RoutedEventArgs e)
    {
        if(DriveChoice.SelectedItem is not OpticalDrive drive) { RipStatus.Text="Refresh and select an optical drive first.";return; }
        SetDiscBusy(true); discCancel=new(); var token=discCancel.Token; string? folder=null;
        try {
            var raw=RawFolder.Text.Trim(); ValidateFolders(raw,Destination.Text.Trim()); var min=MinSeconds(); var season=SeasonValue(); var identity=series;
            var o=Snapshot(); var preset=(PresetChoice.SelectedItem as HandBrakePreset)!.Name; var cli=MakeMkv.Text.Trim(); var label=DiscLabel.Text.Trim();
            var rawFormat=RawTemplate.Text;var compressedFormat=OutputTemplate.Text;var started=DateTime.Now;
            Save(); RipStatus.Text="Checking HandBrake preset, then inspecting the inserted disc…";
            await Presets.Validate(o,token);
            var current=await Disc.Inspect(cli,drive.Source,DiscLog,token);
            if(inspected!=null&&inspectedSource==drive.Source&&inspected.Fingerprint!=current.Fingerprint) { inspected=null; throw new IOException("The disc changed since inspection. Inspect it again before ripping."); }
            inspected=current; inspectedSource=drive.Source;
            if(!current.Titles.Any(t=>t.Qualifies(min))) throw new IOException("No titles meet the minimum length.");
            if(label.Length==0) {
                label=current.Label;DiscLabel.Text=label;
                if(AutoSeason.IsChecked==true) season=SeasonValue();
            }
            var context=new FolderContext(identity,season,label,started);
            var rawPath=FolderNames.Resolve(raw,rawFormat,context);o=o with {Output=FolderNames.Resolve(o.Output,compressedFormat,context)};
            folder=FolderNames.CreateRaw(rawPath); var metadata=Path.Combine(folder,"reel-disc.json");
            RawPathPreview.Text=folder;OutputPathPreview.Text=o.Output;
            QueueStore.Write(metadata,new { version=2,status="Ripping",series_identity=identity,season,disc_label=label,minimum_title_seconds=min,source=drive.Source,disc=current,preset,raw_format=rawFormat,compressed_format=compressedFormat,compressed_folder=o.Output });
            RipStatus.Text="Ripping to "+folder; RipProgress.Value=0;
            using(var log=new StreamWriter(Path.Combine(folder,"makemkv.log"))) {
                var gate=new object(); await Disc.Rip(cli,drive.Source,min,folder,l=>{ lock(gate) log.WriteLine(l); DiscLog(l); },token);
            }
            QueueStore.Write(metadata,new { version=2,status="Ripped",series_identity=identity,season,disc_label=label,minimum_title_seconds=min,source=drive.Source,disc=current,preset,raw_format=rawFormat,compressed_format=compressedFormat,compressed_folder=o.Output });
            var result=await Task.Run(()=>Engine.Scan([folder],o.Output,true,o.Extension));
            Enqueue(result,o,preset,identity,season,metadata);
            RipProgress.Value=100; RipStatus.Text=$"Rip complete · {result.Count} files queued. Swap the disc and refresh; compression can continue.";
            inspected=null; _=Pump();
        } catch(OperationCanceledException) { RipStatus.Text="Rip cancelled. Raw files kept; this disc was not queued."; }
        catch(Exception ex) { RipStatus.Text=ex.Message; Append(ex.Message); }
        finally { if(folder!=null) Append("Raw files retained: "+folder); discCancel.Dispose();discCancel=null;SetDiscBusy(false); }
    }
    async void Preview(object s,RoutedEventArgs e)
    {
        SetAdding(true);
        try { if(folders.Count==0) throw new IOException("Add source folders first."); var o=Snapshot(); var roots=folders.ToArray();var recursive=Recursive.IsChecked==true;
            var format=OutputTemplate.Text;var context=new FolderContext(series,SeasonValue(),"",DateTime.Now);
            var result=await Task.Run(()=>FolderNames.PlanExisting(roots,o,recursive,format,context));
            new Window{Owner=this,Title=$"Queue preview · {result.Count} MKVs",Width=900,Height=550,WindowStartupLocation=WindowStartupLocation.CenterOwner,Content=new TextBox{Text=string.Join("\n",result.Select(j=>Path.Combine(j.Encoding!.Output,j.Name))),IsReadOnly=true,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Padding=new Thickness(16)}}.ShowDialog();
        } catch(Exception ex) { Error(ex); } finally { SetAdding(false); }
    }
    async void Start(object s,RoutedEventArgs e)
    {
        SetAdding(true);
        try { if(folders.Count==0) throw new IOException("Add source folders first."); var o=Snapshot();var identity=series;var season=SeasonValue();var preset=((HandBrakePreset)PresetChoice.SelectedItem).Name;
            var roots=folders.ToArray();var recursive=Recursive.IsChecked==true;
            var format=OutputTemplate.Text;var context=new FolderContext(identity,season,"",DateTime.Now);
            // Prevent an existing-folder import from consuming this app's incomplete rip.
            foreach(var root in roots) foreach(var marker in Directory.EnumerateFiles(root,"reel-disc.json",recursive?SearchOption.AllDirectories:SearchOption.TopDirectoryOnly)) {
                using var doc=JsonDocument.Parse(File.ReadAllText(marker)); if(doc.RootElement.GetProperty("status").GetString()!="Ripped") throw new IOException("This selection includes an incomplete Reel rip. Finish/re-rip that disc before importing it.");
            }
            await Presets.Validate(o,CancellationToken.None); var result=await Task.Run(()=>FolderNames.PlanExisting(roots,o,recursive,format,context));
            if(result.Count==0) throw new IOException("No MKVs found.");
            foreach(var group in result.GroupBy(j=>j.Encoding!.Output,StringComparer.OrdinalIgnoreCase)) Enqueue(group.ToList(),group.First().Encoding!,preset,identity,season,"");
            Save(); _=Pump();
        } catch(Exception ex) { Error(ex); } finally { SetAdding(false); }
    }
    void Enqueue(List<Job> incoming,Options options,string preset,SeriesIdentity? identity,string season,string metadata)
    {
        var names=jobs.Where(j=>j.Encoding?.Output.Equals(options.Output,StringComparison.OrdinalIgnoreCase)==true).Select(j=>j.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach(var job in incoming) {
            if(jobs.Any(j=>j.Source.Equals(job.Source,StringComparison.OrdinalIgnoreCase)&&j.Encoding?.Output.Equals(options.Output,StringComparison.OrdinalIgnoreCase)==true)) continue;
            job.Name=Engine.Unique(options.Output,job.Name,names);job.Encoding=options;job.Series=identity;job.Season=season;if(metadata.Length>0)job.DiscMetadata=metadata;job.PresetDisplay=preset;job.Status=paused?"Paused":"Queued";jobs.Add(job);
        }
        PersistQueue();UpdateQueue();
    }
    void Stop(object s,RoutedEventArgs e) { paused=true;encodingCancel?.Cancel();foreach(var j in jobs.Where(j=>j.Status=="Queued")) j.Status="Paused";Status.Text="Pausing compression; ripping can continue."; }
    async void Resume(object s,RoutedEventArgs e)
    {
        if(encoding) return;
        try { foreach(var j in jobs.Where(j=>j.Status is "Paused" or "Failed")) { j.Status="Queued";j.Error=""; } paused=false;PersistQueue();await Pump(); } catch(Exception ex) { Error(ex); }
    }
    async Task Pump()
    {
        if(encoding||paused) return; encoding=true;encodingCancel=new();UpdateQueue();var token=encodingCancel.Token;
        try {
            while(!token.IsCancellationRequested && jobs.FirstOrDefault(j=>j.Status=="Queued") is Job job) {
                var o=job.Encoding??throw new IOException("Queued job has no preset settings.");job.Status="Encoding";PersistQueue();Progress.Value=0;Status.Text="Compressing "+job.Name;QueueGrid.ScrollIntoView(job);
                try {
                    Directory.CreateDirectory(o.Output);var logDir=Path.Combine(o.Output,".reel-logs");Directory.CreateDirectory(logDir);
                    using var log=new StreamWriter(Path.Combine(logDir,Guid.NewGuid().ToString("N")+".log"));var gate=new object();
                    job.Name=await Engine.Encode(job,o,line=>{lock(gate)log.WriteLine(line);Dispatcher.Invoke(()=>{Append(line);var m=Regex.Match(line,@"(\d+(?:\.\d+)?)\s*%");if(m.Success&&double.TryParse(m.Groups[1].Value,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var p))Progress.Value=p;});},token,name=>Dispatcher.Invoke(()=>{job.Name=name;job.Status="Publishing";PersistQueue();}));
                    job.Status="Completed";PersistQueue();
                    try { QueueStore.Write(Path.Combine(o.Output,job.Name+".reel.json"),new {version=1,series_identity=job.Series,season=job.Season,source=job.Source,disc_metadata=job.DiscMetadata,preset=job.PresetDisplay,output=Path.Combine(o.Output,job.Name)}); }
                    catch(Exception ex) { job.Error="Video complete; sidecar could not be saved: "+ex.Message; }
                } catch(OperationCanceledException) { job.Status="Paused"; }
                catch(Exception ex) { job.Status="Failed";job.Error=ex.Message;Append(ex.Message); }
                PersistQueue();UpdateQueue();
            }
            Status.Text=paused?"Compression paused. Resume when ready.":$"Compression caught up · {jobs.Count(j=>j.Status=="Completed")} completed · {jobs.Count(j=>j.Status=="Failed")} failed. Ready for the next disc.";
        } catch(Exception ex) { paused=true;Error(ex); }
        finally { encodingCancel.Dispose();encodingCancel=null;encoding=false;UpdateQueue(); }
    }
}
