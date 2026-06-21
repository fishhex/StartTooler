using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace StartTooler.Models;

public enum SyncStatus
{
    UploadedAndLocal,
    UploadedButMissingLocal,
    NotUploaded
}

public record Photo(
    string Id,
    DateTime ShotAt,
    string? ThumbnailPath,
    SyncStatus Status,
    int GroupCount = 1
);

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
