using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using RedBloom.Services;

namespace RedBloom.Controls;

/// <summary>
/// A small floating badge that appears while an agent is driving the mouse or keyboard, so the user
/// always knows when input is not their own — and is reminded of the panic key that stops it. Click-
/// through and topmost, so it never gets in the way of what the agent is doing underneath.
/// </summary>
public sealed class InputIndicator : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x20;
    private const int WsExToolWindow = 0x80;
    private const int WsExNoActivate = 0x08000000;

    public InputIndicator()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Focusable = false;
        ShowActivated = false;

        var badge = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x8C, 0x2B, 0x33)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x4D, 0x5A)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(14, 9, 16, 9),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 18,
                ShadowDepth = 3,
                Opacity = 0.5,
                Color = Colors.Black,
            },
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal };

        var dot = new Ellipse
        {
            Width = 9,
            Height = 9,
            Fill = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 9, 0),
        };

        // A soft pulse, so the badge reads as "live" rather than a static label.
        var pulse = new System.Windows.Media.Animation.DoubleAnimation(1, 0.3, TimeSpan.FromSeconds(0.7))
        {
            AutoReverse = true,
            RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
        };
        dot.BeginAnimation(OpacityProperty, pulse);

        _label = new TextBlock
        {
            Foreground = Brushes.White,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
        };

        row.Children.Add(dot);
        row.Children.Add(_label);
        badge.Child = row;
        Content = badge;

        Loaded += OnLoaded;
    }

    private readonly TextBlock _label;

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // Click-through and never activated, so it floats over the agent's work without stealing
        // focus or blocking a click meant for the app underneath.
        var hwnd = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(hwnd, GwlExStyle);
        SetWindowLong(hwnd, GwlExStyle, style | WsExTransparent | WsExToolWindow | WsExNoActivate);

        // Bottom-centre of the primary screen's working area.
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - ActualWidth) / 2;
        Top = area.Bottom - ActualHeight - 24;
    }

    /// <summary>Sets the badge text, e.g. "Agent is controlling input — Ctrl+Alt+Pause to stop".</summary>
    public void SetText(string text) => _label.Text = text;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
}
