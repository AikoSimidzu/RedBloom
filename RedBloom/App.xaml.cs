using System.Windows;
using RedBloom.Services;

namespace RedBloom
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // This copy was started elevated to serve the one the user is looking at. It shows
            // no window and loads no settings: it exists to run what comes down the pipe and to
            // stop when that pipe closes.
            // Serving blocks here on purpose: the window named by StartupUri is not created
            // until this returns, so a helper never puts anything on screen. When the pipe
            // closes there is nothing left to shut down gracefully — no window, no settings, no
            // session — so the process simply ends rather than falling through into startup.
            if (e.Args is [ElevatedHost.Switch, var pipe, var secret])
            {
                ElevatedHost.Serve(pipe, secret);
                Environment.Exit(0);
            }

            // Surface an otherwise-fatal UI exception instead of vanishing: write it aside and show
            // it, so a crash on a new feature can be diagnosed rather than guessed at. Temporary.
            DispatcherUnhandledException += (_, ex) =>
            {
                try
                {
                    var log = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RedBloom", "crash.log");
                    System.IO.File.WriteAllText(log, ex.Exception.ToString());
                }
                catch
                {
                    // Logging the crash must not itself crash.
                }

                MessageBox.Show(ex.Exception.ToString(), "RedBloom error", MessageBoxButton.OK, MessageBoxImage.Error);
                ex.Handled = true;
            };

            // Loaded before base.OnStartup, which is what creates and shows the main window
            // via StartupUri. The other way round the window is built against the default
            // palette and only repainted afterwards.
            ThemeService.Load();

            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // Saving is debounced while the user drags things about; this writes whatever
            // is still pending so the last change is never lost on the way out.
            ThemeService.Flush();

            // An elevated worker must never outlive the window that asked for it, and neither
            // should an inference engine started from here.
            ElevatedHost.Stop();
            Services.Ai.OllamaEngine.Stop();
            Services.Ai.LocalRunner.Stop();
            Services.Ai.AgentTunnel.CloseAll();
            base.OnExit(e);
        }
    }
}
