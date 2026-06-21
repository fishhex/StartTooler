using System.Collections.Generic;

namespace StartTooler.Services;

public class ProjectConfig
{
    public string? CurrentDirectory { get; set; }
    public List<string> RecentDirectories { get; set; } = new();
}
