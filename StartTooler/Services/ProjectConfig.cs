using System.Collections.Generic;

namespace StartTooler.Services;

public class ProjectConfig
{
    public string? CurrentDirectory { get; set; }
    /// <summary>
    /// v0.12: 跨设备项目标识（D02 spec 01-cross-device-sync.md §3.1；同时被 D04 的
    /// /api/v1/projects 响应 projectName 字段使用）。为什么 D04 要率先落地：D04
    /// 的 /api/v1/projects 响应里带了 projectName 字段（与 D02 字段同名同义），
    /// App 端拿了这个字段后写入 media_files.project_name 做跨设备索引。所以
    /// 不论 D02 是否先做，这个字段在 D04 就要存在。
    /// </summary>
    public string? ProjectName { get; set; }
    public List<string> RecentDirectories { get; set; } = new();
}
