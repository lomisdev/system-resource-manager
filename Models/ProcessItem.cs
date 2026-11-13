// Models/ProcessItem.cs
using CommunityToolkit.Mvvm.ComponentModel;

namespace System_Resource_Manager.Models;

public partial class ProcessItem : ObservableObject
{
    public int ProcessId { get; init; }          // never changes, no notification needed

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private double _cpuPercent;
    [ObservableProperty] private double _memoryMb;   // RSS, real megabytes this time
}