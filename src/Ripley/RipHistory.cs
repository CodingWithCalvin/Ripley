using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
namespace Ripley;

public sealed class RipSession : INotifyPropertyChanged
{
    public string Id {get;set;}=Guid.NewGuid().ToString("N");
    public DateTime Created {get;set;}=DateTime.Now;
    public string Status {get;set;}="Draft";
    public string Detail {get;set;}="Choose the disc and series, then rip when ready.";
    public SeriesIdentity? Series {get;set;}
    public string DiscLabel {get;set;}="";
    public string Season {get;set;}="";
    public string Volume {get;set;}="";
    public string DiscNumber {get;set;}="";
    public string RawFolder {get;set;}="";
    public string RawFormat {get;set;}="";
    public string Prefix {get;set;}="";
    public string Minimum {get;set;}="120";
    public bool AutoSeason {get;set;}=true;
    public DiscInfo? Disc {get;set;}
    public string DiscSource {get;set;}="";
    public List<Job> Files {get;set;}=new();
    public List<RipFile> Progress {get;set;}=new();
    public double Overall {get;set;}
    public DateTime? QueuedAtUtc {get;set;}
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsLocked=>QueuedAtUtc.HasValue;
    public string Title=>DiscLabel.Length==0?"New Ripley":(Series?.Name is {Length:>0} name?name+" · ":"")+DiscLabel;
    public string Subtitle=>$"{Created:MMM d, h:mm tt} · {(IsLocked?"Queued · locked":Status)}"+(Files.Count>0?$" · {Files.Count} files":"");
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Refresh()=>PropertyChanged?.Invoke(this,new(null));
}

public partial class MainWindow
{
    readonly ObservableCollection<RipSession> ripHistory=new();
    RipSession? currentRipSession;
    bool historyReady,restoringHistory;
    DateTime lastHistoryWrite=DateTime.MinValue;
    string HistoryPath=>Path.Combine(stateRoot,"history.json");
    record HistoryDocument(string? Selected,List<RipSession> Sessions);
    const string LockedRipMessage="Run locked · Files added to compression. Choose New Ripley for the next disc.";
    bool RipLocked=>currentRipSession?.IsLocked==true;
    bool EditableRip=>!RipLocked&&(currentRipSession==null||currentRipSession.Status=="Draft");

    void InitializeHistory()
    {
        HistoryList.ItemsSource=ripHistory;
        var saved=File.Exists(HistoryPath)?JsonSerializer.Deserialize<HistoryDocument>(File.ReadAllText(HistoryPath)):null;
        foreach(var session in saved?.Sessions??[]) {
            if(session.Status is "Ripping" or "Inspecting") {
                session.Status="Interrupted";session.Detail="This disc operation was interrupted. Raw files are kept. Choose New Ripley to rip the disc again.";
                foreach(var file in session.Progress.Where(f=>f.Status!="Saved"))file.Status=file.OriginalName.Length>0?"Kept · unverified":"Not started";
            }
            ripHistory.Add(session);
        }
        historyReady=true;
        if(ripHistory.Count>0) HistoryList.SelectedItem=ripHistory.FirstOrDefault(s=>s.Id==saved?.Selected)??ripHistory[0];
        LockQueuedRips();
        SaveHistory();
    }
    void NewRipley(object sender,RoutedEventArgs e)
    {
        if(discBusy||adding)return;
        CreateRipSession(true);
        if(driveNotificationSource!=null)QueueDriveRefresh(0);
    }
    void CreateRipSession(bool clearDisc)
    {
        CaptureHistory();
        var session=new RipSession {Series=series,RawFolder=RawFolder.Text,RawFormat=RawTemplate.Text,Prefix=FilenamePrefix.Text,Minimum=Minimum.Text,AutoSeason=AutoSeason.IsChecked==true,
            DiscLabel=clearDisc?"":DiscLabel.Text,Season=clearDisc?"":Season.Text,Volume=clearDisc?"":Volume.Text,DiscNumber=clearDisc?"":DiscNumber.Text};
        ripHistory.Insert(0,session);HistoryList.SelectedItem=session;SaveHistory();
    }
    void EnsureRipSession()
    {
        if(currentRipSession==null||currentRipSession.Status!="Draft")CreateRipSession(false);
    }
    void OpenRipHistory(object sender,RoutedEventArgs e)
    {
        if(sender is not FrameworkElement {DataContext:RipSession session})return;
        if((discBusy||adding)&&session!=currentRipSession) {
            Status.Text="Finish the current disc operation before switching history.";return;
        }
        HistoryList.SelectedItem=session;ShowPage("Rip");
    }
    void HistorySelected(object sender,SelectionChangedEventArgs e)
    {
        if(restoringHistory||!historyReady||HistoryList.SelectedItem is not RipSession selected)return;
        if(discBusy||adding) {
            restoringHistory=true;try{HistoryList.SelectedItem=currentRipSession;}finally{restoringHistory=false;}return;
        }
        CaptureHistory();currentRipSession=selected;restoringHistory=true;var ready=namingReady;namingReady=false;
        try {
            series=selected.Series;DiscLabel.Text=selected.DiscLabel;Season.Text=selected.Season;Volume.Text=selected.Volume;DiscNumber.Text=selected.DiscNumber;
            RawFolder.Text=selected.RawFolder;RawTemplate.Text=selected.RawFormat;FilenamePrefix.Text=selected.Prefix;Minimum.Text=selected.Minimum;AutoSeason.IsChecked=selected.AutoSeason;
            lastSeasonLabel=selected.DiscLabel;inspected=selected.Disc;inspectedSource=selected.DiscSource;
            lastRipFiles=selected.Files.ToList();ripQueue.Restore(selected.Progress,selected.Overall,selected.Detail);
            RipQueuePanel.Visibility=selected.Progress.Count>0?Visibility.Visible:Visibility.Collapsed;
            RipStatus.Text=selected.Detail;RipProgress.Value=selected.Overall;DisplaySeries();RefreshRipQueue();
        }
        finally {namingReady=ready;restoringHistory=false;}
        UpdateNamingPreview();ShowPage("Rip");RipSetupScroll.ScrollToTop();SaveHistory();
    }
    void CaptureHistory()
    {
        if(!historyReady||restoringHistory||currentRipSession is not {IsLocked:false} session)return;
        session.Series=series;session.DiscLabel=DiscLabel.Text;session.Season=Season.Text;session.Volume=Volume.Text;session.DiscNumber=DiscNumber.Text;
        session.RawFolder=RawFolder.Text;session.RawFormat=RawTemplate.Text;session.Prefix=FilenamePrefix.Text;session.Minimum=Minimum.Text;session.AutoSeason=AutoSeason.IsChecked==true;
        session.Disc=inspected??session.Disc;session.DiscSource=inspectedSource;session.Files=lastRipFiles.ToList();session.Overall=ripQueue.Overall;session.Detail=RipStatus.Text;
        session.Progress=ripQueue.Files.Select(f=>new RipFile{Number=f.Number,OriginalName=f.OriginalName,Name=f.Name,Status=f.Status,Bytes=f.Bytes,Progress=f.Progress}).ToList();
        session.Refresh();
        if(currentPage=="Rip")PageHeading.Text=session.Title;
    }
    void SaveHistory()
    {
        if(!historyReady||restoringHistory)return;
        CaptureHistory();
        try {QueueStore.Write(HistoryPath,new HistoryDocument(currentRipSession?.Id,ripHistory.ToList()));lastHistoryWrite=DateTime.UtcNow;}
        catch(Exception ex) {Error(new IOException("Could not save rip history: "+ex.Message,ex));}
    }
    void SetRipSessionStatus(string status)
    {
        if(currentRipSession!=null){currentRipSession.Status=status;SaveHistory();}
    }
    void LockQueuedRips()
    {
        if(!historyReady)return;
        var queuedSources=jobs.Select(j=>j.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var completed=ripHistory.Where(s=>!s.IsLocked&&s.Status=="Completed"&&s.Files.Count>0&&s.Files.All(f=>queuedSources.Contains(f.Source))).ToArray();
        if(completed.Length==0)return;
        CaptureHistory();
        foreach(var session in completed) {
            session.QueuedAtUtc=DateTime.UtcNow;session.Detail=LockedRipMessage;session.Refresh();
            if(ReferenceEquals(session,currentRipSession))RipStatus.Text=LockedRipMessage;
        }
        SaveHistory();RefreshShell();
    }
}
