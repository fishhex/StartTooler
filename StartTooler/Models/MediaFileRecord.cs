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

    /// <summary>
    /// 云存储类型（对应 CloudStorageProvider 枚举值）
    /// </summary>
    public int CloudStorage { get; set; }

    /// <summary>
    /// 存储桶名称
    /// </summary>
    public string Bucket { get; set; } = string.Empty;

    /// <summary>
    /// 存储桶内的路径
    /// </summary>
    public string BucketPath { get; set; } = string.Empty;

    /// <summary>
    /// 是否本地已删除（物理文件已删除，但数据库记录保留）
    /// </summary>
    public bool IsLocalDeleted { get; set; }

    public DateTime CreatedTime { get; set; } = DateTime.Now;

    public DateTime UpdatedTime { get; set; } = DateTime.Now;
}
