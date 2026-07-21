using System.Windows;

namespace WavyFi;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Any command-line argument switches to console mode; a plain start
        // (double-click) opens the GUI.
        if (e.Args.Length > 0)
        {
            int exitCode = Cli.CliRunner.Run(e.Args);
            Shutdown(exitCode);
            return;
        }

        new MainWindow().Show();
    }
}
