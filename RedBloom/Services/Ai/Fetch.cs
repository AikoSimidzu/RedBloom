using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace RedBloom.Services.Ai;

/// <summary>
/// Downloading a large file: in several streams at once, resumable, and able to survive the
/// connection dropping.
/// </summary>
/// <remarks>
/// A model is several gigabytes over a content network, and one plain stream handles that badly
/// on two counts. It is slow — a single connection rarely gets more than a fraction of the line,
/// because the far end paces each one — and it is fragile, since any blip loses the lot. So the
/// file is split into ranges fetched in parallel, each range remembers where it got to, and a
/// range that fails is retried from that point rather than from the beginning.
/// <para>
/// Progress is kept in a small file beside the partial download, so an interrupted or cancelled
/// fetch resumes where it stopped even after the app has been closed and reopened.
/// </para>
/// </remarks>
public static class Fetch
{
    /// <summary>How many ranges are pulled at once.</summary>
    /// <remarks>
    /// Six is where the gain flattens on a home line: past that the connections mostly compete
    /// with each other, and content networks start refusing them.
    /// </remarks>
    private const int Streams = 6;

    /// <summary>No range is worth opening a connection for below this.</summary>
    private const long SmallestSegment = 16L * 1024 * 1024;

    /// <summary>A range that has gone this long without a byte is treated as dead and retried.</summary>
    private static readonly TimeSpan Stall = TimeSpan.FromSeconds(30);

    private const int Attempts = 6;

    private static readonly HttpClient Http = new(
        new SocketsHttpHandler
        {
            // One per range, plus room for the probe.
            MaxConnectionsPerServer = Streams + 2,

            // Content networks move clients between edges; a connection pinned for hours ends up
            // on a node that has since become the slow one.
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(20),
        })
    {
        // No overall timeout: the whole point is a transfer measured in minutes. Stalls are
        // caught per read instead, which is what actually goes wrong.
        Timeout = Timeout.InfiniteTimeSpan,
    };

    static Fetch() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("RedBloom/1.0");

    /// <summary>One range of the file and how far into it we have got.</summary>
    private sealed class Segment
    {
        public long Start { get; set; }

        public long Position { get; set; }

        public long End { get; set; }

        public long Remaining => End - Position + 1;

        public bool Done => Position > End;
    }

    /// <summary>Fetches a URL to a path. Returns the path, or null when it did not finish.</summary>
    public static async Task<string?> ToFileAsync(
        string url,
        string destination,
        IProgress<(long Done, long Total)> progress,
        long expectedBytes = 0,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        var partial = destination + ".part";
        var ledger = destination + ".resume";

        var (total, ranged) = await ProbeAsync(url, cancellationToken).ConfigureAwait(false);

        if (total <= 0)
        {
            total = expectedBytes;
        }

        // Without a known length there is nothing to divide and nothing to resume from, so this
        // falls back to reading one stream to the end.
        if (total <= 0 || !ranged)
        {
            return await SingleAsync(url, partial, destination, total, progress, cancellationToken)
                .ConfigureAwait(false);
        }

        var segments = Resume(ledger, partial, total) ?? Plan(total);

        try
        {
            using var file = File.OpenHandle(
                partial,
                FileMode.OpenOrCreate,
                FileAccess.Write,
                FileShare.ReadWrite,
                FileOptions.Asynchronous);

            // Sized up front so each range can write straight to its own offset. Asking for the
            // space at open time is only allowed on a file being created, and this one may be a
            // resumed download that already exists.
            RandomAccess.SetLength(file, total);

            using var work = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            var report = Track(segments, total, progress, ledger, work.Token);

            await Task.WhenAll(segments.Select(segment =>
                PullAsync(url, file, segment, work.Token))).ConfigureAwait(false);

            await report.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            // Whatever came down is kept, along with where each range stopped: the next attempt
            // carries on from here instead of starting the gigabytes again.
            Save(ledger, segments, total);

            return null;
        }

        if (segments.Any(segment => !segment.Done))
        {
            Save(ledger, segments, total);

            return null;
        }

        progress.Report((total, total));

        try
        {
            File.Move(partial, destination, overwrite: true);
            Delete(ledger);

            return destination;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Could not finish {destination}: {ex.Message}");

            return null;
        }
    }

    /// <summary>How big the file is, and whether the server will serve pieces of it.</summary>
    private static async Task<(long Total, bool Ranged)> ProbeAsync(
        string url, CancellationToken cancellationToken)
    {
        try
        {
            // Asked for with a one-byte range rather than a HEAD: the answer proves ranges work
            // instead of merely claiming to, and some mirrors answer HEAD with no length at all.
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(0, 0);

            using var response = await Http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.PartialContent
                && response.Content.Headers.ContentRange is { Length: { } length })
            {
                return (length, true);
            }

            return (response.Content.Headers.ContentLength ?? 0, false);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return (0, false);
        }
    }

    /// <summary>Divides the file into ranges, one per stream, none of them trivially small.</summary>
    private static List<Segment> Plan(long total)
    {
        var count = (int)Math.Clamp(total / SmallestSegment, 1, Streams);
        var each = total / count;
        var segments = new List<Segment>(count);

        for (var i = 0; i < count; i++)
        {
            var start = i * each;
            var end = i == count - 1 ? total - 1 : start + each - 1;

            segments.Add(new Segment { Start = start, Position = start, End = end });
        }

        return segments;
    }

    /// <summary>Where a previous attempt stopped, if it left a usable trail.</summary>
    private static List<Segment>? Resume(string ledger, string partial, long total)
    {
        try
        {
            if (!File.Exists(ledger) || !File.Exists(partial))
            {
                return null;
            }

            var saved = JsonSerializer.Deserialize<Ledger>(File.ReadAllText(ledger));

            // A different length means a different file behind the same name; starting over is
            // the only safe reading of that.
            if (saved is null || saved.Total != total || saved.Segments.Count == 0)
            {
                return null;
            }

            return [.. saved.Segments];
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    private sealed record Ledger(long Total, List<Segment> Segments);

    private static void Save(string ledger, List<Segment> segments, long total)
    {
        try
        {
            File.WriteAllText(ledger, JsonSerializer.Serialize(new Ledger(total, segments)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing the trail costs a restart of the download, not correctness.
        }
    }

    /// <summary>
    /// Pulls one range, retrying from wherever it stopped.
    /// </summary>
    /// <remarks>
    /// Written straight into the file at its own offset, so the ranges never meet in memory and
    /// a partial file is always exactly as complete as the ledger says.
    /// </remarks>
    private static async Task PullAsync(
        string url, SafeFileHandle file, Segment segment, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            if (segment.Done)
            {
                return;
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Range = new RangeHeaderValue(segment.Position, segment.End);

                using var response = await Http
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                response.EnsureSuccessStatusCode();

                await using var source = await response.Content
                    .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

                var buffer = new byte[1 << 20];

                while (!segment.Done)
                {
                    // A dead connection usually goes quiet rather than closing, so each read is
                    // given a deadline of its own.
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    deadline.CancelAfter(Stall);

                    var wanted = (int)Math.Min(buffer.Length, segment.Remaining);
                    var read = await source.ReadAsync(buffer.AsMemory(0, wanted), deadline.Token)
                        .ConfigureAwait(false);

                    if (read == 0)
                    {
                        break;
                    }

                    await RandomAccess.WriteAsync(
                        file, buffer.AsMemory(0, read), segment.Position, cancellationToken)
                        .ConfigureAwait(false);

                    segment.Position += read;
                }

                if (segment.Done)
                {
                    return;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
            {
                // A stall or a dropped connection. Wait a moment and pick up where it stopped.
                System.Diagnostics.Debug.WriteLine($"Range {segment.Start} retrying: {ex.Message}");
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Min(8, 1 << attempt)), cancellationToken)
                .ConfigureAwait(false);
        }

        if (!segment.Done)
        {
            throw new IOException($"The range starting at {segment.Start} could not be finished.");
        }
    }

    /// <summary>Reports the total across all ranges, and writes the trail as it goes.</summary>
    private static async Task Track(
        List<Segment> segments,
        long total,
        IProgress<(long Done, long Total)> progress,
        string ledger,
        CancellationToken cancellationToken)
    {
        var sinceSaved = 0;

        try
        {
            while (!cancellationToken.IsCancellationRequested && segments.Any(s => !s.Done))
            {
                progress.Report((segments.Sum(s => s.Position - s.Start), total));

                if (++sinceSaved >= 20)
                {
                    sinceSaved = 0;
                    Save(ledger, segments, total);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The transfer ended; there is nothing more to report.
        }
    }

    /// <summary>The plain one-stream path, for servers that will not serve ranges.</summary>
    private static async Task<string?> SingleAsync(
        string url,
        string partial,
        string destination,
        long total,
        IProgress<(long Done, long Total)> progress,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Http
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            total = response.Content.Headers.ContentLength ?? total;

            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var sink = File.Create(partial))
            {
                var buffer = new byte[1 << 20];
                long done = 0;
                int read;

                while (true)
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    deadline.CancelAfter(Stall);

                    read = await source.ReadAsync(buffer, deadline.Token).ConfigureAwait(false);

                    if (read == 0)
                    {
                        break;
                    }

                    await sink.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    done += read;
                    progress.Report((done, total));
                }
            }

            File.Move(partial, destination, overwrite: true);

            return destination;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            Delete(partial);

            return null;
        }
    }

    public static void Delete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover working file is untidy, not harmful; the next attempt overwrites it.
        }
    }
}
