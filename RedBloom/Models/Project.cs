using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace RedBloom.Models;

/// <summary>
/// A workspace that groups chats, rooms and files under one roof — a real folder on disk plus the
/// notes and links that tie the work together.
/// </summary>
/// <remarks>
/// Projects are the personal-work side of RedBloom, not only the AI side: a place to gather and
/// track everything to do with one piece of work. The metadata lives as its own file under
/// <c>%APPDATA%\RedBloom\projects</c> (the same shape as a room), while <see cref="Folder"/> points
/// at a real, user-visible directory where the actual files live. Chats and rooms belong to a
/// project by carrying its <see cref="Id"/>, rather than the project holding a list of them, so a
/// chat is never orphaned by an edit to a list it does not own.
/// </remarks>
public sealed class Project : INotifyPropertyChanged
{
    private string _name = "Project";
    private string _description = string.Empty;

    public string Id { get; set; } = Guid.NewGuid().ToString("n");

    public string Name { get => _name; set => Set(ref _name, value); }

    /// <summary>A free-text note on what this project is — shown at the top of its home.</summary>
    public string Description { get => _description; set => Set(ref _description, value); }

    /// <summary>The real directory on disk this project's files live in.</summary>
    public string Folder { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    /// <summary>This project's own look in the sidebar and the tab strip.</summary>
    public TabCardStyle Card { get; set; } = new();

    /// <summary>The relationship tree: nodes and the connections drawn between them.</summary>
    public ProjectGraph Graph { get; set; } = new();

    /// <summary>Source folders and repositories linked to this project and tracked with it.</summary>
    public List<ProjectSource> Sources { get; set; } = [];

    [JsonIgnore]
    public string Summary
    {
        get
        {
            var when = UpdatedAt.Date == DateTime.Today
                ? UpdatedAt.ToString("HH:mm")
                : UpdatedAt.ToString("d MMM");

            return string.IsNullOrWhiteSpace(_description) ? when : $"{when} · {_description}";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event Action? Changed;

    public void Touch()
    {
        UpdatedAt = DateTime.Now;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Summary)));
        Changed?.Invoke();
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Summary)));
        Changed?.Invoke();
    }
}
