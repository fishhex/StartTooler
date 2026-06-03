using System;
using SQLite;

namespace StartTooler.Models;

/// <summary>
/// 媒体文件数据模型，对应数据库中的 MediaFiles 表
/// </summary>
[Table("MediaFiles")]
public class MediaFile
{
    /// <summary>
    /// 主键 ID
    /// </summary>
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    /// <summary>
    /// 文件的绝对路径
    /// </summary>
    [Indexed]
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// 文件名（含扩展名）
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// 文件大小（字节）
    /// </summary>
    public long FileSize { get; set; }

    /// <summary>
    /// 文件最后修改时间
    /// </summary>
    public DateTime LastModified { get; set; }

    /// <summary>
    /// 预览图的相对路径（相对于应用数据目录的 Thumbnails 文件夹）
    /// </summary>
    public string? ThumbnailPath { get; set; }

    /// <summary>
    /// 文件所属目录路径
    /// </summary>
    [Indexed]
    public string DirectoryPath { get; set; } = string.Empty;

    /// <summary>
    /// 文件扩展名
    /// </summary>
    public string Extension { get; set; } = string.Empty;

    /// <summary>
    /// 记录创建时间
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// 记录更新时间
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}
