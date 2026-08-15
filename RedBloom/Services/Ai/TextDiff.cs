using System.IO;
using System.Text;

namespace RedBloom.Services.Ai;

/// <summary>
/// A line diff of a file's contents before and after a change, in the unified format the chat's
/// diff card already renders — so an edit made by a file tool shows its added and removed lines
/// even when the file is not inside a git repository (the chat's own workspace, a remote box).
/// </summary>
public static class TextDiff
{
    /// <summary>How many unchanged lines to keep around each change.</summary>
    private const int Context = 3;

    /// <summary>Above this many lines a diff is skipped, so a huge file cannot cost O(n·m).</summary>
    private const int MaxLines = 6000;

    /// <summary>
    /// The unified diff between two versions of a file, carrying the path so the card can jump to
    /// it. Empty when nothing changed, or when the file is too large to diff cheaply.
    /// </summary>
    public static string Unified(string path, string oldText, string newText)
    {
        oldText = oldText.Replace("\r\n", "\n");
        newText = newText.Replace("\r\n", "\n");

        if (oldText == newText)
        {
            return string.Empty;
        }

        var a = oldText.Length == 0 ? [] : oldText.Split('\n');
        var b = newText.Length == 0 ? [] : newText.Split('\n');

        if (a.Length > MaxLines || b.Length > MaxLines)
        {
            return string.Empty;
        }

        var body = Hunks(LineDiff(a, b));

        if (body.Length == 0)
        {
            return string.Empty;
        }

        var dir = Path.GetDirectoryName(path) ?? string.Empty;
        var file = Path.GetFileName(path);
        var file2 = file.Length == 0 ? path : file;

        var sb = new StringBuilder();

        // The header carries the folder as the "repo" root so the card can rebuild the full path
        // for its "go to file" action, exactly as a real git diff does.
        if (dir.Length > 0)
        {
            sb.Append("# repo: ").Append(dir).Append('\n');
        }

        sb.Append("diff --git a/").Append(file2).Append(" b/").Append(file2).Append('\n');
        sb.Append("--- a/").Append(file2).Append('\n');
        sb.Append("+++ b/").Append(file2).Append('\n');
        sb.Append(body);

        return sb.ToString();
    }

    /// <summary>The list of add / remove / keep operations turning <paramref name="a"/> into <paramref name="b"/>.</summary>
    private static List<(char Op, string Line)> LineDiff(string[] a, string[] b)
    {
        int n = a.Length, m = b.Length;

        // Longest common subsequence table, filled from the back, so the walk forward below can
        // choose the move that keeps the most lines in common.
        var lcs = new int[n + 1, m + 1];

        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        var ops = new List<(char, string)>();
        int x = 0, y = 0;

        while (x < n && y < m)
        {
            if (a[x] == b[y])
            {
                ops.Add((' ', a[x]));
                x++;
                y++;
            }
            else if (lcs[x + 1, y] >= lcs[x, y + 1])
            {
                ops.Add(('-', a[x]));
                x++;
            }
            else
            {
                ops.Add(('+', b[y]));
                y++;
            }
        }

        while (x < n)
        {
            ops.Add(('-', a[x++]));
        }

        while (y < m)
        {
            ops.Add(('+', b[y++]));
        }

        return ops;
    }

    /// <summary>
    /// Trims the operations to the changed regions with a few lines of context around each, and
    /// writes them out with the leading <c>+</c> / <c>-</c> / space the diff card reads.
    /// </summary>
    private static string Hunks(List<(char Op, string Line)> ops)
    {
        var n = ops.Count;
        var keep = new bool[n];

        for (var i = 0; i < n; i++)
        {
            if (ops[i].Op == ' ')
            {
                continue;
            }

            for (var j = Math.Max(0, i - Context); j <= Math.Min(n - 1, i + Context); j++)
            {
                keep[j] = true;
            }
        }

        // The line each op sits at in the old and new file, so a hunk header can carry real
        // numbers and the card can show a line-number gutter.
        var oldNo = new int[n];
        var newNo = new int[n];
        int ol = 1, nl = 1;

        for (var i = 0; i < n; i++)
        {
            oldNo[i] = ol;
            newNo[i] = nl;

            if (ops[i].Op != '+')
            {
                ol++;
            }

            if (ops[i].Op != '-')
            {
                nl++;
            }
        }

        var sb = new StringBuilder();
        var at = 0;

        while (at < n)
        {
            if (!keep[at])
            {
                at++;
                continue;
            }

            var start = at;
            while (at < n && keep[at])
            {
                at++;
            }

            int oldCount = 0, newCount = 0;
            for (var k = start; k < at; k++)
            {
                if (ops[k].Op != '+') oldCount++;
                if (ops[k].Op != '-') newCount++;
            }

            sb.Append("@@ -").Append(oldNo[start]).Append(',').Append(oldCount)
              .Append(" +").Append(newNo[start]).Append(',').Append(newCount).Append(" @@\n");

            for (var k = start; k < at; k++)
            {
                sb.Append(ops[k].Op).Append(ops[k].Line).Append('\n');
            }
        }

        return sb.ToString();
    }
}
