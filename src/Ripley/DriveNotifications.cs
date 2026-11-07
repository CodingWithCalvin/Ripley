using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
namespace Ripley;

public partial class MainWindow
{
    readonly Func<List<OpticalDrive>> readOpticalDrives;
    HwndSource? driveNotificationSource;
    DispatcherTimer? driveRefreshTimer;
    bool driveRefreshPending,driveScanRunning,updatingDriveList,driveMonitorClosed;
    uint changedMediaDrives;

    static List<OpticalDrive> ReadOpticalDrives()
    {
        var result=new List<OpticalDrive>();
        foreach(var drive in DriveInfo.GetDrives().Where(d=>d.DriveType==DriveType.CDRom).OrderBy(d=>d.Name)) {
            var label="";
            try {if(drive.IsReady)label=drive.VolumeLabel;}
            catch(IOException) { /* Empty, spinning up, or being ejected. Keep the physical drive listed. */ }
            catch(UnauthorizedAccessException) { /* MakeMKV can still inspect a disc Windows cannot mount. */ }
            result.Add(new(result.Count,"Optical drive",label,drive.Name.TrimEnd('\\')));
        }
        return result;
    }
    void StartDriveMonitoring(HwndSource source)
    {
        driveNotificationSource=source;source.AddHook(DriveNotificationHook);
        driveRefreshTimer=new DispatcherTimer(DispatcherPriority.Background,Dispatcher) {Interval=TimeSpan.FromSeconds(1.5)};
        driveRefreshTimer.Tick+=async (_,_)=>{driveRefreshTimer.Stop();await RefreshDriveList();};
        QueueDriveRefresh(0);
    }
    protected override void OnClosed(EventArgs e)
    {
        driveMonitorClosed=true;driveRefreshTimer?.Stop();driveNotificationSource?.RemoveHook(DriveNotificationHook);
        base.OnClosed(e);
    }
    IntPtr DriveNotificationHook(IntPtr hwnd,int message,IntPtr wParam,IntPtr lParam,ref bool handled)
    {
        // Default WM_DEVICECHANGE broadcasts need no registration. Never scan or wait inside the window hook.
        // https://learn.microsoft.com/windows/win32/devio/detecting-media-insertion-or-removal
        if(message!=0x0219)return IntPtr.Zero;
        var change=wParam.ToInt64();
        if(change==0x0007)QueueDriveRefresh(0); // DBT_DEVNODES_CHANGED: drive connected/disconnected.
        else if(change is 0x8000 or 0x8004 && lParam!=IntPtr.Zero && Marshal.ReadInt32(lParam)>=20 &&
            Marshal.ReadInt32(lParam,4)==2 && (Marshal.ReadInt16(lParam,16)&1)!=0) // VOLUME, MEDIA
            QueueDriveRefresh(unchecked((uint)Marshal.ReadInt32(lParam,12)));
        return IntPtr.Zero;
    }
    void QueueDriveRefresh(uint mediaDrives)
    {
        if(driveMonitorClosed)return;
        driveRefreshPending=true;changedMediaDrives|=mediaDrives;ScheduleDriveRefresh();
    }
    void ScheduleDriveRefresh()
    {
        if(!driveRefreshPending||driveMonitorClosed||discBusy||adding||driveScanRunning||driveRefreshTimer==null)return;
        driveRefreshTimer.Stop();driveRefreshTimer.Start();
    }
    async void RefreshDrives(object sender,RoutedEventArgs e)=>await RefreshDriveList();
    async Task RefreshDriveList()
    {
        if(driveMonitorClosed)return;
        if(discBusy||adding||driveScanRunning){driveRefreshPending=true;return;}
        driveRefreshTimer?.Stop();driveRefreshPending=false;
        var mediaDrives=changedMediaDrives;changedMediaDrives=0;
        driveScanRunning=true;RefreshShell();
        try {
            var drives=await Task.Run(readOpticalDrives);
            if(driveMonitorClosed)return;
            // A newer event makes this scan stale; apply only the next settled snapshot.
            if(driveRefreshPending||discBusy||adding) {driveRefreshPending=true;changedMediaDrives|=mediaDrives;return;}
            ApplyDetectedDrives(drives,mediaDrives);
        }
        catch(Exception ex) {if(!driveMonitorClosed)Error(new IOException("Could not refresh optical drives: "+ex.Message,ex));}
        finally {driveScanRunning=false;if(!driveMonitorClosed){RefreshShell();ScheduleDriveRefresh();}}
    }
    static bool MediaChanged(OpticalDrive? drive,uint mask)
    {
        if(drive?.Device is not {Length:>=2} device||device[1]!=':')return false;
        var letter=char.ToUpperInvariant(device[0])-'A';
        return letter is >=0 and <26 && (mask&(1u<<letter))!=0;
    }
    void ApplyDetectedDrives(List<OpticalDrive> drives,uint mediaDrives)
    {
        var previous=DriveChoice.SelectedItem as OpticalDrive;
        var selected=drives.FirstOrDefault(d=>d.Source.Equals(previous?.Source,StringComparison.OrdinalIgnoreCase))
            ??drives.FirstOrDefault(d=>d.Label.Length>0)??drives.FirstOrDefault();
        updatingDriveList=true;
        try {DriveChoice.ItemsSource=drives;DriveChoice.SelectedItem=selected;}
        finally {updatingDriveList=false;}
        if(!EditableRip)return;
        var restoredSameDisc=previous==null&&selected is {Label.Length:>0}&&selected.Label==DiscLabel.Text;
        var changed=previous?.Source!=selected?.Source||previous?.Label!=selected?.Label;
        if((changed&&!restoredSameDisc)||MediaChanged(previous,mediaDrives)||MediaChanged(selected,mediaDrives)||DiscLabel.Text.Length==0) {
            ApplyDriveSelection(selected);
            RipStatus.Text=selected==null?"No optical drives found. Connect a drive to continue."
                :selected.Label.Length>0?$"Disc detected · {selected.Label}. Inspect or rip when ready."
                :"Drive detected. Insert a disc; its label will update when ready. You can also inspect a loaded disc.";
            SaveHistory();
        }
    }
    void DriveChanged(object sender,SelectionChangedEventArgs e)
    {
        if(!updatingDriveList&&EditableRip)ApplyDriveSelection(DriveChoice.SelectedItem as OpticalDrive);
    }
    void ApplyDriveSelection(OpticalDrive? drive)
    {
        inspected=null;inspectedSource="";
        if(currentRipSession!=null){currentRipSession.Disc=null;currentRipSession.DiscSource="";}
        ripQueue.Reset(0);lastRipFiles.Clear();RipQueuePanel.Visibility=Visibility.Collapsed;
        lastSeasonLabel="\0";DiscLabel.Text=drive?.Label??"";DetectSeason();UpdateNamingPreview();CaptureHistory();
    }
}
