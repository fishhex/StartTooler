namespace StartTooler.Data;

/// <summary>
/// 公网接收任务。从 VPS ~/starttooler/tmp/&lt;id&gt;.bin scp 拉到本地后落地一行。
/// 落库到 sync_for_vps_task 表（media.db），跨进程持久化。
///
/// 状态机：
///   - scp exit 0                       → status=Received, local_path=&lt;本地路径&gt;
///   - scp exit != 0（解析 stderr）       → status=Failed, last_error=&lt;stderr 片段&gt;
///   - v0.3 启动时扫 VPS tmp/ 差集       → 重新入队 status=Pending
///
/// 写入时机：DrainBatchAsync 每文件一次 upsert（成功/失败各一次）。
/// </summary>
public class SyncForVpsTask
{
    public long Id { get; set; }
    public string FileId { get; set; } = "";
    public string FileName { get; set; } = "";
    public long SizeBytes { get; set; }
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