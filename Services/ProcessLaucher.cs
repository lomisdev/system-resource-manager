// Services/ProcessLauncher.cs
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace System_Resource_Manager.Services;

public sealed record ResourceLimits(int? MemoryMb, int? CpuPercent)
{
    public bool Any => MemoryMb is not null || CpuPercent is not null;
}

public static class ProcessLauncher
{
    public static (Process Process, string Program) Launch(string commandLine, ResourceLimits? limits = null)
    {
        var parts = CommandLine.Split(commandLine);
        if (parts.Count == 0) throw new ArgumentException("Type a command first.");

        var path = Environment.GetEnvironmentVariable("PATH");
        var exe = ResolveExecutable(parts[0], path);

        var cmd = new List<string> { exe };
        cmd.AddRange(parts.Skip(1));
        if (limits is { Any: true })
            cmd = BuildCommand(exe, parts.Skip(1), limits, ResolveExecutable("systemd-run", path));

        // setsid: new session, so terminal signals aimed at us don't reach the program.
        var psi = new ProcessStartInfo("setsid")
        {
            UseShellExecute = false,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        foreach (var a in cmd) psi.ArgumentList.Add(a);

        try
        {
            var p = Process.Start(psi) ?? throw new IOException("Could not start the process.");
            return (p, Path.GetFileName(exe));
        }
        catch (Win32Exception)
        {
            throw new IOException("'setsid' was not found on this system.");
        }
    }

    // Pure function: easy to test, and the one place that decides what reaches systemd-run.
    public static List<string> BuildCommand(
        string exe, IEnumerable<string> programArgs, ResourceLimits limits, string systemdRun)
    {
        if (limits.MemoryMb is < 1 || limits.CpuPercent is < 1)
            throw new ArgumentException("Limits must be positive whole numbers.");

        var cmd = new List<string> { systemdRun, "--user", "--scope", "--quiet" };
        if (limits.MemoryMb is int mb)
        {
            cmd.AddRange(new[] { "-p", $"MemoryMax={mb}M" });
            cmd.AddRange(new[] { "-p", "MemorySwapMax=0" });   // else zram swap absorbs the overage and nothing is killed
        }
        if (limits.CpuPercent is int cpu) cmd.AddRange(new[] { "-p", $"CPUQuota={cpu}%" });

        cmd.Add("--");       // everything after this is the program, never systemd-run options
        cmd.Add(exe);
        cmd.AddRange(programArgs);
        return cmd;
    }

    public static string ResolveExecutable(string program, string? pathVar)
    {
        if (program.Contains('/'))
        {
            if (!Path.IsPathRooted(program))
                throw new ArgumentException("Use an absolute path, or just the program name.");
            return IsExecutable(program) ? program
                : throw new FileNotFoundException($"Not an executable file: {program}");
        }

        foreach (var dir in (pathVar ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, program);
            if (IsExecutable(candidate)) return candidate;
        }
        throw new FileNotFoundException($"'{program}' not found in PATH.");
    }

    private static bool IsExecutable(string path) =>
        File.Exists(path) &&
        (File.GetUnixFileMode(path) &
            (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
}