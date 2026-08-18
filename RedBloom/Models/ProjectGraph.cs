namespace RedBloom.Models;

/// <summary>
/// A project's relationship tree: the nodes it gathers and the connections drawn between them. Held
/// on the <see cref="Project"/> and saved with it, so the map of how the work fits together lives
/// alongside the work.
/// </summary>
public sealed class ProjectGraph
{
    public List<GraphNode> Nodes { get; set; } = [];

    public List<GraphEdge> Edges { get; set; } = [];
}

/// <summary>
/// One thing on the graph — a note, or a chat, room or file the project holds — placed at a point
/// on the canvas.
/// </summary>
public sealed class GraphNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");

    /// <summary>What the node stands for: <c>note</c>, <c>chat</c>, <c>room</c>, <c>file</c>, <c>milestone</c>.</summary>
    public string Kind { get; set; } = "note";

    public string Label { get; set; } = string.Empty;

    /// <summary>What it points at when it is a chat, room or file — an id or a path. Empty for a note.</summary>
    public string RefId { get; set; } = string.Empty;

    /// <summary>A longer description shown when the node is selected.</summary>
    public string Note { get; set; } = string.Empty;

    /// <summary>The node card's accent colour as <c>#rrggbb</c>, or empty to follow the theme.</summary>
    public string Color { get; set; } = string.Empty;

    public double X { get; set; }

    public double Y { get; set; }
}

/// <summary>
/// A connection between two nodes, with its own description and look — the "card" that styles the
/// line: its colour, thickness, whether it is dashed, and whether it points.
/// </summary>
public sealed class GraphEdge
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");

    /// <summary>The node this connection starts at, by <see cref="GraphNode.Id"/>.</summary>
    public string From { get; set; } = string.Empty;

    /// <summary>The node this connection ends at.</summary>
    public string To { get; set; } = string.Empty;

    /// <summary>A short word on the line — what the connection is.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>A longer description of the connection, shown when it is selected.</summary>
    public string Note { get; set; } = string.Empty;

    /// <summary>The line's colour as <c>#rrggbb</c>, or empty to follow the theme.</summary>
    public string Color { get; set; } = string.Empty;

    /// <summary>The line's thickness in pixels.</summary>
    public double Width { get; set; } = 2;

    /// <summary>Whether the line is drawn dashed rather than solid.</summary>
    public bool Dashed { get; set; }

    /// <summary>Whether the line carries an arrowhead at its end — a direction rather than a link.</summary>
    public bool Directed { get; set; } = true;
}
