namespace StartTooler.Services;

/// <summary>
/// 公网代理配置：SSH 到 VPS，部署并管理 upload-relay（Go）进程；
/// 本地 StartTooler 作为 TCP 客户端连 VPS 接收文件。
/// </summary>
public class PublicRelayConfig
{
    // SSH 认证（密码 / Key 二选一；Key 优先）
    public string SshHost { get; set; } = "";
    public int SshPort { get; set; } = 22;
    public string SshUser { get; set; } = "";
    public string? SshPassword { get; set; }
    public string? SshKeyPath { get; set; }

    /// <summary>VPS 上脚本与 PID 文件存放目录（默认 ~/starttooler）。</summary>
    public string SshRemotePath { get; set; } = "~/starttooler";

    // 端口
    public int HttpPort { get; set; } = 8765;
    public int TcpPort { get; set; } = 8766;

    /// <summary>公网域名/IP（可选，用于 UI 展示）。留空则用 SshHost。</summary>
    public string? PublicHost { get; set; }

    /// <summary>
    /// VPS CPU 架构：auto / amd64 / arm64。
    /// 默认 auto：部署时 SSH `uname -m` 自动检测。
    /// </summary>
    public string RemoteArch { get; set; } = RelayArch.Auto;
}

/// <summary>CPU 架构选项（用于配置 RemoteArch 字段）。</summary>
public static class RelayArch
{
    public const string Auto = "auto";
    public const string Amd64 = "amd64";
    public const string Arm64 = "arm64";

    /// <summary>把 uname -m 的输出（如 "x86_64", "aarch64"）归一化到 amd64/arm64；解析失败返回 null。</summary>
    public static string? FromUnameM(string s)
    {
        var t = (s ?? "").Trim().ToLowerInvariant();
        return t switch
        {
            "x86_64" or "amd64" => Amd64,
            "aarch64" or "arm64" => Arm64,
            _ => null,
        };
    }
}