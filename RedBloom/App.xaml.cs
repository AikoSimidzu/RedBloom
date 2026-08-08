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
            base.OnExit(e);
        }
    }
}
