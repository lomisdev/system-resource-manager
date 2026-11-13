// Services/Signals.cs
using System.Runtime.InteropServices;

namespace System_Resource_Manager.Services;

public static class Signals
{
    public const int SIGTERM = 15;   // "please exit" (process can clean up)
    public const int SIGKILL = 9;    // kernel removes it, no cleanup, can't be caught

    [DllImport("libc.so.6", SetLastError = true)]
    private static extern int kill(int pid, int sig);

    public static bool TrySend(int pid, int signal, out string error)
    {
        if (kill(pid, signal) == 0) { error = ""; return true; }
        error = Marshal.GetLastWin32Error() switch   // errno
        {
            1 => "permission denied (not your process; needs root)",
            3 => "no such process (already gone)",
            var e => $"errno {e}"
        };
        return false;
    }
}