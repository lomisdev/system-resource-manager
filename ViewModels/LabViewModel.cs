using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System_Resource_Manager.Services;

namespace System_Resource_Manager.ViewModels;

public partial class LabViewModel : ViewModelBase
{
    private readonly WorkerClient _worker = new();
    private CancellationTokenSource? _pollCts;
    private ProcFs.MemStats _baseline;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartWorkerCommand), nameof(StopWorkerCommand),
        nameof(PingCommand), nameof(AllocCommand), nameof(TouchCommand), nameof(FreeCommand))]
    private bool _isRunning;

    [ObservableProperty] private int _workerPid;
    [ObservableProperty] private string _limitMb = "";
    [ObservableProperty] private string _allocMb = "200";
    [ObservableProperty] private string _virtualText = "—";
    [ObservableProperty] private string _residentText = "—";
    [ObservableProperty] private string _faultsText = "—";
    [ObservableProperty] private string _status = "Worker not running";

    public ObservableCollection<string> Log { get; } = new();

    private bool CanStart() => !IsRunning;
    private bool CanSend() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartWorkerAsync()
    {
        try
        {
            ResourceLimits? limits = null;
            if (!string.IsNullOrWhiteSpace(LimitMb))
            {
                if (!int.TryParse(LimitMb, out var mb) || mb < 80)
                    throw new ArgumentException("Limit must be a whole number, at least 80 MB (the runtime itself needs ~40).");
                limits = new ResourceLimits(mb, null);
            }

            Status = "Starting worker…";
            await _worker.StartAsync(limits);

            WorkerPid = int.Parse(Payload(await _worker.SendAsync("PID")));   // the worker says who it is

            // Warm-up: first calls JIT-compile code and load assemblies, which would pollute the numbers.
            foreach (var cmd in new[] { "ALLOC 1", "TOUCH", "FREE" }) await _worker.SendAsync(cmd);

            _baseline = ProcFs.ReadMemStats(WorkerPid);
            IsRunning = true;
            Status = limits is null
                ? $"Worker running (PID {WorkerPid})"
                : $"Worker running (PID {WorkerPid}) under MemoryMax={LimitMb} MB, swap off";
            Add($"worker started, PID {WorkerPid}");

            _pollCts = new CancellationTokenSource();
            _ = PollAsync(_pollCts.Token);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or FormatException)
        {
            _worker.Stop();
            Status = ex.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private void StopWorker()
    {
        _pollCts?.Cancel();
        _worker.Stop();
        Add("worker stopped");
        MarkStopped("Worker stopped");
    }

    [RelayCommand(CanExecute = nameof(CanSend))] private Task PingAsync() => SendAsync("PING");
    [RelayCommand(CanExecute = nameof(CanSend))] private Task AllocAsync() => SendAsync($"ALLOC {AllocMb.Trim()}");
    [RelayCommand(CanExecute = nameof(CanSend))] private Task TouchAsync() => SendAsync("TOUCH");
    [RelayCommand(CanExecute = nameof(CanSend))] private Task FreeAsync() => SendAsync("FREE");

    private async Task SendAsync(string command)
    {
        Add($"> {command}");
        try { Add($"< {await _worker.SendAsync(command)}"); }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            Add($"! {ex.Message}");
            if (!_worker.IsRunning) OnWorkerGone();       // e.g. killed in the middle of the command
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        var pid = WorkerPid;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (!_worker.IsRunning) { OnWorkerGone(); return; }

                var m = await Task.Run(() => ProcFs.ReadMemStats(pid), ct);   // /proc reads off the UI thread
                VirtualText = Delta(m.VmSizeKb, _baseline.VmSizeKb);
                ResidentText = Delta(m.VmRssKb, _baseline.VmRssKb);
                FaultsText = $"+{m.MinorFaults - _baseline.MinorFaults}";
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { OnWorkerGone(); }           // its /proc entry vanished: the process died
    }

    private void OnWorkerGone()
    {
        if (!IsRunning) return;
        _pollCts?.Cancel();

        int? code = _worker.Process is { HasExited: true } p ? p.ExitCode : null;
        _worker.Stop();

        var why = code switch
        {
            137 => "was killed by SIGKILL (exit 137): most likely the kernel OOM killer enforcing the limit",
            null => "stopped",
            _ => $"exited with code {code}"
        };
        Add($"worker {why}");
        MarkStopped($"Worker {why}");
    }

    private void MarkStopped(string status)
    {
        IsRunning = false;
        WorkerPid = 0;
        VirtualText = ResidentText = FaultsText = "—";
        Status = status;
    }

    private void Add(string line)
    {
        Log.Insert(0, $"{DateTime.Now:HH:mm:ss}  {line}");     // newest first, so no scrolling logic needed
        while (Log.Count > 200) Log.RemoveAt(Log.Count - 1);
    }

    private static string Payload(string reply) =>
        reply.StartsWith("OK ") ? reply[3..] : throw new IOException(reply);

    private static string Delta(long nowKb, long baseKb)
    {
        var mb = (nowKb - baseKb) / 1024;
        return $"{(mb >= 0 ? "+" : "")}{mb} MB";
    }

    public void Shutdown()
    {
        _pollCts?.Cancel();
        _worker.Dispose();
    }
}