using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StartTooler.Data;
using StartTooler.Models;

namespace StartTooler.Services;

/// <summary>
/// v0.12: 拍摄会话聚类服务 —— 按时间间隔对孤儿照片聚类生成 Session。
///
/// 策略（spec/03-shooting-diary.md §2.3 + code-structure §3.1）：
/// 1. 查询所有 session_id IS NULL 的照片
/// 2. 单趟扫描：相邻照片间隔 > intervalHours → 切分新会话
/// 3. 批量 Upsert Session
/// 4. 批量 UPDATE media_files.session_id
///
/// 已有手动分配的 session_id 不会被覆盖；已有 Session（即使无照片关联）也不删除。
/// </summary>
public class SessionClusteringService
{
    private readonly IMediaRepository _mediaRepo;
    private readonly ISessionRepository _sessionRepo;

    public SessionClusteringService(IMediaRepository mediaRepo, ISessionRepository sessionRepo)
    {
        _mediaRepo = mediaRepo;
        _sessionRepo = sessionRepo;
    }

    /// <summary>
    /// 对项目下所有"未关联会话"的照片按时间间隔聚类，生成/更新 Session。
    /// </summary>
    /// <param name="projectPath">项目绝对路径</param>
    /// <param name="intervalHours">切分间隔（小时），默认 4</param>
    public async Task<IReadOnlyList<Session>> ClusterAsync(string projectPath, int intervalHours = 4, CancellationToken ct = default)
    {
        if (intervalHours < 1) intervalHours = 1;
        if (intervalHours > 24) intervalHours = 24;

        var normalizedPath = Path.GetFullPath(projectPath).TrimEnd(Path.DirectorySeparatorChar);

        // 查询项目下所有有 shot_at 的照片（包含孤儿和非孤儿）
        // 非孤儿的不会被覆盖，但聚类范围仍按全量扫描，避免漏掉手动拆分后遗留的孤儿
        // 用一个非常大的时间窗口覆盖整个项目
        var earliest = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var latest = DateTimeOffset.UtcNow.AddDays(1);
        var allFiles = await _mediaRepo.GetByTimeRangeAsync(normalizedPath, earliest, latest, SortMode.TimeAsc, limit: int.MaxValue, ct: ct);

        // 过滤出孤儿照片：session_id 为空、未删除、有 shot_at
        var orphans = allFiles
            .Where(f => string.IsNullOrEmpty(f.SessionId) && f.ShotAt.HasValue)
            .ToList();

        if (orphans.Count == 0) return Array.Empty<Session>();

        // 单趟扫描聚类
        var sessions = new List<Session>();
        var assignments = new List<(long FileId, string SessionId)>();

        Session current = null!;
        DateTimeOffset currentStart = default, currentEnd = default;
        foreach (var file in orphans)
        {
            var shotAt = DateTimeOffset.FromUnixTimeMilliseconds(file.ShotAt!.Value);

            if (current == null)
            {
                currentStart = shotAt;
                currentEnd = shotAt;
                current = CreateSession(normalizedPath, currentStart, currentEnd);
            }
            else
            {
                var gapHours = (shotAt - currentEnd).TotalHours;
                if (gapHours > intervalHours)
                {
                    // 提交上一个
                    current.EndTime = currentEnd.UtcDateTime;
                    sessions.Add(current);
                    current = CreateSession(normalizedPath, shotAt, shotAt);
                    currentStart = shotAt;
                    currentEnd = shotAt;
                }
                else
                {
                    if (shotAt > currentEnd) currentEnd = shotAt;
                }
            }
            assignments.Add((file.Id, current.Id));
        }
        if (current != null)
        {
            current.EndTime = currentEnd.UtcDateTime;
            sessions.Add(current);
        }

        if (sessions.Count == 0) return Array.Empty<Session>();

        // 持久化
        await _sessionRepo.UpsertBatchAsync(sessions, ct);
        await _mediaRepo.SetSessionBatchAsync(assignments, ct);

        return sessions;
    }

    private static Session CreateSession(string projectPath, DateTimeOffset start, DateTimeOffset end)
    {
        return new Session
        {
            Id = Guid.NewGuid().ToString("N"),
            ProjectPath = projectPath,
            Title = $"{start.LocalDateTime:yyyy-MM-dd} 出摊",
            StartTime = start.UtcDateTime,
            EndTime = end.UtcDateTime,
        };
    }
}