using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace StartTooler.Services;

/// <summary>
/// 跨平台系统 Shell 实现：
/// - macOS:   <c>open -R &lt;path&gt;</c> 在 Finder 中高亮文件
/// - Windows: <c>explorer /select,&lt;path&gt;</c> 在资源管理器中高亮文件
/// - Linux:   <c>xdg-open &lt;dir&gt;</c> 打开文件所在目录
/// </summary>
public class SystemShellService : ISystemShellService
{
    public void RevealInFolder(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            throw new ArgumentException("filePath is null or empty", nameof(filePath));
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"File not found: {filePath}", filePath);
        }

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                // macOS: open -R 在 Finder 中显示并高亮
                Process.Start(new ProcessStartInfo
                {
                    FileName = "open",
                    Arguments = $"-R \"{filePath}\"",
                    UseShellExecute = false,
                });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Windows: explorer /select,<path> 在资源管理器中显示并高亮
                // 注意：explorer 的参数格式特殊，不能加引号
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer",
                    Arguments = $"/select,\"{filePath}\"",
                    UseShellExecute = false,
                });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                // Linux: 打开文件所在目录（xdg-open 不会高亮文件）
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "xdg-open",
                        Arguments = $"\"{dir}\"",
                        UseShellExecute = false,
                    });
                }
            }
            else
            {
                throw new SystemShellException($"Unsupported platform: {RuntimeInformation.OSDescription}");
            }
        }
        catch (SystemShellException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new SystemShellException($"Failed to reveal file: {ex.Message}", ex);
        }
    }
}
