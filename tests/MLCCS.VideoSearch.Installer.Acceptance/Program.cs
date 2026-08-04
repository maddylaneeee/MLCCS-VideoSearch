using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.IO.Compression;
using MLCCS.VideoSearch.OnlineInstaller;
using MLCCS.VideoSearch.Core.Updates;

var testRoot = Path.Combine(
    Directory.Exists(@"R:\") ? @"R:\" : Path.GetTempPath(),
    $"MLCCS-Installer-Acceptance-{Guid.NewGuid():N}");
Directory.CreateDirectory(testRoot);

try
{
    var payload = new byte[24 * 1024 * 1024];
    for (var index = 0; index < payload.Length; index++)
    {
        payload[index] = (byte)(index % 251);
    }

    var portProbe = new TcpListener(IPAddress.Loopback, 0);
    portProbe.Start();
    var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
    portProbe.Stop();

    using var serverCancellation = new CancellationTokenSource();
    var server = new RangeTestServer(port, payload);
    var serverTask = server.RunAsync(serverCancellation.Token);
    var url = $"http://127.0.0.1:{port}/payload.bin";
    var output = Path.Combine(testRoot, "payload.bin");

    using var http = InstallerForm.CreateHttpClient();
    using var interrupted = new CancellationTokenSource();
    var firstPause = new PauseController();
    try
    {
        await InstallerForm.DownloadAsync(
            http,
            url,
            output,
            payload.Length,
            firstPause,
            interrupted.Token,
            (downloaded, _) =>
            {
                if (downloaded >= 3 * 1024 * 1024)
                {
                    interrupted.Cancel();
                }
            });
        throw new InvalidOperationException("The interrupted download unexpectedly completed.");
    }
    catch (OperationCanceledException)
    {
        // Expected: the persistent .partial file must remain for the next run.
    }

    var partial = output + ".partial";
    if (!File.Exists(partial) || new FileInfo(partial).Length < 3 * 1024 * 1024)
    {
        throw new InvalidOperationException("Interrupted download did not preserve usable progress.");
    }
    var resumedFrom = new FileInfo(partial).Length;

    var pause = new PauseController();
    var pauseReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var pauseIssued = false;
    var resumedDownload = InstallerForm.DownloadAsync(
        http,
        url,
        output,
        payload.Length,
        pause,
        CancellationToken.None,
        (downloaded, _) =>
        {
            if (!pauseIssued && downloaded >= resumedFrom + 1024 * 1024)
            {
                pauseIssued = true;
                pause.Pause();
                pauseReached.TrySetResult();
            }
        });

    await pauseReached.Task.WaitAsync(TimeSpan.FromSeconds(15));
    var pausedSize = new FileInfo(partial).Length;
    await Task.Delay(750);
    var pausedSizeAfterWait = new FileInfo(partial).Length;
    if (pausedSizeAfterWait != pausedSize)
    {
        throw new InvalidOperationException(
            $"Pause did not stop file growth: {pausedSize} -> {pausedSizeAfterWait}.");
    }
    pause.Resume();
    await resumedDownload;

    var actualHash = await InstallerForm.ComputeSha256Async(
        output,
        new PauseController(),
        CancellationToken.None);
    var expectedHash = Convert.ToHexStringLower(SHA256.HashData(payload));
    if (!actualHash.Equals(expectedHash, StringComparison.Ordinal))
    {
        throw new InvalidOperationException("Resumed download SHA-256 mismatch.");
    }
    if (!server.RangeStarts.Any(start => start > 0))
    {
        throw new InvalidOperationException("No resumed HTTP Range request was observed.");
    }
    if (!server.ForcedDisconnectOccurred)
    {
        throw new InvalidOperationException("The test server did not exercise forced disconnect recovery.");
    }

    var fallbackOutput = Path.Combine(testRoot, "fallback.bin");
    await InstallerForm.DownloadAsync(
        http,
        $"http://127.0.0.1:{port}/no-range.bin",
        fallbackOutput,
        payload.Length,
        new PauseController(),
        CancellationToken.None,
        (_, _) => { });
    var fallbackHash = await InstallerForm.ComputeSha256Async(
        fallbackOutput,
        new PauseController(),
        CancellationToken.None);
    if (!fallbackHash.Equals(expectedHash, StringComparison.Ordinal))
    {
        throw new InvalidOperationException("Non-Range compatibility download SHA-256 mismatch.");
    }

    var unsafeArchive = Path.Combine(testRoot, "unsafe.zip");
    using (var archive = ZipFile.Open(unsafeArchive, ZipArchiveMode.Create))
    {
        var entry = archive.CreateEntry("../escape.txt");
        await using var stream = entry.Open();
        await stream.WriteAsync("unsafe"u8.ToArray());
    }
    try
    {
        await InstallerForm.ExtractArchiveAsync(unsafeArchive, Path.Combine(testRoot, "extract"),
            new PauseController(), CancellationToken.None, new Progress<ItemProgress>());
        throw new InvalidOperationException("Path-traversal archive was accepted.");
    }
    catch (InvalidDataException) { }

    var verifiedRoot = Path.Combine(testRoot, "verified");
    Directory.CreateDirectory(verifiedRoot);
    await File.WriteAllTextAsync(Path.Combine(verifiedRoot, "payload.txt"), "verified");
    try
    {
        await InstallerForm.VerifyFilesAsync(verifiedRoot,
            [new ReleaseFile("payload.txt", 8, new string('0', 64))], new PauseController(),
            CancellationToken.None, new Progress<ItemProgress>());
        throw new InvalidOperationException("Altered extracted file was accepted.");
    }
    catch (InvalidDataException) { }

    serverCancellation.Cancel();
    try
    {
        await serverTask;
    }
    catch (OperationCanceledException)
    {
        // Expected server shutdown.
    }

    Console.WriteLine("PASS persistent-resume");
    Console.WriteLine("PASS adaptive-range-retry");
    Console.WriteLine("PASS pause-resume");
    Console.WriteLine("PASS background-sha256");
    Console.WriteLine("PASS non-range-fallback");
    Console.WriteLine("PASS path-traversal-rejection");
    Console.WriteLine("PASS extracted-file-hash-rejection");
    Console.WriteLine($"RESUMED_FROM={resumedFrom}");
    Console.WriteLine($"SHA256={actualHash}");
}
finally
{
    if (Directory.Exists(testRoot))
    {
        Directory.Delete(testRoot, recursive: true);
    }
}

internal sealed class RangeTestServer(int port, byte[] payload)
{
    private readonly HttpListener _listener = new();
    private int _requestCount;

    internal ConcurrentBag<long> RangeStarts { get; } = [];
    internal bool ForcedDisconnectOccurred { get; private set; }

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var context = await _listener.GetContextAsync().WaitAsync(cancellationToken);
                _ = Task.Run(() => RespondAsync(context, cancellationToken), cancellationToken);
            }
        }
        finally
        {
            _listener.Stop();
        }
    }

    private async Task RespondAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var requestNumber = Interlocked.Increment(ref _requestCount);
        if (context.Request.RawUrl?.Contains("no-range", StringComparison.OrdinalIgnoreCase) == true)
        {
            context.Response.StatusCode = (int)HttpStatusCode.OK;
            context.Response.ContentLength64 = payload.LongLength;
            await context.Response.OutputStream.WriteAsync(payload, cancellationToken);
            context.Response.Close();
            return;
        }
        var (start, end) = ParseRange(context.Request.Headers["Range"], payload.LongLength);
        RangeStarts.Add(start);
        context.Response.StatusCode = (int)HttpStatusCode.PartialContent;
        context.Response.ContentLength64 = end - start + 1;
        context.Response.AddHeader("Accept-Ranges", "bytes");
        context.Response.AddHeader("Content-Range", $"bytes {start}-{end}/{payload.LongLength}");

        var position = start;
        while (position <= end)
        {
            var count = (int)Math.Min(64 * 1024, end - position + 1);
            await context.Response.OutputStream.WriteAsync(
                payload.AsMemory((int)position, count),
                cancellationToken);
            position += count;
            await Task.Delay(3, cancellationToken);
            if (requestNumber == 1 && position - start >= 512 * 1024)
            {
                ForcedDisconnectOccurred = true;
                context.Response.Abort();
                return;
            }
        }
        context.Response.Close();
    }

    private static (long Start, long End) ParseRange(string? value, long length)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            return (0, length - 1);
        }
        var parts = value[6..].Split('-', 2);
        var start = long.Parse(parts[0]);
        var end = string.IsNullOrWhiteSpace(parts[1]) ? length - 1 : long.Parse(parts[1]);
        return (start, Math.Min(end, length - 1));
    }
}
