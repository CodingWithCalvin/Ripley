using System.Windows;
namespace Reel;
public partial class App : Application
{
    System.Threading.Mutex? instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        instance = new System.Threading.Mutex(true,"Local\\Reel.Desktop",out var created);
        if(!created) { MessageBox.Show("Reel is already running. Use the open window to avoid competing queues.","Reel"); Shutdown(); return; }
        base.OnStartup(e);
    }
    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
}
