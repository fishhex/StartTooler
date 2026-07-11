using System;
using System.Threading;
using System.Threading.Tasks;

namespace StartTooler.Helpers;

/// <summary>
/// 通用 debouncer（spec doc/15-manual-tag-edit.md §6.4）。
/// 多次连续 Trigger 在 delayMs 窗口内合并成一次 action 调用，最后一次 Trigger 之后等 delayMs 触发。
/// 用于：连续编辑 N 个文件 tag 时，左栏 TagGroups 不刷 N 次，只刷 1 次。
/// </summary>
public class TagChangeDebouncer
{
    private readonly int _delayMs;
    private CancellationTokenSource? _cts;

    public TagChangeDebouncer(int delayMs = 500)
    {
        _delayMs = delayMs;
    }

    /// <summary>
    /// 触发 action。同一实例上连续调用时，前一次的 action 会被取消（OperationCanceledException），
    /// 只有最后一次调用的 action 会在 delayMs 后真正执行。
    /// </summary>
    public void Trigger(Func<CancellationToken, Task> action)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = RunAsync(action, ct);
    }

    private async Task RunAsync(Func<CancellationToken, Task> action, CancellationToken ct)
    {
        try
        {
            await Task.Delay(_delayMs, ct);
            await action(ct);
        }
        catch (OperationCanceledException)
        {
            // 被新一轮 Trigger 取消，静默吞掉
        }
    }
}
