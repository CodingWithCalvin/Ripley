using System.Windows;
namespace Ripley;
public partial class App : Application
{
    System.Threading.Mutex? instance, previousInstance;
    protected override void OnStartup(StartupEventArgs e)
    {
        instance = new System.Threading.Mutex(true,"Local\\Ripley.Desktop",out var created);
        if(!created) { MessageBox.Show("Ripley is already running. Use the open window to avoid competing queues.","Ripley"); Shutdown(); return; }
        previousInstance = new System.Threading.Mutex(true,"Local\\Reel.Desktop",out var previousAvailable);
        if(!previousAvailable) { MessageBox.Show("Close Reel after any active work finishes, then open Ripley to carry over your saved settings and queue.","Ripley"); Shutdown(); return; }
        base.OnStartup(e);
        try { var window = new MainWindow(); MainWindow = window; window.Show(); }
        catch(Exception ex) { MessageBox.Show("Could not open Ripley. Your previous saved files were kept.\n\n" + ex.Message,"Ripley",MessageBoxButton.OK,MessageBoxImage.Error); Shutdown(1); }
    }
    protected override void OnExit(ExitEventArgs e) { previousInstance?.Dispose(); instance?.Dispose(); base.OnExit(e); }
}
