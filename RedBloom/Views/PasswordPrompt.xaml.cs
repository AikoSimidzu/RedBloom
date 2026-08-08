using System.Windows;
using System.Windows.Input;

namespace RedBloom.Views;

/// <summary>Asks for a password that is used for one connection and never stored.</summary>
public partial class PasswordPrompt : Window
{
    private PasswordPrompt(string target)
    {
        InitializeComponent();
        TargetText.Text = target;
        Loaded += (_, _) => PasswordField.Focus();
    }

    /// <returns>The entered password, or <c>null</c> if the user cancelled.</returns>
    public static string? Ask(Window owner, string target)
    {
        var prompt = new PasswordPrompt(target) { Owner = owner };
        return prompt.ShowDialog() == true ? prompt.PasswordField.Password : null;
    }

    private void Connect_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
