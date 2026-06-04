using System;
using SQLite;

namespace StartTooler.Models;

[Table("AiSettings")]
public class AiSetting
{
    [PrimaryKey]
    public int Id { get; set; } = 1; // 单例配置，固定 ID 为 1

    public string ApiUrl { get; set; } = string.Empty;

    public string ApiToken { get; set; } = string.Empty;

    public string ModelName { get; set; } = "default";

    public DateTime UpdatedTime { get; set; } = DateTime.Now;
}
