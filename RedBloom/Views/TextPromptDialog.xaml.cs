using System.Windows;
using System.Windows.Input;

namespace RedBloom.Views;

/// <summary>Asks for one line of text.</summary>
public partial class TextPromptDialog : Window
{
    public TextPromptDialog(string title, string note, string initial)
    {
        InitializeComponent();

        Title = title;
        Note.Text = note;
        Entry.Text = initial;

        // Ready to be typed over: the existing name is almost always what is being replaced.
        Loaded += (_, _) =>
        {
            Entry.Focus();
            Entry.SelectAll();
        };
    }

    /// <summary>What was typed, once the dialog was accepted.</summary>
    public string Answer { get; private set; } = string.Empty;

    private void Accept_Click(object sender, RoutedEventArgs e) => Accept();

    private void Entry_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Accept();
        }
    }

    private void Accept()
    {
        Answer = Entry.Text;
        DialogResult = true;
    }
}
