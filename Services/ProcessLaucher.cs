// Services/ProcessLauncher.cs
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace System_Resource_Manager.Services;

public static class ProcessLauncher
{
    public static (Process Process, string Program) Launch(string commandLine)
    {
        var parts = CommandLine.Split(commandLine);
        if (parts.Count == 0) throw new ArgumentException("Type a command first.");

        var exe = ResolveExecutable(parts[0], Environment.GetEnvironmentVariable("PATH"));

        // setsid: run the program in a new session, so terminal signals aimed at us don't reach it.
        var psi = new ProcessStartInfo("setsid")
        {
            UseShellExecute = false,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        psi.ArgumentList.Add(exe);
        foreach (var arg in parts.Skip(1)) psi.ArgumentList.Add(arg);

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