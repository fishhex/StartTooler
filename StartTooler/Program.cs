using Avalonia;
using System;
using System.Diagnostics;
using System.IO;

namespace StartTooler;

sealed class Program
{
    static Program()
    {
        // 把 Debug.WriteLine 转到文件，绕开 WinExe 没 console 的限制。
        // 日志写在 cwd 下，dotnet run 和双击 .app 启动都生效。
        try
        {
            var cwd = Directory.GetCurrentDirectory();
            var logPath = Path.Combine(cwd, "starttooler-debug.log");
            Trace.Listeners.Add(new TextWriterTraceListener(logPath) { Name = "FileLog" });
            Trace.AutoFlush = true;  // 进程崩溃前不丢日志
            Trace.WriteLine($"[starttooler-debug] pid={Environment.ProcessId} cwd={cwd} log={logPath}");
        }
        catch (Exception ex)
        {
            // cwd 不可写时静默失败（macOS 双击 .app 时 cwd=/ 不可写），不影响主程序启动。
            Trace.WriteLine($"[starttooler-debug] Trace setup failed: {ex.Message}");
        }
    }

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
