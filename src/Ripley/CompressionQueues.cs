using System.Windows;
using System.Windows.Controls;
namespace Ripley;

public record QueueGroup(string Id,string Name,string Summary);

public partial class MainWindow
{
    bool updatingQueueGroups;
    string activeQueueId="";
    void UpdateQueueGroups()
    {
        var selected=(QueueList.SelectedItem as QueueGroup)?.Id??"";
        var groups=jobs.GroupBy(j=>j.QueueId,StringComparer.OrdinalIgnoreCase).Select(group=>new QueueGroup(group.Key,group.First().QueueName,
            $"{group.Count(j=>j.Status=="Completed")} / {group.Count()} done"+(group.Any(j=>j.Status is "Encoding" or "Publishing")?" · running":group.Any(j=>j.Status=="Failed")?" · failed":""))).ToList();
        groups.Insert(0,new("","All queues",$"{groups.Count} disc queues · {jobs.Count} files"));
        updatingQueueGroups=true;
        try {QueueList.ItemsSource=groups;QueueList.SelectedItem=groups.FirstOrDefault(g=>g.Id==selected)??groups[0];}
        finally {updatingQueueGroups=false;}
        FilterQueue();
    }
    void QueueSelected(object sender,SelectionChangedEventArgs e) {if(!updatingQueueGroups){FilterQueue();RefreshShell();}}
    void FilterQueue()
    {
        var id=(QueueList.SelectedItem as QueueGroup)?.Id??"";
        QueueGrid.ItemsSource=id.Length==0?jobs:jobs.Where(j=>j.QueueId.Equals(id,StringComparison.OrdinalIgnoreCase)).ToArray();
    }
    async void StartSelectedQueue(object sender,RoutedEventArgs e)
    {
        if(QueueList.SelectedItem is not QueueGroup {Id.Length:>0} selected)return;
        try {await StartQueues(selected.Id);} catch(Exception ex){Error(ex);}
    }
    async Task StartQueues(string? queueId)
    {
        if(encoding||clearingQueue||adding)return;
        foreach(var job in jobs.Where(j=>queueId==null||j.QueueId.Equals(queueId,StringComparison.OrdinalIgnoreCase)))
            if(job.Status is "Waiting" or "Queued" or "Paused" or "Failed") {job.Status="Queued";job.Error="";}
        paused=false;pauseAfterCurrent=false;PersistQueue();await Pump();
    }
}
