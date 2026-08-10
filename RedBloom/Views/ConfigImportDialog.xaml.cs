using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using RedBloom.Models;
using RedBloom.Services;
using RedBloom.Services.Ai;

namespace RedBloom.Views;

/// <summary>Takes a pasted tool config and hands back the agents it describes.</summary>
public partial class ConfigImportDialog : Window
{
    public ConfigImportDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => ConfigBox.Focus();
    }

    /// <summary>The agents the config produced, once the dialog closes with a true result.</summary>
    public IReadOnlyList<AiAgent> Agents { get; private set; } = [];

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void FromFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "JSON (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            ConfigBox.Text = File.ReadAllText(dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = ex.Message;
        }
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var result = AgentConfigImport.Import(ConfigBox.Text);

        if (result.Error is not null)
        {
            StatusText.Text = result.Error;
            return;
        }

        Agents = result.Agents;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
