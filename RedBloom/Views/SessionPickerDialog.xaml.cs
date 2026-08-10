using System.Windows;
using System.Windows.Input;
using RedBloom.Models;

namespace RedBloom.Views;

/// <summary>Picks one saved SSH connection to attach to a message.</summary>
public partial class SessionPickerDialog : Window
{
    public SessionPickerDialog(IReadOnlyList<SshSession> sessions)
    {
        InitializeComponent();

        List.ItemsSource = sessions;
        List.SelectedIndex = 0;
    }

    /// <summary>The chosen connection, or null when the dialog was dismissed.</summary>
    public SshSession? Chosen { get; private set; }

    private void Choose_Click(object sender, RoutedEventArgs e) => Accept();

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Accept();

    private void Accept()
    {
        if (List.SelectedItem is not SshSession session)
        {
            return;
        }

        Chosen = session;
        DialogResult = true;
    }
}
