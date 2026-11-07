using System.IO;
using System.Windows;
using System.Windows.Controls;
namespace Ripley;

public partial class MainWindow
{
    void CompressionNamingChanged(object sender,TextChangedEventArgs e) {if(namingReady)UpdateOutputSummary();}
    void DisplayCompressionSeries()
    {
        ExistingSeriesSummary.Text=compressionSeries?.Display??"Search for a series above if older rips need a fallback.";
        ExistingUseSeries.IsEnabled=compressionSeries!=null;
    }
    async void SearchCompressionSeries(object sender,RoutedEventArgs e)
    {
        CompressionSearchButton.IsEnabled=false;CompressionSearchStatus.Text="Searching TVDB…";
        try {var result=await Tvdb.Search(CompressionSeriesQuery.Text.Trim(),ScoutFolder.Text.Trim(),CancellationToken.None);CompressionSeriesResults.ItemsSource=result;CompressionSearchStatus.Text=$"{result.Count} matches · select the correct series/year. Powered by TheTVDB.";}
        catch(Exception ex) {CompressionSearchStatus.Text=ex.Message;}
        finally {CompressionSearchButton.IsEnabled=true;}
    }
    void CompressionSeriesChanged(object sender,SelectionChangedEventArgs e)
    {
        if(CompressionSeriesResults.SelectedItem is SeriesIdentity identity) {compressionSeries=identity;ExistingUseSeries.IsChecked=true;DisplayCompressionSeries();Save();}
    }
    async void QueueCompletedRip(object sender,RoutedEventArgs e)
    {
        if(adding||discBusy||lastRipFiles.Count==0) return;
        try {await AddCompletedRip();} catch(Exception ex) {Error(ex);RipStatus.Text="Raw files saved, but could not add to queue: "+ex.Message;}
    }
    async Task AddCompletedRip()
    {
        SetAdding(true);
        try {
            var selected=lastRipFiles.ToArray();var options=Snapshot();var preset=((HandBrakePreset)PresetChoice.SelectedItem).Name;
            var format=OutputTemplate.Text;var prefix=CompressionPrefix.Text;var fallback=ExistingUseSeries.IsChecked==true?compressionSeries:null;
            var roots=selected.Select(j=>Path.GetDirectoryName(j.Source)!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var result=await Task.Run(()=>FolderNames.PlanFiles(selected,roots,options,format,fallback,prefix));
            await Presets.Validate(options,CancellationToken.None);
            var added=0;
            foreach(var group in result.GroupBy(j=>j.Encoding!.Output,StringComparer.OrdinalIgnoreCase)) added+=Enqueue(group.ToList(),group.First().Encoding!,preset);
            QueueNotice.Text=added>0?$"Added {added} files from the completed disc." : "These files are already in the queue.";
            QueueNotice.Visibility=Visibility.Visible;Status.Text=QueueNotice.Text;Save();ShowCompressionQueue();
        } finally {SetAdding(false);}
    }
    void PauseAfterCurrent(object sender,RoutedEventArgs e)
    {
        if(!encoding||paused||clearingQueue) return;
        pauseAfterCurrent=!pauseAfterCurrent;
        Status.Text=pauseAfterCurrent?"The current encode will finish and save, then compression will pause.":"Compression will continue through the queue.";
        RefreshShell();
    }
    void StopAll(object sender,RoutedEventArgs e)
    {
        if(!encoding&&!discBusy) return;
        if(MessageBox.Show(this,"Interrupt the active disc operation and compression now?\n\nRaw files and completed compressed files are kept. The unfinished compressed file is discarded. Pending queue entries are retained and processing pauses. An interrupted disc rip must be started again.","Stop all processing",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
        StopAllOperations();
    }
    void StopAllOperations()
    {
        Stop(this,new());
        if(discBusy) CancelRip(this,new());
        Status.Text="Stopping all processing. Raw and completed files are kept; pending jobs remain paused.";
    }
}
