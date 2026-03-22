using System.Windows;

namespace ArbiterHost
{
    public partial class App : Application
    {
        private void App_Startup(object sender, StartupEventArgs e)
        {
            Exit += App_Exit;
            var launcher = new LauncherWindow();
            MainWindow = launcher;
            launcher.Show();
        }

        private static void App_Exit(object sender, ExitEventArgs e)
        {
            // Terminate the Arbiter Engine server if we started it.
            try
            {
                if (AppConfig.EngineProcess != null && !AppConfig.EngineProcess.HasExited)
                    AppConfig.EngineProcess.Kill(entireProcessTree: true);
            }
            catch { /* best-effort */ }
        }
    }
}

