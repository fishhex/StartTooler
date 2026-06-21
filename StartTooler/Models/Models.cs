using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace StartTooler.Models;

// UI 内部状态枚举，用于转换器桥接
public enum SyncStatus
{
    UploadedAndLocal,
    UploadedButMissingLocal,
    NotUploaded
}

public enum RefreshState
{
    Idle,
    Scanning,
    Completed,
    Stopped
}

public partial class TimelineEntry : ObservableObject
{
    public DateTime Date { get; }
    public int PhotoCount { get; }

    [ObservableProperty] private bool isSelected;

    public TimelineEntry(DateTime date, int photoCount)
    {
        Date = date;
        PhotoCount = photoCount;
    }
}

public sealed class ScanProgress
{
    public int Total { get; set; }
    public int Processed { get; set; }
    public int Failed { get; set; }
    public string? CurrentFile { get; set; }
}
