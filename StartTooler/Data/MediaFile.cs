using System;

namespace StartTooler.Data;

public enum MediaType
{
    Image,
    Video
}

public sealed class MediaFile
{
    public long Id { get; set; }
    public string ProjectPath { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string FileName { get; set; } = "";
    public MediaType MediaType { get; set; }
    public long FileSize { get; set; }
    public long LastModified { get; set; }
    public long? ShotAt { get; set; }
    public bool IsUploaded { get; set; }
    public bool LocalExists { get; set; } = true;
    public string? ThumbnailPath { get; set; }
    public string? RemoteUrl { get; set; }
    public long? UploadedAt { get; set; }
    public long ScannedAt { get; set; }

    public DateTime? ShotAtDateTime => ShotAt.HasValue
        ? DateTimeOffset.FromUnixTimeMilliseconds(ShotAt.Value).DateTime
        : null;
}
