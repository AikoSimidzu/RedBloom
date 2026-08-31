using System.Diagnostics;
using System.IO;

namespace RedBloom.Services;

/// <summary>
/// The git operations projects need: cloning a repository into a folder, and publishing a folder as
/// a new repository. A token is used only for the network call and then wiped from the remote URL,
/// so it is never left sitting in the repo's config.
/// </summary>
public static class GitOps
{
    /// <summary>
    /// Clones a repository into <paramref name="targetDir"/> (which must not already exist). Returns
    /// the cloned path, or null with a message on failure.
    /// </summary>
    public static async Task<(string? Path, string? Error)> CloneAsync(string cloneUrl, string token, string targetDir)
    {
        if (Directory.Exists(targetDir))
        {
            return (null, "A folder with that name is already in the project.");
        }

        var parent = System.IO.Path.GetDirectoryName(targetDir);
        try
        {
            if (parent is { Length: > 0 })
            {
                Directory.CreateDirectory(parent);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, ex.Message);
        }

        var (ok, output) = await RunAsync($"clone {Quote(Authed(cloneUrl, token))} {Quote(targetDir)}", parent ?? ".", 180_000).ConfigureAwait(false);
        if (!ok)
        {
            return (null, Clean(output, token));
        }

        // Strip the token back out of the remote so it never persists on disk.
        await RunAsync($"-C {Quote(targetDir)} remote set-url origin {Quote(cloneUrl)}", parent ?? ".", 15_000).ConfigureAwait(false);
        return (targetDir, null);
    }

    /// <summary>
    /// Publishes a folder as the given repository: initialises it if needed, commits everything, and
    /// pushes. Returns null on success, or a message on failure.
    /// </summary>
    public static async Task<string?> PublishAsync(string folder, string cloneUrl, string token, string message = "Publish from RedBloom", IProgress<string>? progress = null)
    {
        if (!Directory.Exists(folder))
        {
            return "The project folder does not exist.";
        }

        var q = Quote(folder);

        if (!Directory.Exists(System.IO.Path.Combine(folder, ".git")))
        {
            var (initOk, initOut) = await RunAsync($"-C {q} init", folder, 20_000).ConfigureAwait(false);
            if (!initOk)
            {
                return Clean(initOut, token);
            }
        }

        // A linked source cloned into the project keeps its own .git, which git would otherwise add
        // as an embedded repo (a gitlink) — so none of its files reach the published repo. Hiding the
        // nested .git folders during the commit makes their contents publish as ordinary files; they
        // are restored afterwards, whatever happens, so the clones keep working.
        var hidden = HideNestedGit(folder);

        try
        {
            progress?.Report("Staging files…");

            // Clear the index first, so files newly covered by .gitignore drop out on a later publish
            // (a switch from "everything" to "project data only"), and so a folder previously added as
            // a gitlink is replaced by its real files.
            await RunAsync($"-C {q} rm -r --cached --ignore-unmatch .", folder, 60_000).ConfigureAwait(false);
            await RunAsync($"-C {q} add -A", folder, 120_000).ConfigureAwait(false);

            progress?.Report("Committing…");

            // -c identity so a commit works even when git has no global user configured.
            var (commitOk, commitOut) = await RunAsync(
                $"-C {q} -c user.email=redbloom@localhost -c user.name=RedBloom commit -m \"{message.Replace("\"", "'")}\"", folder, 120_000).ConfigureAwait(false);

            // "nothing to commit" is fine — the tree may already be committed.
            if (!commitOk && !commitOut.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase))
            {
                return Clean(commitOut, token);
            }

            await RunAsync($"-C {q} branch -M main", folder, 15_000).ConfigureAwait(false);
            await RunAsync($"-C {q} remote remove origin", folder, 15_000).ConfigureAwait(false);
            await RunAsync($"-C {q} remote add origin {Quote(Authed(cloneUrl, token))}", folder, 15_000).ConfigureAwait(false);

            progress?.Report("Uploading to GitHub…");

            var (pushOk, pushOut) = await RunReportingAsync(
                $"-C {q} push -u origin main --progress", folder, 300_000, progress, token).ConfigureAwait(false);

            // Clean the token out of the remote whatever happened.
            await RunAsync($"-C {q} remote set-url origin {Quote(cloneUrl)}", folder, 15_000).ConfigureAwait(false);

            return pushOk ? null : Clean(pushOut, token);
        }
        finally
        {
            RestoreNestedGit(hidden);
        }
    }

    private const string HiddenGit = ".git__rbpublish";

    /// <summary>
    /// Temporarily renames every nested <c>.git</c> folder (not the project's own) out of the way, so
    /// a source cloned into the project publishes its files instead of an empty gitlink. Returns the
    /// pairs to put back.
    /// </summary>
    private static List<(string Hidden, string Original)> HideNestedGit(string root)
    {
        var moved = new List<(string, string)>();

        try
        {
            var rootGit = System.IO.Path.Combine(System.IO.Path.GetFullPath(root), ".git");

            var nested = Directory.EnumerateDirectories(root, ".git", SearchOption.AllDirectories)
                .Where(dir => !string.Equals(System.IO.Path.GetFullPath(dir), rootGit, StringComparison.OrdinalIgnoreCase)
                              && !dir.Contains(System.IO.Path.DirectorySeparatorChar + HiddenGit + System.IO.Path.DirectorySeparatorChar))
                .ToList();

            foreach (var git in nested)
            {
                var parent = System.IO.Path.GetDirectoryName(git);
                if (parent is null)
                {
                    continue;
                }

                var target = System.IO.Path.Combine(parent, HiddenGit);
                try
                {
                    if (!Directory.Exists(target))
                    {
                        Directory.Move(git, target);
                        moved.Add((target, git));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A .git we cannot move is left alone; its folder just publishes as a gitlink.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort — publish proceeds with whatever could be hidden.
        }

        return moved;
    }

    private static void RestoreNestedGit(List<(string Hidden, string Original)> moved)
    {
        foreach (var (hidden, original) in moved)
        {
            try
            {
                if (Directory.Exists(hidden) && !Directory.Exists(original))
                {
                    Directory.Move(hidden, original);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine($"Could not restore {original}: {ex.Message}");
            }
        }
    }

    /// <summary>One pending change in a folder: its two-letter git status and its path.</summary>
    public readonly record struct Change(string Code, string Path);

    /// <summary>
    /// The changes that a publish would commit: git's own status for a repo, or every file for a
    /// folder not yet under git (a first publish).
    /// </summary>
    public static async Task<List<Change>> ChangesAsync(string folder)
    {
        var list = new List<Change>();
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return list;
        }

        if (Directory.Exists(Path.Combine(folder, ".git")))
        {
            var (ok, output) = await RunAsync($"-C {Quote(folder)} status --porcelain=v1", folder, 20_000).ConfigureAwait(false);
            if (ok)
            {
                foreach (var line in output.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (line.Length > 3)
                    {
                        list.Add(new Change(line[..2].Trim(), line[3..].Trim().Trim('"')));
                    }
                }
            }

            return list;
        }

        // No repo yet — the first publish commits everything, so preview the files that would go in.
        var skip = new[] { ".git", ".vs", "bin", "obj", "node_modules", "packages", ".chats", ".rooms" };
        var stack = new Stack<string>();
        stack.Push(folder);

        while (stack.Count > 0 && list.Count < 600)
        {
            var dir = stack.Pop();
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    if (!skip.Contains(Path.GetFileName(sub), StringComparer.OrdinalIgnoreCase))
                    {
                        stack.Push(sub);
                    }
                }

                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    list.Add(new Change("A", Path.GetRelativePath(folder, file).Replace('\\', '/')));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Skip a folder we cannot read.
            }
        }

        return list;
    }

    private static string Authed(string url, string token) =>
        url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && token.Length > 0
            ? "https://x-access-token:" + token + "@" + url["https://".Length..]
            : url;

    private static string Clean(string text, string token) =>
        token.Length > 0 ? text.Replace(token, "***") : text;

    private static string Quote(string s) => "\"" + s + "\"";

    /// <summary>
    /// Runs git and streams its progress to <paramref name="progress"/> as it happens — git writes
    /// its "Writing objects: NN%" lines to stderr, separated by carriage returns, so they are split on
    /// both CR and LF and reported one at a time. Returns success and the full combined output.
    /// </summary>
    private static Task<(bool Ok, string Output)> RunReportingAsync(
        string arguments, string workDir, int timeoutMs, IProgress<string>? progress, string token) => Task.Run(async () =>
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("git", arguments)
                {
                    WorkingDirectory = Directory.Exists(workDir) ? workDir : ".",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            if (!process.Start())
            {
                return (false, "git could not be started.");
            }

            var all = new System.Text.StringBuilder();

            async Task Pump(StreamReader reader)
            {
                var buffer = new char[512];
                var line = new System.Text.StringBuilder();
                int read;

                while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                {
                    for (var i = 0; i < read; i++)
                    {
                        var c = buffer[i];
                        if (c is '\r' or '\n')
                        {
                            if (line.Length > 0)
                            {
                                var text = line.ToString().Trim();
                                all.Append(text).Append('\n');
                                if (text.Length > 0 && progress is not null)
                                {
                                    progress.Report(Clean(text, token));
                                }

                                line.Clear();
                            }
                        }
                        else
                        {
                            line.Append(c);
                        }
                    }
                }

                if (line.Length > 0)
                {
                    all.Append(line.ToString().Trim()).Append('\n');
                }
            }

            var pumps = Task.WhenAll(Pump(process.StandardOutput), Pump(process.StandardError));

            if (!await Task.Run(() => process.WaitForExit(timeoutMs)).ConfigureAwait(false))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return (false, "git timed out.");
            }

            await pumps.ConfigureAwait(false);
            return (process.ExitCode == 0, all.ToString().Trim());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return (false, $"git is not available: {ex.Message}");
        }
    });

    private static Task<(bool Ok, string Output)> RunAsync(string arguments, string workDir, int timeoutMs) => Task.Run(() =>
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("git", arguments)
                {
                    WorkingDirectory = Directory.Exists(workDir) ? workDir : ".",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            if (!process.Start())
            {
                return (false, "git could not be started.");
            }

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return (false, "git timed out.");
            }

            var output = (stdout.Result + "\n" + stderr.Result).Trim();
            return (process.ExitCode == 0, output);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return (false, $"git is not available: {ex.Message}");
        }
    });
}
