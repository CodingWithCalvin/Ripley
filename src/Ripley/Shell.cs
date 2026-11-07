using System.Windows;
using System.Windows.Controls;
namespace Ripley;

public partial class MainWindow
{
    string currentPage = "Rip";
    bool clearingQueue;
    string compressionView="Queue";
    bool showingSources=>compressionView=="Sources";
    bool AcceptsRunningJobs => encoding && !paused && !pauseAfterCurrent && !clearingQueue;
    TaskCompletionSource? encodingStopped;

    void InitializeShell()
    {
        VersionText.Text = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "Ripley";
        ShowPage("Rip");
    }

    void ShowPage(string page)
    {
        currentPage=page;
        RipPage.Visibility=page=="Rip"?Visibility.Visible:Visibility.Collapsed;
        CompressPage.Visibility=page=="Compress"?Visibility.Visible:Visibility.Collapsed;
        SettingsPage.Visibility=page=="Settings"?Visibility.Visible:Visibility.Collapsed;
        foreach(var item in new[]{(SideCompress,"Compress"),(NavSettings,"Settings")})
            item.Item1.Style=(Style)FindResource(item.Item2==page?"NavActive":"NavButton");
        (PageHeading.Text,PageSubtitle.Text)=page switch {
            "Compress"=>("Compression Queue","Add finished rips to the queue. Start processing when you are ready."),
            "Settings"=>("Settings","Choose your installed tools, preset file, and Scout data folder."),
            _=>(currentRipSession?.Title??"New Ripley","Save raw files and disc metadata. Reopen sessions from history, or choose New Ripley for the next disc.")
        };
        RefreshShell();
    }
    void ShowSources(object sender,RoutedEventArgs e) {compressionView="Sources";ShowPage("Compress");}
    void ShowCompressionSettings(object sender,RoutedEventArgs e) {compressionView="Output";ShowPage("Compress");CompressionSettings.ScrollToTop();}
    void BackToQueue(object sender,RoutedEventArgs e)=>ShowCompressionQueue();
    void ShowCompressionQueue() {compressionView="Queue";ShowPage("Compress");}

    void RefreshShell()
    {
        UpdateOutputSummary();
        if(currentPage=="Rip")PageSubtitle.Text=RipLocked?LockedRipMessage:"Save raw files and disc metadata. Reopen sessions from history, or choose New Ripley for the next disc.";
        NewRipleyButton.IsEnabled=!discBusy&&!adding;
        RipSetup.IsEnabled=!discBusy&&EditableRip;
        InspectButton.IsEnabled=RipButton.IsEnabled=!discBusy&&!driveScanRunning&&EditableRip;
        RefreshButton.IsEnabled=DriveChoice.IsEnabled=!driveScanRunning;
        RefreshButton.Content=driveScanRunning?"Refreshing…":"Refresh";
        SourceSetup.Visibility=showingSources?Visibility.Visible:Visibility.Collapsed;
        CompressionQueue.Visibility=compressionView=="Queue"?Visibility.Visible:Visibility.Collapsed;
        CompressionSettings.Visibility=compressionView=="Output"?Visibility.Visible:Visibility.Collapsed;
        foreach(var tab in new[]{(CompressQueueTab,"Queue"),(CompressSourcesTab,"Sources"),(CompressOutputTab,"Output")})
            tab.Item1.Style=(Style)FindResource(tab.Item2==compressionView?"PageTabActive":"PageTab");
        RipActions.Visibility=currentPage=="Rip"?Visibility.Visible:Visibility.Collapsed;
        SourceActions.Visibility=currentPage=="Compress"&&showingSources?Visibility.Visible:Visibility.Collapsed;
        SaveSettingsButton.Visibility=currentPage=="Settings"||(currentPage=="Compress"&&compressionView=="Output")?Visibility.Visible:Visibility.Collapsed;
        DiscActivity.Visibility=currentPage=="Rip"||discBusy?Visibility.Visible:Visibility.Collapsed;
        RipProgress.Visibility=discBusy?Visibility.Visible:Visibility.Collapsed;
        CancelRipButton.Visibility=discBusy?Visibility.Visible:Visibility.Collapsed;
        Progress.Visibility=encoding?Visibility.Visible:Visibility.Collapsed;
        StopButton.Visibility=encoding?Visibility.Visible:Visibility.Collapsed;
        StopButton.IsEnabled=encoding&&!paused&&!clearingQueue;
        StopButton.Content=pauseAfterCurrent?"Keep processing":"Pause after current file";
        StopAllButton.Visibility=encoding||discBusy?Visibility.Visible:Visibility.Collapsed;
        StopAllButton.IsEnabled=!clearingQueue;
        ResumeButton.Visibility=currentPage=="Compress"&&compressionView=="Queue"&&!encoding?Visibility.Visible:Visibility.Collapsed;
        ResumeButton.IsEnabled=!encoding&&!clearingQueue&&!adding&&jobs.Any(j=>j.Status is "Waiting" or "Queued" or "Paused" or "Failed");
        ResumeButton.Content="Start all queues";
        ResumeButton.Style=(Style)FindResource("Primary");
        StartSelectedButton.Visibility=ResumeButton.Visibility;
        StartSelectedButton.IsEnabled=ResumeButton.IsEnabled&&QueueList.SelectedItem is QueueGroup {Id.Length:>0} selected&&jobs.Any(j=>j.QueueId==selected.Id&&j.Status is "Waiting" or "Queued" or "Paused" or "Failed");
        ClearQueueButton.IsEnabled=jobs.Count>0&&!clearingQueue&&!adding;
        StartButton.Content=AcceptsRunningJobs?"Add to running queue":"Add to queue";
        QueueRipButton.Content=RipLocked?"Added to queue":StartButton.Content;
        QueueRipButton.Visibility=lastRipFiles.Count>0?Visibility.Visible:Visibility.Collapsed;
        QueueRipButton.IsEnabled=!RipLocked&&!discBusy&&!adding&&!clearingQueue;
        QueueRipButton.ToolTip=RipLocked?"This run is locked. Use New Ripley for the next disc, or Add existing rips to compress its raw files again.":"Add this disc's completed files using the compression settings.";
        QueueStateText.Text=clearingQueue?"Stopping the current encode and clearing queue entries. Raw files and completed outputs are kept."
            :encoding&&paused?"Stopping the current encode. Pending jobs are retained."
            :encoding&&pauseAfterCurrent?"Pausing after the current file finishes and saves. New additions will wait."
            :encoding?"Processing the queue. Add to running queue explicitly includes new files in this run."
            :jobs.Any(j=>j.Status is "Waiting" or "Queued" or "Paused" or "Failed")?"Start a selected disc queue or start all queues in order. Adding files does not start an idle queue."
            :jobs.Count>0?"Compression is complete. Add more rips whenever you are ready."
            :"The queue is empty. Choose Add existing rips, or add a completed disc from history.";
    }

    void UpdateOutputSummary()
    {
        if(OutputSummary==null) return;
        var destinations=jobs.Select(j=>j.Encoding?.Output).Where(p=>p!=null).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        OutputSummary.Text=currentPage=="Rip"?"Raw folder: "+RawPathPreview.Text
            :currentPage=="Compress"&&compressionView=="Queue"?(destinations.Length==1?"Queue output: "+destinations[0]:$"Queue output: {destinations.Length} folders · each file keeps its own destination")
            :"Compressed base folder: "+Destination.Text+" · folders resolved per source file";
    }

    void Navigate(object sender, RoutedEventArgs e) => ShowPage((string)((Button)sender).Tag);
    void SavePreferences(object sender, RoutedEventArgs e) { if(Save()) Status.Text = "Settings saved."; }
    void MinimizeWindow(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    void ToggleMaximize(object sender, RoutedEventArgs e)
    {
        if(WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }
    void CloseWindow(object sender, RoutedEventArgs e) => Close();

    async void ClearQueue(object sender, RoutedEventArgs e)
    {
        if(clearingQueue || jobs.Count == 0) return;
        if(encoding && MessageBox.Show(this,
            "Stop the current encode and clear all compression queues? Raw files and completed outputs are kept. Any disc rip will continue.",
            "Clear all queues", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { await ClearQueueEntries(); } catch(Exception ex) { Error(ex); }
    }

    async Task ClearQueueEntries()
    {
        if(clearingQueue) return;
        var selected = jobs.ToArray();
        clearingQueue = true; paused = true; RefreshShell();
        try {
            // Wait for HandBrake to stop and clean up its incomplete output before removing its job.
            var stopped = encoding ? encodingStopped?.Task : null;
            encodingCancel?.Cancel();
            if(stopped != null) await stopped;
            foreach(var job in selected) jobs.Remove(job);
            QueueNotice.Visibility = Visibility.Collapsed;
            // A disc that finished during the clear can enqueue new files; keep those new jobs.
            PersistQueue();
            Status.Text = "Compression queue cleared. Raw files and completed outputs were kept.";
        }
        finally { paused = true; pauseAfterCurrent=false; clearingQueue = false; UpdateQueue(); }
    }
}
