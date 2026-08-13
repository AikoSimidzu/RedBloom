using System.Text.Json;
using RedBloom.Models;

namespace RedBloom.Services;

/// <summary>
/// The pieces the chat page needs for its task list — the localised labels, and applying the edits
/// it sends back — shared by the one-to-one chat and the room so both behave the same.
/// </summary>
public static class TaskPanel
{
    /// <summary>The five states, by their enum name, in the language of the window.</summary>
    public static Dictionary<string, string> Statuses() => new()
    {
        ["NotStarted"] = LocalizationService.T("L_TaskNotStarted"),
        ["InProgress"] = LocalizationService.T("L_TaskInProgress"),
        ["Done"] = LocalizationService.T("L_TaskDone"),
        ["NeedsRework"] = LocalizationService.T("L_TaskNeedsRework"),
        ["Tests"] = LocalizationService.T("L_TaskTests"),
    };

    /// <summary>The panel's own words.</summary>
    public static Dictionary<string, string> Labels() => new()
    {
        ["tasks"] = LocalizationService.T("L_TasksButton"),
        ["add"] = LocalizationService.T("L_TaskAdd"),
        ["del"] = LocalizationService.T("L_TaskDelete"),
        ["shareOne"] = LocalizationService.T("L_TaskShareOne"),
        ["shareAll"] = LocalizationService.T("L_TaskShareAll"),
        ["shareHeader"] = LocalizationService.T("L_TaskShareHeader"),
        ["none"] = LocalizationService.T("L_TaskNone"),
        ["namePh"] = LocalizationService.T("L_TaskNamePh"),
        ["descPh"] = LocalizationService.T("L_TaskDescPh"),
        ["unassigned"] = LocalizationService.T("L_TaskUnassigned"),
    };

    /// <summary>One task as the page reads it.</summary>
    public static object Item(TaskItem t) =>
        new { id = t.Id, name = t.Name, desc = t.Description, state = t.State.ToString(), assignee = t.Assignee };

    /// <summary>Applies an edit the page sent to a task list; true when something changed.</summary>
    public static bool Apply(List<TaskItem> tasks, JsonElement message)
    {
        var op = message.TryGetProperty("op", out var o) ? o.GetString() : null;

        if (op == "add")
        {
            tasks.Add(new TaskItem { Name = Str(message, "name") });
            return true;
        }

        var id = Str(message, "id");

        if (id.Length == 0)
        {
            return false;
        }

        if (op == "delete")
        {
            return tasks.RemoveAll(t => t.Id == id) > 0;
        }

        if (op != "update" || tasks.FirstOrDefault(t => t.Id == id) is not { } task)
        {
            return false;
        }

        if (message.TryGetProperty("name", out var name))
        {
            task.Name = name.GetString() ?? string.Empty;
        }

        if (message.TryGetProperty("desc", out var desc))
        {
            task.Description = desc.GetString() ?? string.Empty;
        }

        if (message.TryGetProperty("assignee", out var assignee))
        {
            task.Assignee = assignee.GetString() ?? string.Empty;
        }

        if (message.TryGetProperty("state", out var state)
            && Enum.TryParse<TaskState>(state.GetString(), out var parsed))
        {
            task.State = parsed;
        }

        return true;
    }

    private static string Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
}
