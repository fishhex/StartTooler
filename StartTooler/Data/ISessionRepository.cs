using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using StartTooler.Models;

namespace StartTooler.Data;

/// <summary>
/// v0.12: 拍摄会话仓储接口（spec/03-shooting-diary.md §2.2）。
/// 管理 sessions 表的 CRUD。
/// </summary>
public interface ISessionRepository
{
    Task<IReadOnlyList<Session>> GetByProjectAsync(string projectPath, CancellationToken ct = default);
    Task<Session?> GetByIdAsync(string sessionId, CancellationToken ct = default);
    Task UpsertAsync(Session session, CancellationToken ct = default);
    Task DeleteAsync(string sessionId, CancellationToken ct = default);
    Task UpsertBatchAsync(IReadOnlyList<Session> sessions, CancellationToken ct = default);
    Task DeleteBatchAsync(IReadOnlyList<string> sessionIds, CancellationToken ct = default);
}