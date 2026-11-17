// Services/LabDemo.cs
using System;
using System.Threading.Tasks;

namespace System_Resource_Manager.Services;

public static class LabDemo
{
    public static async Task<int> RunAsync()
    {
        using var worker = new WorkerClient();
        await worker.StartAsync();
        var pid = worker.Process!.Id;
        var baseline = ProcFs.ReadMemStats(pid);
        Console.WriteLine($"worker PID {pid}; baseline VmSize {baseline.VmSizeKb / 1024} MB, RSS {baseline.VmRssKb / 1024} MB\n");

        foreach (var cmd in new[] { "ECHO hello from the parent", "ALLOC 200", "TOUCH", "FREE" })
        {
            Console.WriteLine($"> {cmd}");
            Console.WriteLine($"< {await worker.SendAsync(cmd)}");
            var m = ProcFs.ReadMemStats(pid);
            Console.WriteLine($"  VmSize {Delta(m.VmSizeKb, baseline.VmSizeKb)} | VmRSS {Delta(m.VmRssKb, baseline.VmRssKb)}" +
                              $" | minor faults +{m.MinorFaults - baseline.MinorFaults}\n");
        }

        Console.WriteLine($"Inspect the channel from another terminal, then press Enter:\n" +
                          $"  ls -l /proc/{pid}/fd\n  ls -l /proc/{Environment.ProcessId}/fd\n" +
                          $"  ss -x -p | grep -E '{pid}|{Environment.ProcessId}'");
        Console.ReadLine();
        return 0;
    }

    private static string Delta(long nowKb, long baseKb)
    {
        var mb = (nowKb - baseKb) / 1024;
        return $"{(mb >= 0 ? "+" : "")}{mb} MB";
    }
}