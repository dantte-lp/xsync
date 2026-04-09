using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Xsync;
using Xsync.Data;
using Xsync.Logging;
using Xsync.Models;
using Xsync.Pipeline;
using Xsync.Services;
using Spectre.Console;

var baseDir = ResolveBaseDir();
var config = ConfigLoader.Load(baseDir, args);
var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
return await RunAsync(config, cts.Token);

static string ResolveBaseDir()
{
    // dotnet run: BaseDirectory is bin/Release/net10.0/
    var candidate = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    if (File.Exists(Path.Combine(candidate, "xsync.toml")))
    {
        return candidate;
    }

    // Published binary next to xsync.toml
    if (File.Exists(Path.Combine(AppContext.BaseDirectory, "xsync.toml")))
    {
        return AppContext.BaseDirectory;
    }

    // CWD has xsync.toml
    if (File.Exists(Path.Combine(Environment.CurrentDirectory, "xsync.toml")))
    {
        return Environment.CurrentDirectory;
    }

    // Fallback: CWD
    return Environment.CurrentDirectory;
}

static async Task<int> RunAsync(SyncConfig config, CancellationToken ct)
{
    var now = DateTime.Now;
    var logFile = Path.Combine(config.LogsDir,
        $"{now:dd-MM-yyyy-HH-mm-ss}{now.Millisecond / 10:D2}.jsonl");
    using var loggerProvider = new SyncLoggerProvider(logFile, config.Verbosity);
    using var loggerFactory = LoggerFactory.Create(b =>
        b.AddProvider(loggerProvider).SetMinimumLevel(LogLevel.Debug));
    var logger = loggerFactory.CreateLogger("Sync");

    var mode = config.DryRun ? "DRY RUN" : config.VerifyOnly ? "VERIFY" : "SYNC";

    // Validate
    if (!File.Exists(config.SshKeyPath))
    {
        AnsiConsole.MarkupLine("[red bold]SSH key not found:[/] {0}", Markup.Escape(config.SshKeyPath));
        return 1;
    }
    if (!Directory.Exists(config.LocalDir))
    {
        AnsiConsole.MarkupLine("[red bold]Local directory not found:[/] {0}", Markup.Escape(config.LocalDir));
        return 1;
    }

    // Database
    await using var db = new MetadataStore(config.DbPath);
    await db.InitializeAsync().ConfigureAwait(false);

    // SSH
    using var remote = new RemoteCommandService(config,
        loggerFactory.CreateLogger<RemoteCommandService>());
    AnsiConsole.Markup("[grey]Connecting SSH...[/] ");
    remote.Connect();
    AnsiConsole.MarkupLine("[green]OK[/]");
    remote.Mkdir(config.RemoteDir);

    // SFTP pool
    await using var sftpPool = new SftpConnectionPool(config,
        loggerFactory.CreateLogger<SftpConnectionPool>());

    if (!config.DryRun && !config.VerifyOnly)
    {
        AnsiConsole.Markup("[grey]Opening {0} SFTP connections...[/] ", config.Parallelism);
        await sftpPool.InitializeAsync(config.Parallelism).ConfigureAwait(false);
        AnsiConsole.MarkupLine("[green]OK[/]");
    }

    // Scan files
    List<FileInfo> allFiles;

    // --file with absolute/relative path outside local_dir
    if (config.FilterFile is not null && (Path.IsPathRooted(config.FilterFile) ||
        config.FilterFile.Contains(Path.DirectorySeparatorChar) ||
        config.FilterFile.Contains(Path.AltDirectorySeparatorChar)))
    {
        var fi = new FileInfo(config.FilterFile);
        allFiles = fi.Exists ? [fi] : [];
    }
    else
    {
        allFiles = Directory.EnumerateFiles(config.LocalDir)
            .Where(f => !SyncConfig.ExcludePatterns.Any(p =>
                Path.GetFileName(f).EndsWith(p, StringComparison.OrdinalIgnoreCase)))
            .Where(f => config.FilterFile is null ||
                Path.GetFileName(f).Equals(config.FilterFile, StringComparison.OrdinalIgnoreCase))
            .Select(f => new FileInfo(f))
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .ToList();
    }

    // Interactive file selection (only in interactive terminal, no --file filter, no --quiet)
    var scanFiles = allFiles;
    var isInteractive = !Console.IsOutputRedirected && !Console.IsErrorRedirected;
    if (isInteractive && config.FilterFile is null && config.Verbosity >= 0 && allFiles.Count > 1)
    {
        var totalAll = allFiles.Sum(f => f.Length);
        AnsiConsole.MarkupLine($"\n[bold]{allFiles.Count}[/] files found ([green]{FmtSize(totalAll)}[/])");
        AnsiConsole.MarkupLine("[grey]Select files to sync (Space to toggle, Enter to confirm, A to select all):[/]\n");

        var choices = allFiles.Select(f => $"{f.Name,-50} {FmtSize(f.Length),10}").ToList();
        var prompt = new MultiSelectionPrompt<string>()
            .PageSize(20)
            .HighlightStyle(new Style(Color.Cyan1))
            .InstructionsText("[grey](Space=toggle  Enter=confirm  Up/Down=navigate)[/]")
            .AddChoices(choices);
        // Pre-select all
        foreach (var c in choices)
        {
            prompt.Select(c);
        }

        var selected = AnsiConsole.Prompt(prompt);
        var selectedNames = selected
            .Select(s => s.TrimEnd().Split("  ", StringSplitOptions.RemoveEmptyEntries)[0].Trim())
            .ToHashSet(StringComparer.Ordinal);
        scanFiles = allFiles.Where(f => selectedNames.Contains(f.Name)).ToList();

        AnsiConsole.MarkupLine($"\n[green]{scanFiles.Count}[/] files selected ([bold]{FmtSize(scanFiles.Sum(f => f.Length))}[/])");
    }

    var totalFiles = scanFiles.Count;
    var totalBytes = scanFiles.Sum(f => f.Length);

    // LiveTracker
    var tracker = new LiveTracker(totalBytes, totalFiles, mode);
    foreach (var fi in scanFiles)
    {
        tracker.Register(fi.Name, fi.Length, SyncConfig.GetExtension(fi.Name));
    }

    var runId = await db.StartSyncRunAsync(totalFiles, totalBytes).ConfigureAwait(false);
    AnsiConsole.WriteLine();

    // Pipeline channels
    var polly = ResiliencePipelines.CreateSftpPipeline();
    var ch1 = Channel.CreateUnbounded<FileEntry>();
    var ch2 = Channel.CreateBounded<HashResult>(16);
    var ch3 = Channel.CreateBounded<TransferResult>(16);

    int transferred = 0, verified = 0, skipped = 0, failed = 0;

    // Pipeline runner + verify logic (shared between live and fallback modes)
    async Task RunPipeline(Action? onRefresh = null)
    {
        var scanTask = FileScannerStage.RunAsync(scanFiles, config, ch1.Writer, logger, ct);
        var hashTask = HashingStage.RunAsync(ch1.Reader, ch2.Writer, db,
            tracker, config, logger, ct);
        var transferTask = TransferStage.RunAsync(ch2.Reader, ch3.Writer, sftpPool, remote, db,
            polly, tracker, config, logger, ct);

        var verifyTask = Task.Run(async () =>
        {
            await foreach (var result in ch3.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                var file = result.Hash.File;
                var status = tracker.Get(file.Name);

                if (config.DryRun && result.Success)
                {
                    status.Phase = FilePhase.DryRun;
                    Interlocked.Increment(ref skipped);
                    continue;
                }

                if (!result.Success)
                {
                    status.Phase = FilePhase.Failed;
                    Interlocked.Increment(ref failed);
                    continue;
                }

                status.Phase = FilePhase.Verifying;
                var verifyPath = result.TempRemotePath ?? file.RemotePath;
                var remoteHash = remote.ComputeRemoteHash(verifyPath);

                if (remoteHash is null ||
                    !result.Hash.ContentHash.AsSpan().SequenceEqual(remoteHash))
                {
                    status.Phase = FilePhase.Failed;
                    status.Error = "Hash mismatch";
                    if (result.TempRemotePath is not null)
                    {
                        remote.Exec($"rm -f '{result.TempRemotePath}'");
                    }
                    await db.UpsertFileAsync(file.Name, file.RemotePath, file.Size,
                        file.MtimeUnix, result.Hash.ContentHash, SyncState.Failed,
                        "hash_mismatch").ConfigureAwait(false);
                    Interlocked.Increment(ref failed);
                    continue;
                }

                if (result.TempRemotePath is not null)
                {
                    remote.Rename(result.TempRemotePath, file.RemotePath);
                    status.Phase = FilePhase.Done;
                    Interlocked.Increment(ref transferred);
                }
                else
                {
                    status.Phase = FilePhase.Match;
                    Interlocked.Increment(ref verified);
                }

                await db.UpsertFileAsync(file.Name, file.RemotePath, file.Size,
                    file.MtimeUnix, result.Hash.ContentHash, SyncState.Done).ConfigureAwait(false);
            }
        }, ct);

        await Task.WhenAll(scanTask, hashTask, transferTask).ConfigureAwait(false);
        await verifyTask.ConfigureAwait(false);
    }

    // Pause support via CancellationTokenSource
    var pauseCts = new CancellationTokenSource();
    var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, pauseCts.Token);

    if (isInteractive)
    {
        await AnsiConsole.Live(tracker.Render())
            .AutoClear(false)
            .Overflow(VerticalOverflow.Ellipsis)
            .StartAsync(async liveCtx =>
            {
                var refreshCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

                // Refresh + keyboard handler
                _ = Task.Run(async () =>
                {
                    while (!refreshCts.Token.IsCancellationRequested)
                    {
                        // Handle keyboard
                        if (Console.KeyAvailable)
                        {
                            var key = Console.ReadKey(intercept: true);
                            switch (key.Key)
                            {
                                case ConsoleKey.P:
                                    if (!tracker.IsPaused)
                                    {
                                        tracker.IsPaused = true;
                                        pauseCts.Cancel();
                                        logger.LogWarning("Paused by user");
                                    }
                                    break;

                                case ConsoleKey.R:
                                    if (tracker.IsPaused)
                                    {
                                        tracker.IsPaused = false;
                                        pauseCts = new CancellationTokenSource();
                                        linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, pauseCts.Token);
                                        logger.LogWarning("Resumed by user");
                                    }
                                    break;

                                case ConsoleKey.Q:
                                    logger.LogWarning("Quit requested by user");
                                    pauseCts.Cancel();
                                    break;
                            }
                        }

                        liveCtx.UpdateTarget(tracker.Render());
                        await Task.Delay(250, refreshCts.Token)
                            .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                    }
                }, refreshCts.Token);

                await RunPipeline().ConfigureAwait(false);
                refreshCts.Cancel();
                liveCtx.UpdateTarget(tracker.Render());
            }).ConfigureAwait(false);
    }
    else
    {
        await RunPipeline().ConfigureAwait(false);
        AnsiConsole.Write(tracker.Render());
    }

    var totalElapsed = tracker.Elapsed;
    var avgSpeed = totalElapsed.TotalSeconds > 0
        ? totalBytes / totalElapsed.TotalSeconds / 1024 / 1024 : 0;
    await db.FinishSyncRunAsync(runId, transferred, skipped + verified, failed, totalBytes, avgSpeed).ConfigureAwait(false);

    // Summary table
    AnsiConsole.WriteLine();
    var summaryTable = new Table()
        .Border(TableBorder.Rounded)
        .Title("[bold]Summary[/]")
        .AddColumn("[bold]Metric[/]")
        .AddColumn(new TableColumn("[bold]Value[/]").RightAligned());

    summaryTable.AddRow("Transferred & verified", $"[green bold]{transferred}[/]");
    summaryTable.AddRow("Already synced (match)", $"[cyan]{verified}[/]");
    summaryTable.AddRow("Skipped (dry-run)", $"[yellow]{skipped}[/]");
    summaryTable.AddRow("Failed", failed > 0 ? $"[red bold]{failed}[/]" : $"[grey]{failed}[/]");
    summaryTable.AddRow("Total time", $"[bold]{FmtDur(totalElapsed)}[/]");
    summaryTable.AddRow("Average speed", $"[bold]{avgSpeed:F1} MB/s[/]");
    summaryTable.AddRow("Log", $"[dim]{Markup.Escape(logFile)}[/]");
    AnsiConsole.Write(summaryTable);

    if (failed > 0)
    {
        AnsiConsole.MarkupLine("\n[red bold]Some files failed.[/]");
        return 1;
    }
    return 0;
}

static string FmtDur(TimeSpan ts) => ts switch
{
    { TotalSeconds: < 60 } => $"{ts.TotalSeconds:F0}s",
    { TotalMinutes: < 60 } => $"{ts.Minutes}m {ts.Seconds}s",
    _ => $"{ts.Hours}h {ts.Minutes}m {ts.Seconds}s",
};

static string FmtSize(long bytes)
{
    string[] units = ["B", "KB", "MB", "GB", "TB"];
    double size = bytes;
    foreach (var unit in units)
    {
        if (Math.Abs(size) < 1024)
        {
            return $"{size:F1} {unit}";
        }

        size /= 1024;
    }

    return $"{size:F1} PB";
}
