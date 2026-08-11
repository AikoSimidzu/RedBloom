using System.Windows;
using RedBloom.Models;

namespace RedBloom.Views;

/// <summary>
/// Names a chat: what it is called in the list, and what the agent is called inside it.
/// </summary>
/// <remarks>
/// A window rather than the tab popup these fields used to live in. That popup closes on any
/// click outside itself and never took keyboard focus, so the boxes could not actually be typed
/// into — and a name belongs with the list where chats are found, not with the tab's paintwork.
/// </remarks>
public partial class ChatNamesDialog : Window
{
    private readonly ChatSession _chat;

    public ChatNamesDialog(ChatSession chat)
    {
        InitializeComponent();

        _chat = chat;
        TitleBox.Text = chat.Title;
        BotBox.Text = chat.BotName;

        Loaded += (_, _) =>
        {
            TitleBox.Focus();
            TitleBox.SelectAll();
        };
    }

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        var named = TitleBox.Text.Trim();

        // An empty name means "go back to being named after the first question", which is what
        // the chat is called until someone renames it.
        _chat.Title = named.Length > 0 ? named
            : _chat.Turns.FirstOrDefault(t => t.Role == "user") is { } first ? ChatSession.TitleFrom(first.Text)
            : string.Empty;

        _chat.BotName = BotBox.Text.Trim();

        DialogResult = true;
    }
}
