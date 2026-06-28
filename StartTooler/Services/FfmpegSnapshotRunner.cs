using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace StartTooler.Services;

/// <summary>
/// 直接调 ffmpeg 命令行抓单帧，跨平台。
///
/// 命令：
///   ffmpeg -y -i &lt;input&gt; -vf "thumbnail=N,scale=W:H" -frames:v 1 -y &lt;output&gt;
///
/// - thumbnail=N filter 扫前 N 帧，挑出色彩变化最大的那一帧（自动跳过黑帧 / 相机初始化阶段）
/// - scale=W:H 缩放
/// - 输出格式由 outputPath 扩展名推断（.jpg → mjpeg 自动）
///
/// 之前用 "-ss T -i input -vframes 1" 的方案对 rawvideo AVI 不灵，因为 rawvideo 没有
/// 关键帧表，input seek (-ss 在 -i 前) 会被忽略，ffmpeg 始终从第 0 帧开始解码，
/// 导致抓到的永远是相机初始化阶段的暗帧。thumbnail filter 是位置无关的，自动找最佳帧。
/// </summary>
public static class FfmpegSnapshotRunner
{
    /// <summary>
    /// thumbnail filter 扫描的最大帧数。rawvideo 一帧 6MB，扫 1 万帧 ≈ 60GB 磁盘读，
    /// 但 rawvideo 解码只是字节拷贝，比有 codec 的视频快几个数量级，秒级返回。
    /// 1 万帧对 76fps 视频覆盖 ~130 秒，25fps 视频覆盖 ~400 秒，对天文 capture 足够。
    /// </summary>
    private const int ThumbnailScanFrames = 10000;

    public static async Task<bool> SnapshotAsync(
        string inputPath,
        string outputPath,
        TimeSpan captureTime,  // 保留参数以保持接口稳定；thumbnail filter 不使用此值
        int width,
        int height,
        CancellationToken ct = default)
    {
        var ffmpegPath = FFmpegConfigurator.GetFFmpegBinaryPath();
        Trace.WriteLine($"[FfmpegSnapshotRunner] exec: {ffmpegPath}");
        Trace.WriteLine($"[FfmpegSnapshotRunner]   note: captureTime={captureTime.TotalSeconds:F2}s ignored — using thumbnail filter for robust frame selection");

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(inputPath);
        psi.ArgumentList.Add("-vf");
        psi.ArgumentList.Add($"thumbnail={ThumbnailScanFrames},scale={width}:{height}");
        // -frames:v 1：保险起见再限制一次（thumbnail filter 选完只剩 1 帧，但显式更稳）
        // -update 1：image2 muxer 默认期望序列帧 (%03d 等)，单图输出必须告诉它"这是单张"
        psi.ArgumentList.Add("-frames:v");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-update");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add(outputPath);

        Trace.WriteLine($"[FfmpegSnapshotRunner] argv: ffmpeg {string.Join(" ", psi.ArgumentList)}");

        using var proc = Process.Start(psi)!;
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        var stderr = await stderrTask;

        Trace.WriteLine($"[FfmpegSnapshotRunner] exit: {proc.ExitCode}");
        if (proc.ExitCode != 0)
        {
            Trace.WriteLine($"[FfmpegSnapshotRunner] stderr: {stderr.Trim()}");
            throw new InvalidOperationException(
                $"ffmpeg failed (exit {proc.ExitCode}): {stderr.Trim()}");
        }

        return File.Exists(outputPath);
    }
}