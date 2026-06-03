using System;
using SQLite;

namespace StartTooler.Models;

[Table("RecentFolders")]
public class RecentFolder
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [Indexed]
    public string FolderPath { get; set; } = string.Empty;

    public string FolderName { get; set; } = string.Empty;

    public DateTime LastOpenedTime { get; set; }

    public int OpenCount { get; set; } = 1;

    // 忽略此属性，不存储到数据库
    [Ignore]
    public string DisplayText => $"{FolderName} ({LastOpenedTime:yyyy-MM-dd HH:mm})";
}
