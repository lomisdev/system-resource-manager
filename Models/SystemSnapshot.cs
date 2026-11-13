// Models/SystemSnapshot.cs
using System.Collections.Generic;

namespace System_Resource_Manager.Models;

public readonly record struct CpuTimes(long Total, long Idle);
public readonly record struct RawProcess(string Name, long Ticks, double RssMb);

public sealed record SystemSnapshot(
    CpuTimes Cpu,
    long MemTotalKb, long MemAvailableKb,
    long SwapTotalKb, long SwapFreeKb,
    double UptimeSeconds,
    string LoadAvg,
    IReadOnlyDictionary<int, RawProcess> Processes);