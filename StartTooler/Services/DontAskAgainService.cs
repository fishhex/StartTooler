using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace StartTooler.Services;

/// <summary>
/// v0.11 spec/08 §5.1: 「不再提示」偏好管理。
/// 每个操作类型一个 key，值为 ISO 8601 截止时间，勾选后 30 天内不再弹同类型确认框。
/// 存储位置: <see cref="ConfigKeys.DontAskAgain"/>（config.db）。
/// </summary>
public class DontAskAgainService
{
    private const int ExpiryDays = 30;

    private readonly IConfigService _configService;

    public DontAskAgainService(IConfigService configService)
    {
        _configService = configService;
    }

    /// <summary>检查是否需要弹出确认框（true = 需要弹）</summary>
    public async Task<bool> ShouldAskAsync(string operationKey)
    {
        if (string.IsNullOrEmpty(operationKey)) return true;

        var prefs = await _configService.GetAsync<Dictionary<string, string>>(ConfigKeys.DontAskAgain);
        if (prefs == null || !prefs.TryGetValue(operationKey, out var expiry))
            return true;

        if (DateTime.TryParse(expiry, null, System.Globalization.DateTimeStyles.RoundtripKind, out var expiryDate))
        {
            return DateTime.UtcNow >= expiryDate;
        }

        // 解析失败保守弹
        Trace.WriteLine($"[DontAskAgain] {operationKey} 截止时间解析失败: {expiry}");
        return true;
    }

    /// <summary>记录「不再提示」，30 天内有效</summary>
    public async Task SetDontAskAsync(string operationKey)
    {
        if (string.IsNullOrEmpty(operationKey)) return;

        var prefs = await _configService.GetAsync<Dictionary<string, string>>(ConfigKeys.DontAskAgain)
                    ?? new Dictionary<string, string>();
        prefs[operationKey] = DateTime.UtcNow.AddDays(ExpiryDays).ToString("O");
        await _configService.SetAsync(ConfigKeys.DontAskAgain, prefs);
    }

    /// <summary>
    /// 重置指定操作的「不再提示」（恢复显示确认框）。
    /// operationKey 为 null 时重置全部（写空 dict 实现"全部失效"）。
    /// </summary>
    public async Task ResetAsync(string? operationKey = null)
    {
        if (operationKey == null)
        {
            // 写空 dict 而非 null（IConfigService.SetAsync<T> 约束 T : class，不接受 null）
            await _configService.SetAsync(ConfigKeys.DontAskAgain, new Dictionary<string, string>());
            return;
        }

        var prefs = await _configService.GetAsync<Dictionary<string, string>>(ConfigKeys.DontAskAgain);
        if (prefs == null) return;
        if (prefs.Remove(operationKey))
        {
            await _configService.SetAsync(ConfigKeys.DontAskAgain, prefs);
        }
    }
}
