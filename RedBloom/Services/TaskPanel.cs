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

    /// <summary>The panel's words for the agent-notebook button and its picker.</summary>
    public static Dictionary<string, string> AgentLabels() => new()
    {
        ["agentTasks"] = LocalizationService.T("L_TaskAgentButton"),
        ["agentNone"] = LocalizationService.T("L_TaskAgentNone"),
    };

    /// <summary>
    /// Carries out a <c>manage_tasks</c> tool call against the two lists and reports both back so
    /// the model can see the ids to act on next.
    /// </summary>
    /// <remarks>
    /// Kept here rather than in each chat view so the one-to-one chat and the room drive the tool
    /// identically; the views only decide which list is "mine" and what to persist afterwards.
    /// </remarks>
    public static string HandleTool(
        string argumentsJson,
        List<TaskItem> shared,
        List<TaskItem>? mine,
        out bool changedShared,
        out bool changedMine,
        out string report)
    {
        changedShared = false;
        changedMine = false;
        report = string.Empty;

        JsonElement root;

        try
        {
            root = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson).RootElement;
        }
        catch (JsonException)
        {
            return "The task arguments could not be read. " + Render(shared, mine);
        }

        var op = Str(root, "op").Trim().ToLowerInvariant();
        var which = Str(root, "list").Trim().ToLowerInvariant();
        var toMine = which is "mine" or "self" or "own" or "notebook";
        var target = toMine ? mine : shared;

        if (op == "report")
        {
            report = Str(root, "note").Trim();
            return report.Length == 0
                ? "A report needs a note. " + Render(shared, mine)
                : "Posted the note to the user. " + Render(shared, mine);
        }

        if (op is "" or "list" or "read" or "get")
        {
            return Render(shared, mine);
        }

        if (target is null)
        {
            return "There is no private notebook here, so \"mine\" cannot be used. " + Render(shared, mine);
        }

        var changed = false;

        if (op == "add")
        {
            var task = new TaskItem { Name = Str(root, "name") };

            if (root.TryGetProperty("desc", out var d) && d.ValueKind == JsonValueKind.String)
            {
                task.Description = d.GetString() ?? string.Empty;
            }

            if (root.TryGetProperty("state", out var s) && Enum.TryParse<TaskState>(s.GetString(), true, out var st))
            {
                task.State = st;
            }

            target.Add(task);
            changed = true;
        }
        else if (op is "update" or "delete")
        {
            var id = Str(root, "id");

            // An id is enough on its own; but a model that names a task instead of quoting its id is
            // met halfway by matching on the name, so a plain "mark 'tests' done" still lands.
            var task = target.FirstOrDefault(t => t.Id == id)
                       ?? target.FirstOrDefault(t => string.Equals(t.Name, Str(root, "name"), StringComparison.OrdinalIgnoreCase))
                       ?? (id.Length > 0 ? target.FirstOrDefault(t => string.Equals(t.Name, id, StringComparison.OrdinalIgnoreCase)) : null);

            if (task is null)
            {
                return "No task matched that id or name. " + Render(shared, mine);
            }

            if (op == "delete")
            {
                target.Remove(task);
                changed = true;
            }
            else
            {
                if (root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                {
                    task.Name = n.GetString() ?? string.Empty;
                }

                if (root.TryGetProperty("desc", out var d) && d.ValueKind == JsonValueKind.String)
                {
                    task.Description = d.GetString() ?? string.Empty;
                }

                if (root.TryGetProperty("state", out var s) && Enum.TryParse<TaskState>(s.GetString(), true, out var st))
                {
                    task.State = st;
                }

                changed = true;
            }
        }
        else
        {
            return $"Unknown op \"{op}\". Use list, add, update, delete or report. " + Render(shared, mine);
        }

        if (toMine)
        {
            changedMine = changed;
        }
        else
        {
            changedShared = changed;
        }

        return Render(shared, mine);
    }

    /// <summary>
    /// A short block naming the current tasks, appended to what the model is sent so it starts a
    /// turn already aware of the list and the ids to act on — without a first "list" call. Empty
    /// when both lists are empty, so a chat with no tasks sends nothing extra.
    /// </summary>
    public static string SeedBlock(List<TaskItem> shared, List<TaskItem>? mine)
    {
        if (shared.Count == 0 && (mine is null || mine.Count == 0))
        {
            return string.Empty;
        }

        return "[Current task lists — you may change them with the manage_tasks tool; the ids below "
            + "are what it acts on. Keep each task's state current as you work.]\n"
            + Render(shared, mine);
    }

    /// <summary>Both lists as compact text with ids, the way the tool result hands them back.</summary>
    private static string Render(List<TaskItem> shared, List<TaskItem>? mine)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Shared list:");
        AppendList(sb, shared);

        if (mine is not null)
        {
            sb.AppendLine("Your notebook:");
            AppendList(sb, mine);
        }

        return sb.ToString().TrimEnd();
    }

    private static void AppendList(System.Text.StringBuilder sb, List<TaskItem> tasks)
    {
        if (tasks.Count == 0)
        {
            sb.AppendLine("  (empty)");
            return;
        }

        foreach (var t in tasks)
        {
            sb.Append("  [").Append(t.Id).Append("] ").Append(t.State).Append(" — ").Append(t.Name);

            if (t.Description.Length > 0)
            {
                sb.Append(": ").Append(t.Description);
            }

            if (t.Assignee.Length > 0)
            {
                sb.Append(" (@").Append(t.Assignee).Append(')');
            }

            sb.AppendLine();
        }
    }

    private static string Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
}
