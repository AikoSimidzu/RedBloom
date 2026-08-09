using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace RedBloom.Controls;

/// <summary>
/// Holds up to four terminals in one tab and arranges them by how many there are: one fills the
/// tab, two split it left and right, three put two on top and one across the bottom, four make
/// quarters. Draggable splitters sit between the rows and columns.
/// </summary>
/// <remarks>
/// Each pane keeps a permanent wrapper that is never re-parented — only its grid position
/// changes on a relayout. Re-parenting a WebView2 recreates its surface and flashes it blue, so
/// moving the panes rather than rebuilding them is what keeps a split clean.
/// </remarks>
public sealed class SplitContainer : Grid, IDisposable
{
    /// <summary>Four panes is the ceiling: past that the cells are too small to be useful.</summary>
    public const int MaxPanes = 4;

    private const double HeaderHeight = 22;

    private readonly List<TerminalView> _panes = [];
    private readonly Dictionary<TerminalView, Grid> _hosts = [];
    private readonly List<GridSplitter> _splitters = [];

    public SplitContainer(TerminalView first)
    {
        BuildColumnsAndRows();
        AddInternal(first);
        ActiveView = first;
        Layout();
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

        AddInternal(view);
        ActiveView = view;
        Layout();
    }

    /// <summary>Removes a pane and disposes it. Never removes the last — a tab keeps one pane.</summary>
    public void Remove(TerminalView view)
    {
        if (_panes.Count <= 1 || !_panes.Remove(view))
        {
            return;
        }

        if (_hosts.Remove(view, out var host))
        {
            host.Children.Clear();
            Children.Remove(host);
        }

        if (ReferenceEquals(ActiveView, view))
        {
            ActiveView = _panes[^1];
        }

        Layout();
        (view as IDisposable).Dispose();
    }

    private void AddInternal(TerminalView view)
    {
        _panes.Add(view);

        var host = new Grid();
        host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = BuildHeader(view);
        SetRow(header, 0);
        SetRow(view, 1);
        host.Children.Add(header);
        host.Children.Add(view);

        _hosts[view] = host;
        Children.Add(host);
    }

    // Columns [*, splitter, *] and rows [*, splitter, *]; panes live in the star tracks (0, 2).
    private void BuildColumnsAndRows()
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
    }

    private void Layout()
    {
        // Only the splitters are torn down and rebuilt; the pane wrappers stay put so their
        // WebView2 surfaces are never recreated.
        foreach (var splitter in _splitters)
        {
            Children.Remove(splitter);
        }

        _splitters.Clear();

        var count = _panes.Count;

        // (column, row, columnSpan, rowSpan) for each pane at each population. Two panes run the
        // full height as left and right; three put two on top and one across the bottom; four
        // make quarters.
        (int Col, int Row, int ColSpan, int RowSpan) Slot(int index) => count switch
        {
            1 => (0, 0, 3, 3),
            2 => index == 0 ? (0, 0, 1, 3) : (2, 0, 1, 3),
            3 => index switch { 0 => (0, 0, 1, 1), 1 => (2, 0, 1, 1), _ => (0, 2, 3, 1) },
            _ => index switch { 0 => (0, 0, 1, 1), 1 => (2, 0, 1, 1), 2 => (0, 2, 1, 1), _ => (2, 2, 1, 1) },
        };

        for (var i = 0; i < count; i++)
        {
            var host = _hosts[_panes[i]];
            var (col, row, colSpan, rowSpan) = Slot(i);
            SetColumn(host, col);
            SetRow(host, row);
            SetColumnSpan(host, colSpan);
            SetRowSpan(host, rowSpan);

            // The per-pane header (with its close button) only appears once the tab is split.
            host.RowDefinitions[0].Height = count > 1 ? new GridLength(HeaderHeight) : new GridLength(0);
        }

        if (count >= 2)
        {
            // Full height between the columns for two panes and for quarters; only the top row
            // for a three-way split, where the bottom is a single full-width pane.
            AddSplitter(GridResizeDirection.Columns, column: 1, row: 0, rowSpan: count == 3 ? 1 : 3, columnSpan: 1);
        }

        if (count >= 3)
        {
            AddSplitter(GridResizeDirection.Rows, column: 0, row: 1, rowSpan: 1, columnSpan: 3);
        }
    }

    private void AddSplitter(GridResizeDirection direction, int column, int row, int rowSpan, int columnSpan)
    {
        var splitter = new GridSplitter
        {
            ResizeDirection = direction,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            ShowsPreview = false,
        };

        if (direction == GridResizeDirection.Columns)
        {
            splitter.Width = 4;
            splitter.HorizontalAlignment = HorizontalAlignment.Center;
            splitter.VerticalAlignment = VerticalAlignment.Stretch;
        }
        else
        {
            splitter.Height = 4;
            splitter.HorizontalAlignment = HorizontalAlignment.Stretch;
            splitter.VerticalAlignment = VerticalAlignment.Center;
        }

        splitter.SetResourceReference(BackgroundProperty, "Divider");
        SetColumn(splitter, column);
        SetRow(splitter, row);
        SetColumnSpan(splitter, columnSpan);
        SetRowSpan(splitter, rowSpan);

        _splitters.Add(splitter);
        Children.Add(splitter);
    }

    /// <summary>
    /// The slim header carrying a close button, collapsed until the tab is split. It is WPF
    /// above the terminal rather than over it, since a WebView2 surface cannot be drawn on top of.
    /// </summary>
    private Border BuildHeader(TerminalView view)
    {
        var header = new Border { Height = HeaderHeight };
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

        return header;
    }

    public void Dispose()
    {
        foreach (var pane in _panes)
        {
            (pane as IDisposable).Dispose();
        }

        _panes.Clear();
        _hosts.Clear();
        _splitters.Clear();
        Children.Clear();
    }
}
