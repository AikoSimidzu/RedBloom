using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using RedBloom.Services;

namespace RedBloom.Views;

/// <summary>
/// The extensions page, shown as a tab beside the appearance settings: lists every extension found,
/// lets each be turned on or off, and opens one in its own tab. Opening is routed to the window
/// through <see cref="OpenRequested"/>, the way the AI page hands agents up to be launched.
/// </summary>
public partial class ExtensionsPage : UserControl
{
    /// <summary>Raised when the user opens an extension; the window turns it into a tab.</summary>
    public static event Action<ExtensionStore.Extension>? OpenRequested;

    public ExtensionsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Reload();
    }

    private void Reload()
    {
        var rows = ExtensionStore.All().Select(e => new Row(e)).ToList();
        ExtList.ItemsSource = rows;
        EmptyNote.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is Row row)
        {
            OpenRequested?.Invoke(row.Extension);
        }
    }

    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(ExtensionStore.UserRoot);
            Process.Start(new ProcessStartInfo(ExtensionStore.UserRoot) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Debug.WriteLine($"Could not open extensions folder: {ex.Message}");
        }
    }

    private void Docs_Click(object sender, RoutedEventArgs e)
    {
        // Ships beside the app; falls back to the source copy when running from the tree.
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "EXTENSIONS.md"),
            Path.Combine(AppContext.BaseDirectory, "Assets", "EXTENSIONS.md"),
        };

        var doc = candidates.FirstOrDefault(File.Exists);
        try
        {
            Process.Start(new ProcessStartInfo(doc ?? "https://github.com") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Debug.WriteLine($"Could not open docs: {ex.Message}");
        }
    }

    /// <summary>One row in the list; toggling <see cref="Enabled"/> persists the choice at once.</summary>
    private sealed class Row(ExtensionStore.Extension extension) : INotifyPropertyChanged
    {
        public ExtensionStore.Extension Extension { get; } = extension;

        public string Icon => Extension.Manifest.Icon.Length > 0 ? Extension.Manifest.Icon : "";
        public string Name => Extension.Manifest.Name.Length > 0 ? Extension.Manifest.Name : Extension.Id;
        public string Description => Extension.Manifest.Description;

        public string Meta
        {
            get
            {
                var version = Extension.Manifest.Version.Length > 0 ? "v" + Extension.Manifest.Version : string.Empty;
                var source = Extension.BuiltIn
                    ? LocalizationService.T("L_ExtBuiltIn")
                    : LocalizationService.T("L_ExtUser");
                return version.Length > 0 ? $"{version} · {source}" : source;
            }
        }

        private bool _enabled = extension.Enabled;
        public bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value)
                {
                    return;
                }

                _enabled = value;
                Extension.Enabled = value;
                ExtensionStore.SetEnabled(Extension.Id, value);
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
