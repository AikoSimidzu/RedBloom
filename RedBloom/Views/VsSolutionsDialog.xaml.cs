using System.Windows;
using System.Windows.Input;
using RedBloom.Services;

namespace RedBloom.Views;

/// <summary>Picks one of the Visual Studio solutions found on this machine to link to a project.</summary>
public partial class VsSolutionsDialog : Window
{
    private VsSolutionsDialog(List<VisualStudioSources.Solution> solutions)
    {
        InitializeComponent();
        List.ItemsSource = solutions;
        Empty.Visibility = solutions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Shows the picker; the chosen solution, or null if cancelled.</summary>
    public static VisualStudioSources.Solution? Pick(Window? owner, List<VisualStudioSources.Solution> solutions)
    {
        var dialog = new VsSolutionsDialog(solutions) { Owner = owner };

        return dialog.ShowDialog() == true && dialog.List.SelectedItem is VisualStudioSources.Solution chosen
            ? chosen
            : null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is VisualStudioSources.Solution)
        {
            DialogResult = true;
        }
    }

    private void List_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (List.SelectedItem is VisualStudioSources.Solution)
        {
            DialogResult = true;
        }
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
