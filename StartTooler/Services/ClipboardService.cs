using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;

namespace StartTooler.Services;

/// <summary>
/// v0.11 全局剪贴板服务。
///
/// Avalonia 11 没有 `Application.Current.Clipboard`，剪贴板挂在 TopLevel 上；
/// ViewModel 没有可视化树，需要一个能从 App 启动期就能拿到的 IClipboard 句柄。
/// 启动时 App.OnFrameworkInitializationCompleted 创建 MainWindow 后调
/// <see cref="Attach"/> 把当前 TopLevel 绑进来；VM 调 <see cref="SetTextAsync"/>。
///
/// 进程崩溃/未 Attach 时静默 no-op（VM 层 try/catch 兜底）。
/// </summary>
public static class ClipboardService
{
    private static IClipboard? _clipboard;

    public static void Attach(TopLevel topLevel)
    {
        _clipboard = topLevel.Clipboard;
    }

    public static async Task SetTextAsync(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var cb = _clipboard;
        if (cb == null) return;
        try
        {
            await cb.SetTextAsync(text);
        }
        catch (Exception ex)
        {
            // 剪贴板被其他进程占用等异常：trace 后吞掉，不影响主流程
            System.Diagnostics.Trace.WriteLine($"[ClipboardService] SetTextAsync err: {ex.Message}");
        }
    }
}
