using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace RedBloom.Controls;

/// <summary>
/// Holds up to four terminals in one tab and arranges them by how many there are: one fills the
/// tab, two split it left and right, three put two on top and one across the bottom, four make
/// quarters. Draggable splitters sit between the rows and columns.
/// </summary>
public sealed class SplitContainer : Grid, IDisposable
{
    /// <summary>Four panes is the ceiling: past that the cells are too small to be useful.</summary>
    public const int MaxPanes = 4;

    private readonly List<TerminalView> _panes = [];

    public SplitContainer(TerminalView first)
    {
        _panes.Add(first);
        ActiveView = first;
        Rebuild();
    }

    public IReadOnlyList<TerminalView> Panes => _panes;

    public int Count => _panes.Count;

    public bool CanAdd => _panes.Count < MaxPanes;

    /// <summary>The pane a split acts on and the one focused when the tab is shown.</summary>
    public TerminalView? ActiveView { get; private set; }

    /// <summary>Raised when a pane's own close button is pressed.</summary>
    public event Action<TerminalView>? PaneCloseRequested;

    public void SetActive(TerminalView view)
    {
        if (_panes.Contains(view))
        {
            ActiveView = view;
        }
    }

    /// <summary>Appends a pane and makes it active. Caller checks <see cref="CanAdd"/> first.</summary>
    public void Add(TerminalView view)
    {
        if (_panes.Count >= MaxPanes)
        {
            return;
        }

        _panes.Add(view);
        ActiveView = view;
        Rebuild();
    }

    /// <summary>Removes a pane and disposes it. Never removes the last — a tab keeps one pane.</summary>
    public void Remove(TerminalView view)
    {
        if (_panes.Count <= 1 || !_panes.Remove(view))
        {
            return;
        }

        if (ReferenceEquals(ActiveView, view))
        {
            ActiveView = _panes[^1];
        }

        Rebuild();
        (view as IDisposable).Dispose();
    }

    // Grid geometry: columns [*, splitter, *], rows [*, splitter, *]. Panes live in the star
    // columns/rows (0 and 2); the auto tracks (1) carry the splitters.
    private void Rebuild()
    {
        // Detach the panes from the wrappers they were in, or re-parenting them below throws.
        foreach (var host in Children.OfType<Grid>())
        {
            host.Children.Clear();
        }

        Children.Clear();
        ColumnDefinitions.Clear();
        RowDefinitions.Clear();

        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var count = _panes.Count;

        // (column, row, columnSpan) for each pane at each population.
        (int Col, int Row, int Span) Slot(int index) => count switch
        {
            1 => (0, 0, 3),
            2 => index == 0 ? (0, 0, 1) : (2, 0, 1),
            3 => index switch { 0 => (0, 0, 1), 1 => (2, 0, 1), _ => (0, 2, 3) },
            _ => index switch { 0 => (0, 0, 1), 1 => (2, 0, 1), 2 => (0, 2, 1), _ => (2, 2, 1) },
        };

        for (var i = 0; i < count; i++)
        {
            var (col, row, span) = Slot(i);
            var host = BuildPaneHost(_panes[i]);
            SetColumn(host, col);
            SetRow(host, row);
            SetColumnSpan(host, span);
            Children.Add(host);
        }

        // Vertical splitter between the two columns. Full height for quarters; only the top row
        // otherwise, so it never lands on the full-width bottom pane of a three-way split.
        if (count >= 2)
        {
            var vertical = new GridSplitter
            {
                Width = 4,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Stretch,
                ResizeDirection = GridResizeDirection.Columns,
                ResizeBehavior = GridResizeBehavior.PreviousAndNext,
                ShowsPreview = false,
            };
            vertical.SetResourceReference(BackgroundProperty, "Divider");
            SetColumn(vertical, 1);
            SetRow(vertical, 0);
            SetRowSpan(vertical, count >= 4 ? 3 : 1);
            Children.Add(vertical);
        }

        // Horizontal splitter between the two rows, once there is a bottom row.
        if (count >= 3)
        {
            var horizontal = new GridSplitter
            {
                Height = 4,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
                ResizeDirection = GridResizeDirection.Rows,
                ResizeBehavior = GridResizeBehavior.PreviousAndNext,
                ShowsPreview = false,
            };
            horizontal.SetResourceReference(BackgroundProperty, "Divider");
            SetColumn(horizontal, 0);
            SetColumnSpan(horizontal, 3);
            SetRow(horizontal, 1);
            Children.Add(horizontal);
        }
    }

    /// <summary>
    /// Wraps a pane with a slim header carrying a close button. The header only shows once the
    /// tab is actually split — a lone terminal keeps the whole cell. The header is WPF above the
    /// terminal rather than over it, since a WebView2 surface cannot be drawn on top of.
    /// </summary>
    private FrameworkElement BuildPaneHost(TerminalView view)
    {
        if (_panes.Count < 2)
        {
            return view;
        }

        var host = new Grid();
        host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new Border { Height = 22 };
        header.SetResourceReference(BackgroundProperty, "TabBacking");

        var close = new Button
        {
            Content = "",
            Width = 18,
            Height = 18,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
            FontSize = 9,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        close.SetResourceReference(StyleProperty, "IconButton");
        close.SetResourceReference(ToolTipProperty, "L_CloseSplitTip");
        close.Click += (_, _) => PaneCloseRequested?.Invoke(view);
        header.Child = close;

        SetRow(header, 0);
        SetRow(view, 1);
        host.Children.Add(header);
        host.Children.Add(view);
        return host;
    }

    public void Dispose()
    {
        foreach (var pane in _panes)
        {
            (pane as IDisposable).Dispose();
        }

        _panes.Clear();
        Children.Clear();
    }
}
