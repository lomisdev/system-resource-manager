// Services/WorkerHost.cs   (runs INSIDE the worker process)
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace System_Resource_Manager.Services;

public sealed class WorkerHost
{
    // Raw syscalls, so the demo shows what malloc does underneath. Constants are the Linux values.
    [DllImport("libc.so.6", SetLastError = true)]
    private static extern nint mmap(nint addr, nuint length, int prot, int flags, int fd, nint offset);

    [DllImport("libc.so.6", SetLastError = true)]
    private static extern int munmap(nint addr, nuint length);

    private const int PROT_READ = 1, PROT_WRITE = 2;
    private const int MAP_PRIVATE = 0x02, MAP_ANONYMOUS = 0x20;
    private const long MB = 1024 * 1024;

    private sealed class Block { public nint Ptr; public long Bytes; public bool Touched; }
    private readonly List<Block> _blocks = new();

    public bool ShouldExit { get; private set; }

    public string Handle(string line)
    {
        var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return "ERR empty command";
        var arg = parts.Length > 1 ? parts[1] : "";

        switch (parts[0].ToUpperInvariant())
        {
            case "PING": return "OK pong";
            case "ECHO": return $"OK {arg}";
            case "ALLOC": return Alloc(arg);
            case "TOUCH": return Touch();
            case "FREE": return Free();
            case "PID": return $"OK {Environment.ProcessId}";
            case "QUIT": ShouldExit = true; return "OK bye";
            default: return $"ERR unknown command '{parts[0]}'";
        }
    }

    private string Alloc(string arg)
    {
        if (!long.TryParse(arg, out var mb) || mb < 1)
            return "ERR usage: ALLOC <megabytes>";

        // Safety: TOUCH makes memory real. Never let a typo take the machine down.
        long limit = SafeLimitMb();
        long total = _blocks.Sum(b => b.Bytes) / MB;
        if (total + mb > limit)
            return $"ERR refused: {total}+{mb} MB would exceed the safety cap of {limit} MB (half of available RAM)";

        var ptr = mmap(0, (nuint)(mb * MB), PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
        if (ptr == -1) return $"ERR mmap failed (errno {Marshal.GetLastWin32Error()})";

        _blocks.Add(new Block { Ptr = ptr, Bytes = mb * MB });
        return $"OK allocated {mb} MB (virtual only: no pages are resident until touched)";
    }

    private string Touch()
    {
        long step = Environment.SystemPageSize, pages = 0;
        foreach (var b in _blocks.Where(b => !b.Touched))
        {
            for (long off = 0; off < b.Bytes; off += step)
                Marshal.WriteByte(b.Ptr + (nint)off, 1);     // first write to a page = a page fault
            b.Touched = true;
            pages += b.Bytes / step;
        }
        return $"OK touched {pages} pages ({pages * step / MB} MB now resident)";
    }

    private string Free()
    {
        long mb = _blocks.Sum(b => b.Bytes) / MB;
        foreach (var b in _blocks) munmap(b.Ptr, (nuint)b.Bytes);
        _blocks.Clear();
        return $"OK freed {mb} MB";
    }

    private static long SafeLimitMb()
    {
        foreach (var line in File.ReadLines("/proc/meminfo"))
            if (line.StartsWith("MemAvailable:"))
                return long.Parse(line.Split(':')[1].Trim().Split(' ')[0]) / 1024 / 2;
        return 256;
    }

    // Entry point of the worker process.
    public static int Run(string socketPath)
    {
        using var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        sock.Connect(new UnixDomainSocketEndPoint(socketPath));

        using var stream = new NetworkStream(sock);
        using var reader = new StreamReader(stream, new UTF8Encoding(false));
        using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

        var host = new WorkerHost();
        string? line;
        while ((line = reader.ReadLine()) is not null)    // null = EOF = the peer closed: our cue to exit
        {
            string reply;
            try { reply = host.Handle(line); }
            catch (Exception ex) { reply = $"ERR {ex.GetType().Name}: {ex.Message}"; }

            writer.WriteLine(reply);
            if (host.ShouldExit) break;
        }
        return 0;
    }
}