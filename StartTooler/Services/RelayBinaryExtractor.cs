using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;

namespace StartTooler.Services;

/// <summary>
/// 把嵌入到 .NET 程序集里的 upload-relay Linux 二进制按目标架构解压到本地 temp。
///
/// - 资源名约定：StartTooler.Resources.relay-binaries.upload-relay-linux-{amd64|arm64}
/// - 解压目标：{Temp}/starttooler/upload-relay-linux-{arch}，可执行
/// - 已存在则跳过（速度优先；如要强制覆盖请先删）
/// - chmod 0755：mac/linux 通过 File.SetUnixFileMode；Windows 上忽略
/// </summary>
public static class RelayBinaryExtractor
{
    public const string ResourcePrefix = "StartTooler.Resources.relay-binaries.";
    public static readonly string[] SupportedArchs = { "amd64", "arm64" };

    private static readonly object _lock = new();
    private static readonly string _extractDir = Path.Combine(Path.GetTempPath(), "starttooler");

    static RelayBinaryExtractor()
    {
        try { Directory.CreateDirectory(_extractDir); }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[RelayBinary] mkdir temp: {ex.Message}"); }
    }

    /// <summary>
    /// 把指定架构的 Linux 二进制从嵌入资源解压到本地 temp，返回绝对路径。
    /// 找不到资源或 IO 失败抛异常。
    /// </summary>
    public static string Extract(string arch)
    {
        if (string.IsNullOrEmpty(arch) || !SupportedArchs.Contains(arch))
            throw new ArgumentException($"unsupported arch: '{arch}'. Supported: {string.Join(", ", SupportedArchs)}");

        var dest = Path.Combine(_extractDir, $"upload-relay-linux-{arch}");

        lock (_lock)
        {
            if (File.Exists(dest) && new FileInfo(dest).Length > 0)
                return dest;

            var resourceName = ResourcePrefix + $"upload-relay-linux-{arch}";
            var assembly = typeof(RelayBinaryExtractor).Assembly;
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
                throw new InvalidOperationException(
                    $"Embedded relay binary not found: '{resourceName}'. " +
                    $"Available: {string.Join(", ", assembly.GetManifestResourceNames())}");

            using var fs = File.Create(dest);
            stream.CopyTo(fs);
        }

#pragma warning disable CA1416
        TryChmod755(dest);
#pragma warning restore CA1416
        return dest;
    }

    /// <summary>检查指定架构的二进制是否已解压好。</summary>
    public static bool IsExtracted(string arch)
    {
        if (!SupportedArchs.Contains(arch)) return false;
        var p = Path.Combine(_extractDir, $"upload-relay-linux-{arch}");
        return File.Exists(p) && new FileInfo(p).Length > 0;
    }

    /// <summary>返回本机可解压的所有架构（用于 sanity check / 日志）。</summary>
    public static string[] AvailableArchs()
    {
        var assembly = typeof(RelayBinaryExtractor).Assembly;
        return assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix))
            .Select(n => n.Substring(ResourcePrefix.Length))
            .ToArray();
    }

    [SupportedOSPlatform("Linux")]
    [SupportedOSPlatform("macOS")]
    private static void TryChmod755(string path)
    {
        // mac/linux：File.SetUnixFileMode（.NET 7+）
        // windows：调用不支持（由 SupportedOSPlatformGuard 静态剔除，try/catch 仍然兜底）
        try
        {
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                File.SetUnixFileMode(path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[RelayBinary] chmod {path} skipped: {ex.Message}");
        }
    }
}
