using System.Windows;
using System.Windows.Input;

namespace RedBloom.Views;

/// <summary>Creating a project: its name and a short description. The folder is made from the name.</summary>
public partial class ProjectDialog : Window
{
    private ProjectDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => NameBox.Focus();
    }

    /// <summary>What the user typed when they chose to create a project.</summary>
    public readonly record struct Result(string Name, string Description);

    /// <summary>Shows the dialog; the entered name and description, or null if cancelled.</summary>
    public static Result? Create(Window owner)
    {
        var dialog = new ProjectDialog { Owner = owner };

        return dialog.ShowDialog() == true
            ? new Result(dialog.NameBox.Text.Trim(), dialog.DescriptionBox.Text.Trim())
            : null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            NameBox.Focus();
            return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
