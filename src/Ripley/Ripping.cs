using System.IO;
using System.Windows;
namespace Ripley;

public partial class MainWindow
{
    readonly RipQueue ripQueue=new();
    DateTime lastRipFilesRead=DateTime.MinValue;

    void PrepareRipQueue(DiscInfo disc,int minimum)
    {
        ripQueue.Reset(disc.Titles.Count(t=>t.Qualifies(minimum)));
        RipQueuePanel.Visibility=Visibility.Visible;RefreshRipQueue();
    }
    void RefreshRipQueue()
    {
        RipQueueSummary.Text=ripQueue.Summary;RipFileActivity.Text=ripQueue.Activity;
        RipOverallProgress.Value=ripQueue.Overall;
        if(ripQueue.Current is { } current) RipGrid.ScrollIntoView(current);
    }
    void TrackRip(string line,RawRip rip,string prefix,FolderContext context,bool force=false)
    {
        ripQueue.Read(line);
        var now=DateTime.UtcNow;
        if(force||ripQueue.Current is {OriginalName:""}||now-lastRipFilesRead>TimeSpan.FromMilliseconds(500)) {
            lastRipFilesRead=now;
            try {
                var files=new DirectoryInfo(rip.Files).EnumerateFiles("*.mkv").OrderBy(f=>f.CreationTimeUtc).ThenBy(f=>f.Name)
                    .Select(f=>(f.Name,f.Length)).ToArray();
                ripQueue.ObserveFiles(files,name=>FileNames.WithPrefix(prefix,name,context));
            }
            catch(IOException) { /* MakeMKV can still be opening or closing the current raw file. */ }
        }
        RipProgress.Value=ripQueue.Overall;RefreshRipQueue();
        RipStatus.Text=ripQueue.Summary+" · "+ripQueue.Activity;
        if(now-lastHistoryWrite>TimeSpan.FromSeconds(5))SaveHistory();
    }
}
