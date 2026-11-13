// ViewModels/MainViewModel.cs
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System_Resource_Manager.Models;
using System_Resource_Manager.Services;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace System_Resource_Manager.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    // --- navigation ---
    public enum AppPage { Dashboard, Processes, Files }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDashboardVisible), nameof(IsProcessesVisible), nameof(IsFilesVisible))]
    private AppPage _currentPage = AppPage.Dashboard;

    public bool IsDashboardVisible => CurrentPage == AppPage.Dashboard;
    public bool IsProcessesVisible => CurrentPage == AppPage.Processes;
    public bool IsFilesVisible => CurrentPage == AppPage.Files;

    public FilesViewModel Files { get; } = new();

    [RelayCommand] private void ShowDashboard() => CurrentPage = AppPage.Dashboard;
    [RelayCommand] private void ShowProcesses() => CurrentPage = AppPage.Processes;
    [RelayCommand] private void ShowFiles() => CurrentPage = AppPage.Files;

    // --- live stats ---
    [ObservableProperty] private double _cpuPercent;
    [ObservableProperty] private double _memoryPercent;
    [ObservableProperty] private string _memorySummary = "—";
    [ObservableProperty] private double _swapPercent;
    [ObservableProperty] private string _swapSummary = "—";
    [ObservableProperty] private string _uptime = "—";
    [ObservableProperty] private string _loadAverage = "—";
    [ObservableProperty] private int _processCount;

    // --- process page ---
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _status = "Select a process";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TerminateCommand), nameof(ForceKillCommand))]
    private ProcessItem? _selectedProcess;

    [ObservableProperty] private string _pidSortIndicator = "";
    [ObservableProperty] private string _cpuSortIndicator = " ↓";
    [ObservableProperty] private string _memorySortIndicator = "";

    [ObservableProperty]
    private string _processListSummary = "Processes";
    public ObservableCollection<ProcessItem> Processes { get; } = new();
    public ObservableCollection<ProcessItem> TopProcesses { get; } = new();

    private readonly Dictionary<int, ProcessItem> _all = new();
    private SystemSnapshot? _prev;
    private readonly CancellationTokenSource _cts = new();

    private enum ProcessSortColumn
    {
        Pid,
        Cpu,
        Memory
    }

    private ProcessSortColumn _sortColumn = ProcessSortColumn.Cpu;
    private bool _sortDescending = true;

    [RelayCommand]
    private void SortByPid() => SetSort(ProcessSortColumn.Pid);

    [RelayCommand]
    private void SortByCpu() => SetSort(ProcessSortColumn.Cpu);

    [RelayCommand]
    private void SortByMemory() => SetSort(ProcessSortColumn.Memory);

    private void SetSort(ProcessSortColumn column)
    {
        if (_sortColumn == column)
        {
            _sortDescending = !_sortDescending;
        }
        else
        {
            _sortColumn = column;
            _sortDescending = true;
        }

        PidSortIndicator = "";
        CpuSortIndicator = "";
        MemorySortIndicator = "";

        string indicator = _sortDescending ? " ↓" : " ↑";

        switch (_sortColumn)
        {
            case ProcessSortColumn.Pid:
                PidSortIndicator = indicator;
                break;

            case ProcessSortColumn.Cpu:
                CpuSortIndicator = indicator;
                break;

            case ProcessSortColumn.Memory:
                MemorySortIndicator = indicator;
                break;
        }

        ApplyProcessView();
    }

    public MainViewModel()
    {
        CpuSeries =
    [
        new LineSeries<double>
        {
            Name = "CPU",
            Values = CpuHistory,
            Fill = null,
            GeometrySize = 0,
            LineSmoothness = 0.3
        }
    ];

        MemorySeries =
        [
            new LineSeries<double>
        {
            Name = "Memory",
            Values = MemoryHistory,
            Fill = null,
            GeometrySize = 0,
            LineSmoothness = 0.3
        }
        ];

        if (Design.IsDesignMode) return;

        _ = MonitorAsync(_cts.Token);
    }

    public void Stop() => _cts.Cancel();

    private async Task MonitorAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            do
            {
                try
                {
                    var snap = await Task.Run(() => ProcFs.ReadSnapshot(), ct); // file I/O off the UI thread
                    Apply(snap);                                                // back on the UI thread
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Status = $"Monitor error: {ex.Message}";
                }
            } while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) { }
    }

    private void Apply(SystemSnapshot s)
    {
        // CPU% = share of NON-idle ticks between two samples. One sample alone means nothing.
        double totalDelta = _prev is null ? 0 : s.Cpu.Total - _prev.Cpu.Total;
        if (_prev is not null && totalDelta > 0)
        {
            double idleDelta = s.Cpu.Idle - _prev.Cpu.Idle;

            CpuPercent = Math.Round(
                (1 - idleDelta / totalDelta) * 100, 1);

            AddHistorySample(CpuHistory, CpuPercent);
        }

        long used = s.MemTotalKb - s.MemAvailableKb;
        MemoryPercent = s.MemTotalKb > 0 ? Math.Round(used * 100.0 / s.MemTotalKb, 1) : 0;
        AddHistorySample(MemoryHistory, MemoryPercent);
        MemorySummary = $"{used / 1048576.0:F1} / {s.MemTotalKb / 1048576.0:F1} GB";

        long swapUsed = s.SwapTotalKb - s.SwapFreeKb;
        SwapPercent = s.SwapTotalKb > 0 ? Math.Round(swapUsed * 100.0 / s.SwapTotalKb, 1) : 0;
        SwapSummary = $"{swapUsed / 1048576.0:F1} / {s.SwapTotalKb / 1048576.0:F1} GB";

        var up = TimeSpan.FromSeconds(s.UptimeSeconds);
        Uptime = $"{(int)up.TotalDays}d {up.Hours}h {up.Minutes}m";
        LoadAverage = s.LoadAvg;
        ProcessCount = s.Processes.Count;

        // Update existing ProcessItems IN PLACE so selection and scroll survive refreshes.
        foreach (var (pid, raw) in s.Processes)
        {
            if (!_all.TryGetValue(pid, out var item))
                _all[pid] = item = new ProcessItem { ProcessId = pid };

            double cpu = 0;
            if (totalDelta > 0 && _prev!.Processes.TryGetValue(pid, out var old))
                cpu = (raw.Ticks - old.Ticks) / totalDelta * 100;

            item.Name = raw.Name;
            item.CpuPercent = Math.Round(cpu, 1);
            item.MemoryMb = Math.Round(raw.RssMb, 1);
        }
        foreach (var dead in _all.Keys.Where(k => !s.Processes.ContainsKey(k)).ToList())
            _all.Remove(dead);

        TopProcesses.Clear();
        foreach (var p in _all.Values.OrderByDescending(p => p.CpuPercent).Take(5))
            TopProcesses.Add(p);

        ApplyProcessView();
        _prev = s;
    }

    // --- chart history ---
    private const int HistoryLimit = 60;

    public ObservableCollection<double> CpuHistory { get; } = new();
    public ObservableCollection<double> MemoryHistory { get; } = new();

    public ISeries[] CpuSeries { get; }
    public ISeries[] MemorySeries { get; }

    public Axis[] MemoryYAxes { get; } =
[
    new Axis
    {
        MinLimit = 0,
        MaxLimit = 100
    }
];

    public Axis[] MemoryXAxes { get; } =
    [
        new Axis
    {
        Name = "Time",
        MinLimit = 0,
        MaxLimit = 59,
        MinStep = 10,
        ForceStepToMin = true,
        Labeler = value => $"{value - 59:0}s"
    }
    ];

    public Axis[] CpuYAxes { get; } =
    [
        new Axis
    {
        MinLimit = 0,
        MaxLimit = 100
    }
    ];

    public Axis[] CpuXAxes { get; } =
[
  new Axis
    {
        Name = "Time",
        MinLimit = 0,
        MaxLimit = 59,
        MinStep = 10,
        ForceStepToMin = true,
        Labeler = value => $"{value - 59:0}s"
    }
];

    private static void AddHistorySample(
    ObservableCollection<double> history,
    double value)
    {
        history.Add(value);

        if (history.Count > HistoryLimit)
            history.RemoveAt(0);
    }

    partial void OnSearchTextChanged(string value) => ApplyProcessView();

    // Sync the visible collection to "filtered + sorted" with minimal Insert/Remove/Move calls.
    private void ApplyProcessView()
    {
        // 1. Filter processes using the search text.
        var filtered = _all.Values
            .Where(p => string.IsNullOrWhiteSpace(SearchText)
                     || p.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
                     || p.ProcessId.ToString().Contains(SearchText))
            .ToList();

        // 2. Count all matching processes before applying the display limit.
        int matchingCount = filtered.Count;

        // 3. Sort according to the selected column and direction.
        IOrderedEnumerable<ProcessItem> sorted = _sortColumn switch
        {
            ProcessSortColumn.Pid => _sortDescending
                ? filtered.OrderByDescending(p => p.ProcessId)
                : filtered.OrderBy(p => p.ProcessId),

            ProcessSortColumn.Cpu => _sortDescending
                ? filtered.OrderByDescending(p => p.CpuPercent)
                : filtered.OrderBy(p => p.CpuPercent),

            ProcessSortColumn.Memory => _sortDescending
                ? filtered.OrderByDescending(p => p.MemoryMb)
                : filtered.OrderBy(p => p.MemoryMb),

            _ => filtered.OrderByDescending(p => p.CpuPercent)
        };

        // 4. Limit the number of displayed processes to 200.
        var visible = sorted.Take(200).ToList();

        // 5. Update the summary.
        ProcessListSummary = $"Processes ({visible.Count} of {matchingCount})";

        // 6. Synchronize the visible ObservableCollection.
        var keep = visible.ToHashSet();

        for (int i = Processes.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(Processes[i]))
                Processes.RemoveAt(i);
        }

        for (int i = 0; i < visible.Count; i++)
        {
            int idx = Processes.IndexOf(visible[i]);

            if (idx < 0)
                Processes.Insert(i, visible[i]);
            else if (idx != i)
                Processes.Move(idx, i);
        }
    }

    private bool CanSignal() => SelectedProcess is not null;

    [RelayCommand(CanExecute = nameof(CanSignal))]
    private void Terminate() => Send(Signals.SIGTERM, "SIGTERM");

    [RelayCommand(CanExecute = nameof(CanSignal))]
    private void ForceKill() => Send(Signals.SIGKILL, "SIGKILL");

    [ObservableProperty] private string _launchText = "";

    [RelayCommand]
    private void Launch()
    {
        if (string.IsNullOrWhiteSpace(LaunchText)) return;

        Process proc;
        string name;
        try { (proc, name) = ProcessLauncher.Launch(LaunchText); }
        catch (Exception ex) when (ex is ArgumentException or IOException or Win32Exception)
        {
            Status = ex.Message;
            return;
        }

        var pid = proc.Id;
        Status = $"Started {name} (PID {pid})";
        LaunchText = "";
        _ = WatchAsync(proc, name, pid);     // deliberately not awaited
    }

    private async Task WatchAsync(Process proc, string name, int pid)
    {
        await proc.WaitForExitAsync();       // resumes on the UI thread, like your monitor loop
        if (proc.ExitCode != 0)
            Status = $"{name} (PID {pid}) exited with code {proc.ExitCode}";
        proc.Dispose();
    }

    private void Send(int signal, string label)
    {
        var p = SelectedProcess;
        if (p is null) return;
        if (p.ProcessId <= 1 || p.ProcessId == Environment.ProcessId)
        {
            Status = "Refusing: that's init or this app.";
            return;
        }
        Status = Signals.TrySend(p.ProcessId, signal, out var err)
            ? $"Sent {label} to {p.Name} ({p.ProcessId})"
            : $"Failed on {p.Name} ({p.ProcessId}): {err}";
    }
}