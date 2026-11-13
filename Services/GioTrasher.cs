// Services/GioTrasher.cs
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace System_Resource_Manager.Services;

public interface ITrasher
{
    void Trash(string fullPath);
}

public sealed class GioTrasher : ITrasher
{
    public void Trash(string fullPath)
    {
        var psi = new ProcessStartInfo("gio")
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        // ArgumentList passes each argument as-is: no shell, no quoting rules.
        psi.ArgumentList.Add("trash");
        psi.ArgumentList.Add(fullPath);   // always absolute, so it can't be parsed as an option

        try
        {
            using var p = Process.Start(psi) ?? throw new IOException("Could not start gio.");
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0) throw new IOException($"gio trash failed: {err.Trim()}");
        }
        catch (Win32Exception)
        {
            throw new IOException("'gio' was not found on this system.");
        }
    }
}