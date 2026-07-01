using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace StartTooler.Data;

public interface ISyncForVpsTaskRepository
{
    /// <summary>
    /// TCP 收到通知时调用：UNIQUE(fileId) 幂等插入 Pending 行。
    /// 已存在则 noop，返回 false。
    /// </summary>
    Task<bool> InsertIfNewAsync(
        string fileId, string fileName, long sizeBytes, string? remotePath,
        CancellationToken ct = default);

    /// <summary>Poller 拉一批 Pending 行（先到先下载）。</summary>
    Task<IReadOnlyList<SyncForVpsTask>> GetPendingBatchAsync(
        int limit, CancellationToken ct = default);

    /// <summary>scp 成功 → Received + LocalPath + AttemptCount++。</summary>
    Task MarkReceivedAsync(long id, string localPath, CancellationToken ct = default);

    /// <summary>scp 失败 → Failed + LastError + AttemptCount++。</summary>
    Task MarkFailedAsync(long id, string error, CancellationToken ct = default);

    /// <summary>UI 显示用：Pending 行数。</summary>
    Task<int> CountPendingAsync(CancellationToken ct = default);
}