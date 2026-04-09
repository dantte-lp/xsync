using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Xsync.Logging;

public sealed class LiveTracker
{
    private readonly ConcurrentDictionary<string, FileStatus> _files = new(StringComparer.Ordinal);
    private readonly Stopwatch _globalSw = Stopwatch.StartNew();
    private readonly long _totalBytes;
    private readonly int _totalFiles;
    private readonly string _mode;

    public bool IsPaused { get; set; }

    public LiveTracker(long totalBytes, int totalFiles, string mode)
    {
        _totalBytes = totalBytes;
        _totalFiles = totalFiles;
        _mode = mode;
    }

    public FileStatus Register(string name, long size, string extension)
    {
        var status = new FileStatus { Name = name, Size = size, Extension = extension };
        _files[name] = status;
        return status;
    }

    public FileStatus Get(string name) => _files[name];
    public TimeSpan Elapsed => _globalSw.Elapsed;

    public IRenderable Render()
    {
        var (doneBytes, counts) = ComputeStats();

        var layout = new Grid().AddColumn();
        layout.AddRow(RenderHeader(doneBytes, counts));
        layout.AddRow(RenderProgressBar(doneBytes));
        layout.AddRow(new Text(""));
        layout.AddRow(RenderFileTable());
        layout.AddRow(new Text(""));
        layout.AddRow(RenderControls());

        return layout;
    }

    // ── Header ──────────────────────────────────────────────────────────

    private Markup RenderHeader(long doneBytes, PhaseCounts c)
    {
        var elapsed = _globalSw.Elapsed;
        var avgSpeed = elapsed.TotalSeconds > 0 ? doneBytes / elapsed.TotalSeconds / 1024 / 1024 : 0;
        var remaining = avgSpeed > 0 ? (_totalBytes - doneBytes) / (avgSpeed * 1024 * 1024) : 0;
        var pct = _totalBytes > 0 ? (double)doneBytes / _totalBytes * 100 : 0;
        var completed = c.Done + c.Match + c.DryRun;
        var pauseTag = IsPaused ? " [bold red on yellow] PAUSED [/]" : "";

        var parts = new List<string>
        {
            $"[bold cyan]xsync[/]{pauseTag} [grey]|[/]",
            $"[bold]{_mode}[/] [grey]|[/]",
            $"[bold]{completed}[/]/{_totalFiles} files [grey]|[/]",
            $"[green]{Fmt(doneBytes)}[/]/[bold]{Fmt(_totalBytes)}[/] [grey]({Inv(pct, "F1")}%)[/] [grey]|[/]",
            $"[bold yellow]{Inv(avgSpeed, "F1")} MB/s[/] [grey]|[/]",
            $"ETA [bold]{FmtDur(TimeSpan.FromSeconds(remaining))}[/] [grey]|[/]",
            $"[dim]{FmtDur(elapsed)}[/]",
        };

        if (c.Failed > 0)
        {
            parts.Add($"[grey]|[/] [red bold]{c.Failed} failed[/]");
        }

        return new Markup(string.Join(" ", parts));
    }

    // ── Progress bar ────────────────────────────────────────────────────

    private BreakdownChart RenderProgressBar(long doneBytes)
    {
        var chart = new BreakdownChart().Width(100);

        // Active transfers contribution
        long activeBytes = 0;
        foreach (var f in _files.Values)
        {
            if (f.Phase == FilePhase.Transferring)
            {
                activeBytes += f.BytesTransferred;
            }
        }

        chart.AddItem("Done", Math.Max(1, doneBytes), Color.Green);
        if (activeBytes > 0)
        {
            chart.AddItem("Active", activeBytes, Color.Yellow);
        }

        chart.AddItem("Remaining", Math.Max(0, _totalBytes - doneBytes - activeBytes), Color.Grey);
        return chart;
    }

    // ── File table ──────────────────────────────────────────────────────

    private Table RenderFileTable()
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderStyle(new Style(Color.Grey))
            .Expand()
            .AddColumn(new TableColumn("[grey]#[/]").RightAligned().Width(3).NoWrap())
            .AddColumn(new TableColumn("[bold]File[/]").NoWrap())
            .AddColumn(new TableColumn("[bold]Size[/]").RightAligned().Width(9).NoWrap())
            .AddColumn(new TableColumn("[bold]Progress[/]").Width(22).NoWrap())
            .AddColumn(new TableColumn("[bold]Speed[/]").RightAligned().Width(11).NoWrap())
            .AddColumn(new TableColumn("[bold]Status[/]").Width(8).NoWrap());

        var idx = 0;
        foreach (var f in _files.Values.OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            idx++;
            var typeColor = GetTypeColor(f.Extension);
            var (statusIcon, statusColor) = GetStatusIcon(f);
            var progressBar = RenderCellProgress(f);
            var speedStr = f.Phase is FilePhase.Transferring or FilePhase.Done
                ? $"[yellow]{Inv(f.SpeedMbps, "F1")} MB/s[/]" : "";

            // Smart file name: show extension separately if long
            var displayName = f.Name.Length > 45 ? SmartTrunc(f.Name, 45) : f.Name;

            table.AddRow(
                new Markup($"[grey]{idx}[/]"),
                new Markup($"[{typeColor}]{Markup.Escape(displayName)}[/]"),
                new Markup($"{Fmt(f.Size)}"),
                progressBar,
                new Markup(speedStr),
                new Markup($"[{statusColor}]{statusIcon}[/]"));
        }

        return table;
    }

    private static IRenderable RenderCellProgress(FileStatus f)
    {
        if (f.Phase == FilePhase.Pending)
        {
            return new Markup("[grey]waiting...[/]");
        }

        if (f.Phase == FilePhase.Hashing)
        {
            var pct = (int)f.ProgressPct;
            var bar = new string('\u2588', pct / 5) + new string('\u2591', 20 - pct / 5);
            return new Markup($"[blue]{bar}[/] [blue]{pct}%[/]");
        }

        if (f.Phase == FilePhase.Transferring)
        {
            var pct = (int)f.ProgressPct;
            var bar = new string('\u2588', pct / 5) + new string('\u2591', 20 - pct / 5);
            return new Markup($"[yellow]{bar}[/] [yellow]{pct}%[/]");
        }

        if (f.Phase == FilePhase.Verifying)
        {
            return new Markup("[cyan]verifying hash...[/]");
        }

        if (f.Phase is FilePhase.Done or FilePhase.Match)
        {
            var h = f.Hash is { Length: >= 12 } ? f.Hash[..12] : f.Hash ?? "";
            return new Markup($"[grey]{Markup.Escape(h)}[/]");
        }

        if (f.Phase == FilePhase.DryRun)
        {
            return new Markup("[yellow]planned[/]");
        }

        if (f.Phase == FilePhase.Failed)
        {
            return new Markup($"[red]{Markup.Escape(f.Error ?? "error")}[/]");
        }

        return new Text("");
    }

    private static (string Icon, string Color) GetStatusIcon(FileStatus f) => f.Phase switch
    {
        FilePhase.Pending => ("\u00b7", "grey"),          // ·
        FilePhase.Hashing => ("\u25cb", "blue"),           // ○
        FilePhase.Transferring => ("\u25b2", "yellow"),    // ▲
        FilePhase.Verifying => ("\u2026", "cyan"),         // …
        FilePhase.Done => ("\u2714", "green"),             // ✔
        FilePhase.Match => ("\u2261", "cyan"),             // ≡
        FilePhase.DryRun => ("\u2012", "yellow"),          // ‒
        FilePhase.Failed => ("\u2718", "red"),             // ✘
        _ => ("?", "grey"),
    };

    // ── Controls bar ────────────────────────────────────────────────────

    private Markup RenderControls()
    {
        var pause = IsPaused
            ? "[black on green] R [/] [green]Resume[/]"
            : "[black on yellow] P [/] [yellow]Pause[/]";

        return new Markup(string.Join("  ",
            pause,
            "[black on red] Q [/] [red]Quit[/]",
            "[black on grey] Ctrl+C [/] [grey]Force quit[/]"));
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static string GetTypeColor(string ext) => ext switch
    {
        "qcow2" => "cyan",
        "ova" => "magenta",
        "iso" => "yellow",
        "tar.gz" or "tgz" => "blue",
        "tar" => "blue",
        "exe" or "msi" or "sh" => "red",
        "json" or "csv" => "green",
        _ => "white",
    };

    private static string SmartTrunc(string name, int max)
    {
        // Keep extension visible: "very-long-name...ext"
        var lastDot = name.LastIndexOf('.');
        if (lastDot > 0 && name.Length - lastDot <= 10)
        {
            var ext = name[lastDot..];
            var stem = name[..lastDot];
            var stemMax = max - ext.Length - 2;
            if (stemMax > 5)
            {
                return string.Concat(stem.AsSpan(0, stemMax), "..", ext);
            }
        }

        return string.Concat(name.AsSpan(0, max - 2), "..");
    }

    private (long DoneBytes, PhaseCounts Counts) ComputeStats()
    {
        long doneBytes = 0;
        var c = new PhaseCounts();
        foreach (var f in _files.Values)
        {
            switch (f.Phase)
            {
                case FilePhase.Pending: c.Pending++; break;
                case FilePhase.Hashing: c.Hashing++; break;
                case FilePhase.Transferring: c.Transferring++; break;
                case FilePhase.Verifying: c.Verifying++; break;
                case FilePhase.Done: c.Done++; doneBytes += f.Size; break;
                case FilePhase.Match: c.Match++; doneBytes += f.Size; break;
                case FilePhase.DryRun: c.DryRun++; doneBytes += f.Size; break;
                case FilePhase.Failed: c.Failed++; doneBytes += f.Size; break;
            }
        }

        return (doneBytes, c);
    }

    private static string Fmt(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        foreach (var unit in units)
        {
            if (Math.Abs(size) < 1024)
            {
                return $"{Inv(size, "F1")} {unit}";
            }

            size /= 1024;
        }

        return $"{Inv(size, "F1")} PB";
    }

    private static string Inv(double v, string fmt) =>
        v.ToString(fmt, CultureInfo.InvariantCulture);

    private static string FmtDur(TimeSpan ts) => ts switch
    {
        { TotalSeconds: < 60 } => $"{Inv(ts.TotalSeconds, "F0")}s",
        { TotalMinutes: < 60 } => $"{ts.Minutes}m{ts.Seconds:D2}s",
        _ => $"{ts.Hours}h{ts.Minutes:D2}m",
    };

    private sealed record PhaseCounts
    {
        public int Pending { get; set; }
        public int Hashing { get; set; }
        public int Transferring { get; set; }
        public int Verifying { get; set; }
        public int Done { get; set; }
        public int Match { get; set; }
        public int DryRun { get; set; }
        public int Failed { get; set; }
    }
}
