using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace StartTooler.Data;

public interface ISyncForVpsTaskRepository
{
    /// <summary>
    /// 按 FileId upsert（存在则覆盖 Status/LocalPath/LastError/AttemptCount/UpdatedAt，
    /// 不存在则插入；FileId 是 UNIQUE 约束）。
    /// </summary>
    Task UpsertAsync(SyncForVpsTask task, CancellationToken ct = default);

    Task<SyncForVpsTask?> GetByFileIdAsync(string fileId, CancellationToken ct = default);

    Task<IReadOnlyList<SyncForVpsTask>> GetByStatusAsync(
        SyncForVpsTaskStatus status, CancellationToken ct = default);
}