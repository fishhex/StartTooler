using System;

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

public record TimelineEntry(DateTime Date, int PhotoCount);
