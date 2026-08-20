namespace RedBloom.Models;

/// <summary>
/// What an extension declares about itself in its <c>manifest.json</c>. An extension is a folder with
/// this file and an HTML entry page; the app hosts the page in a WebView2 and gives it a small,
/// declared set of host powers (running the programs it lists, reading and writing files in its own
/// data folder).
/// </summary>
public sealed class ExtensionManifest
{
    /// <summary>Stable id, also the folder name; used to file the extension's data and enabled state.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Shown in the list and on the tab.</summary>
    public string Name { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    /// <summary>A single Segoe MDL2 glyph (e.g. "") for the list and tab.</summary>
    public string Icon { get; set; } = string.Empty;

    /// <summary>The HTML page loaded into the WebView2, relative to the extension folder.</summary>
    public string Entry { get; set; } = "index.html";

    /// <summary>
    /// The external programs this extension is allowed to run (by name, e.g. "arduino-cli"). The host
    /// refuses any exec whose program is not on this list, so a page cannot run arbitrary commands.
    /// </summary>
    public List<string> Programs { get; set; } = [];
}
