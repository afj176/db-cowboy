using Microsoft.UI.Xaml;
namespace DbCowboy;

public partial class App : Application
{
    // Kept alive for the full process lifetime; Setup checks this before replacing files.
    readonly System.Threading.Mutex installerMutex=new(false,@"Local\DbCowboy.Postgres.App");
    Window? window;
    public App() { ShellIdentity.Initialize(); if (Environment.GetCommandLineArgs().Contains("--integration-ui") && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DBCOWBOY_DATA_HOME"))) Environment.SetEnvironmentVariable("DBCOWBOY_DATA_HOME", Path.Combine(AppContext.BaseDirectory, "ui-qa-data")); InitializeComponent(); UnhandledException += (_, e) => { Directory.CreateDirectory(Store.Root); File.WriteAllText(Path.Combine(Store.Root, "crash.txt"), e.Exception.ToString()); }; }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try { window = new MainWindow(); window.Activate(); }
        catch (Exception error) { Directory.CreateDirectory(Store.Root); File.WriteAllText(Path.Combine(Store.Root, "crash.txt"), error.ToString()); throw; }
    }
}

