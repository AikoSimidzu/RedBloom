using System.Windows;
using System.Windows.Input;
using RedBloom.Models;

namespace RedBloom.Views;

/// <summary>Picks the model (agent) a new chat should use — for creating a chat inside a project.</summary>
public partial class AgentPickerDialog : Window
{
    private AgentPickerDialog(List<AiAgent> agents)
    {
        InitializeComponent();
        List.ItemsSource = agents;
        List.SelectedItem = agents.FirstOrDefault();
        Empty.Visibility = agents.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Shows the picker; the chosen agent, or null if cancelled.</summary>
    public static AiAgent? Pick(Window? owner, List<AiAgent> agents)
    {
        var dialog = new AgentPickerDialog(agents) { Owner = owner };

        return dialog.ShowDialog() == true && dialog.List.SelectedItem is AiAgent chosen ? chosen : null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is AiAgent)
        {
            DialogResult = true;
        }
    }

    private void List_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (List.SelectedItem is AiAgent)
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
