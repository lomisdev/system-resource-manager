// Services/ProcFs.cs
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System_Resource_Manager.Models;

namespace System_Resource_Manager.Services;

public static class ProcFs
{
    private static readonly double PageMb = Environment.SystemPageSize / 1048576.0;

    public static SystemSnapshot ReadSnapshot()
    {
        var mem = ReadMemInfo();
        return new SystemSnapshot(
            ReadCpuTimes(),
            mem.GetValueOrDefault("MemTotal"), mem.GetValueOrDefault("MemAvailable"),
            mem.GetValueOrDefault("SwapTotal"), mem.GetValueOrDefault("SwapFree"),
            ReadUptime(),
            string.Join(" ", File.ReadAllText("/proc/loadavg").Split(' ').Take(3)),
            ReadProcesses());
    }

    // "cpu  user nice system idle iowait irq softirq steal ..." (cumulative ticks since boot)
    private static CpuTimes ReadCpuTimes()
    {
        var f = File.ReadLines("/proc/stat").First()
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        long total = 0;
        for (int i = 1; i <= 8; i++) total += long.Parse(f[i]);
        long idle = long.Parse(f[4]) + long.Parse(f[5]);   // idle + iowait
        return new CpuTimes(total, idle);
    }

    private static Dictionary<string, long> ReadMemInfo()
    {
        var d = new Dictionary<string, long>();
        foreach (var line in File.ReadLines("/proc/meminfo"))     // "MemTotal:  16384 kB"
        {
            int i = line.IndexOf(':');
            if (i < 0) continue;
            var num = line[(i + 1)..].Trim().Split(' ')[0];
            if (long.TryParse(num, out var v)) d[line[..i]] = v;
        }
        return d;
    }

    private static double ReadUptime() =>
        double.Parse(File.ReadAllText("/proc/uptime").Split(' ')[0], CultureInfo.InvariantCulture);

    private static Dictionary<int, RawProcess> ReadProcesses()
    {
        var result = new Dictionary<int, RawProcess>();
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid)) continue;   // skip /proc/meminfo-style entries
            try
            {
                var stat = File.ReadAllText($"{dir}/stat");
                // Format: pid (comm) state ppid ... — comm can contain spaces AND ')' ,
                // so split on the LAST ')' rather than on spaces.
                int open = stat.IndexOf('('), close = stat.LastIndexOf(')');
                var name = stat[(open + 1)..close];
                var f = stat[(close + 2)..].Split(' ');          // f[0] is field 3 (state)
                long ticks = long.Parse(f[11]) + long.Parse(f[12]);   // utime + stime (fields 14, 15)

                var statm = File.ReadAllText($"{dir}/statm").Split(' ');
                result[pid] = new RawProcess(name, ticks, long.Parse(statm[1]) * PageMb);  // field 2 = resident pages
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                          or FormatException or IndexOutOfRangeException
                                          or ArgumentOutOfRangeException)
            {
                // Process exited between listing and reading. Normal on a live system.
            }
        }
        return result;
    }
}