using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using RedBloom.Controls;

namespace RedBloom;

/// <summary>
/// One entry in the tab strip. Most tabs hold a terminal, but the appearance page is a tab
/// too, so the content is any element rather than a <see cref="TerminalView"/>.
/// </summary>
public sealed class TerminalTab : INotifyPropertyChanged
{
    private string _title;
    private string _toolTip;
    private bool _isSelected;
    private bool _hasEnded;

    public TerminalTab(FrameworkElement content, string title, string glyph, string toolTip)
    {
        Content = content;
        _title = title;
        _toolTip = toolTip;
        Glyph = glyph;
    }

    public FrameworkElement Content { get; }

    /// <summary>The terminal this tab holds, or null for pages such as the settings tab.</summary>
    public TerminalView? View => Content as TerminalView;

    public string Glyph { get; }

    public string Title
    {
        get => _title;
        set => Set(ref _title, value);
    }

    public string ToolTip
    {
        get => _toolTip;
        set => Set(ref _toolTip, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    /// <summary>The shell exited or the connection dropped; the scrollback is still readable.</summary>
    public bool HasEnded
    {
        get => _hasEnded;
        set => Set(ref _hasEnded, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
