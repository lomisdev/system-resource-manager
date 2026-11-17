// Services/WorkerClient.cs   (the parent: listens, launches the worker, sends commands)
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;

namespace System_Resource_Manager.Services;

public sealed class WorkerClient : IDisposable
{
    private Socket? _listener, _conn;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private string _socketPath = "";
    private readonly SemaphoreSlim _gate = new(1, 1);      // one request/response at a time

    public Process? Process { get; private set; }
    public bool IsRunning => Process is { HasExited: false };

    public async Task StartAsync(ResourceLimits? limits = null)
    {
        if (IsRunning) return;
        Stop();                                            // clear leftovers from an earlier run

        var dir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? Path.GetTempPath();
        _socketPath = Path.Combine(dir, $"srm-{Guid.NewGuid():N}.sock");

        try
        {
            _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _listener.Bind(new UnixDomainSocketEndPoint(_socketPath));
            _listener.Listen(1);

            Process = Process.Start(WorkerStartInfo(_socketPath, limits))
                      ?? throw new IOException("Could not start the worker process.");

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            _conn = await _listener.AcceptAsync(timeout.Token);

            // The socket FILE is only a rendezvous name. Once connected it is no longer needed,
            // and removing it stops anyone else from connecting to our worker.
            _listener.Dispose(); _listener = null;
            File.Delete(_socketPath);

            var stream = new NetworkStream(_conn, ownsSocket: false);
            _reader = new StreamReader(stream, new UTF8Encoding(false));
            _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or IOException)
        {
            Stop();
            throw new IOException($"Could not start the worker: {ex.Message}");
        }
    }

    public async Task<string> SendAsync(string command)
    {
        // The protocol is one command per line, so a newline inside a command would smuggle in a second one.
        if (command.Contains('\n') || command.Contains('\r'))
            throw new ArgumentException("Commands must be a single line.");
        if (_reader is null || _writer is null || !IsRunning)
            throw new IOException("The worker is not running.");

        await _gate.WaitAsync();
        try
        {
            await _writer.WriteLineAsync(command);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var reply = await _reader.ReadLineAsync(timeout.Token);
            return reply ?? throw new IOException("The worker closed the connection.");
        }
        finally { _gate.Release(); }
    }

    public void Stop()
    {
        try { _writer?.Dispose(); } catch (IOException) { }
        try { _reader?.Dispose(); } catch (IOException) { }
        _writer = null; _reader = null;

        _conn?.Dispose(); _conn = null;               // the worker sees EOF and exits on its own
        _listener?.Dispose(); _listener = null;
        if (_socketPath.Length > 0) File.Delete(_socketPath);   // no-op if already gone

        if (Process is { } p)
        {
            if (!p.WaitForExit(2000)) p.Kill(entireProcessTree: true);
            p.Dispose();
            Process = null;
        }
    }

    public void Dispose() => Stop();

    // Same executable, different role: "--worker <socket>" runs without any UI.
    private static ProcessStartInfo WorkerStartInfo(string socketPath, ResourceLimits? limits)
    {
        var exe = Environment.ProcessPath ?? throw new IOException("Cannot locate our own executable.");

        var args = new List<string>();
        if (Path.GetFileNameWithoutExtension(exe) == "dotnet")      // started as "dotnet App.dll"
            args.Add(Assembly.GetEntryAssembly()!.Location);
        args.Add("--worker");
        args.Add(socketPath);

        var cmd = new List<string> { exe };
        cmd.AddRange(args);
        if (limits is { Any: true })
        {
            var systemdRun = ProcessLauncher.ResolveExecutable("systemd-run", Environment.GetEnvironmentVariable("PATH"));
            cmd = ProcessLauncher.BuildCommand(exe, args, limits, systemdRun);
        }

        var psi = new ProcessStartInfo(cmd[0]) { UseShellExecute = false };
        foreach (var a in cmd.Skip(1)) psi.ArgumentList.Add(a);
        return psi;
    }
}