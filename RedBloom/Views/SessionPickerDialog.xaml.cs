using System.Windows;
using System.Windows.Input;
using RedBloom.Models;

namespace RedBloom.Views;

/// <summary>Picks one saved SSH connection to attach to a message.</summary>
public partial class SessionPickerDialog : Window
{
    private readonly IReadOnlyList<SshSession> _sessions;

    public SessionPickerDialog(IReadOnlyList<SshSession> sessions)
    {
        InitializeComponent();

        _sessions = sessions;
        List.ItemsSource = sessions;
        List.SelectedIndex = 0;

        Loaded += (_, _) => FilterBox.Focus();
    }

    /// <summary>
    /// Narrows the list as it is typed into, by name or by where it connects.
    /// </summary>
    /// <remarks>
    /// Both, because a saved connection is found either way: by what the user called it, or by
    /// the host they remember.
    /// </remarks>
    private void Filter_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var wanted = FilterBox.Text.Trim();

        FilterHint.Visibility = FilterBox.Text.Length == 0
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;

        List.ItemsSource = wanted.Length == 0
            ? _sessions
            : [.. _sessions.Where(session =>
                session.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase)
                || session.DisplayTarget.Contains(wanted, StringComparison.OrdinalIgnoreCase))];

        if (List.Items.Count > 0)
        {
            List.SelectedIndex = 0;
        }
    }

    /// <summary>The chosen connection, or null when the dialog was dismissed.</summary>
    public SshSession? Chosen { get; private set; }

    /// <summary>Whether the user asked for its password to travel with it.</summary>
    public bool SendsSecret { get; private set; }

    /// <summary>The replacement title bar drags the window, as the system one would have.</summary>
    private void Bar_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Choose_Click(object sender, RoutedEventArgs e) => Accept();

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Accept();

    private void Accept()
    {
        if (List.SelectedItem is not SshSession session)
        {
            return;
        }

        Chosen = session;
        SendsSecret = SendSecret.IsChecked == true;
        DialogResult = true;
    }
}
