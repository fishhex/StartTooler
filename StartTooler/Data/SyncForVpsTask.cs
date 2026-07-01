namespace StartTooler.Data;

/// <summary>
/// 公网接收任务。VPS 上传后，TCP 通知本地，本地入库并由 Poller 异步 scp 拉取。
/// 落库到 sync_for_vps_task 表（media.db），跨进程持久化。
///
/// 状态机（v0.3 pull 模型）：
///   - TCP 收到 file_pending 通知  → status=Pending, RemotePath=&lt;vps path&gt;, CreatedAt=now
///   - Poller 拿到 Pending 行 scp 成功 → status=Received, LocalPath=&lt;本地路径&gt;, AttemptCount++
///   - Poller 拿到 Pending 行 scp 失败 → status=Failed, LastError=&lt;stderr 片段&gt;
///                                     → **不重试**：用户手动从 UI 重试（v0.4 计划）
///
/// 写入时机：
///   - TCP 通知时 → InsertIfNewAsync（FileId UNIQUE 幂等）
///   - Poller 下载完成 → MarkReceived / MarkFailed
///
/// 进程崩溃兜底：Pending 行下次启动被 Poller 自动拉取（db 是 single source of truth）。
/// </summary>
public class SyncForVpsTask
{
    public long Id { get; set; }
    public string FileId { get; set; } = "";
    public string FileName { get; set; } = "";
    public long SizeBytes { get; set; }

    /// <summary>VPS 上的 tmp/&lt;id&gt;.bin 绝对路径，例如 /root/starttooler/tmp/abc123.bin。</summary>
    public string? RemotePath { get; set; }

    /// <summary>本地落地路径，scp 成功后才填。</summary>
    public string? LocalPath { get; set; }

    public SyncForVpsTaskStatus Status { get; set; } = SyncForVpsTaskStatus.Pending;
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }
    public long CreatedAt { get; set; }   // unix ms
    public long UpdatedAt { get; set; }   // unix ms
}

public enum SyncForVpsTaskStatus
{
    Pending = 0,
    Received = 1,
    Failed = 2,
}