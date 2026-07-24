using System.Windows;

namespace WavyFi;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Any command-line argument switches to console mode; a plain start
        // (double-click) opens the GUI. `--demo` alone opens the GUI on
        // synthetic data, for screenshots that expose no real networks.
        bool demo = e.Args.Length == 1 &&
                    e.Args[0].Equals("--demo", StringComparison.OrdinalIgnoreCase);
        if (e.Args.Length > 0 && !demo)
        {
            int exitCode = Cli.CliRunner.Run(e.Args);
            Shutdown(exitCode);
            return;
        }

        new MainWindow(demo).Show();
    }
}
