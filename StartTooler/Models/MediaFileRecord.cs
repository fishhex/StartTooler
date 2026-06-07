using System;
using SQLite;

namespace StartTooler.Models;

[Table("MediaFileRecords")]
public class MediaFileRecord
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [Indexed]
    public string FeatureCode { get; set; } = string.Empty;

    [Indexed]
    public string FileName { get; set; } = string.Empty;

    [Indexed]
    public string LocalPath { get; set; } = string.Empty;

    [Indexed]
    public string RootPath { get; set; } = string.Empty;

    [Indexed]
    public string? GroupId { get; set; }

    public long PerceptualHash { get; set; }

    public bool IsUploaded { get; set; }

    public DateTime CreatedTime { get; set; } = DateTime.Now;

    public DateTime UpdatedTime { get; set; } = DateTime.Now;
}
