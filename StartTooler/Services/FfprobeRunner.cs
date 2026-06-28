using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace StartTooler.Services;

/// <summary>
/// ffprobe 解析媒体文件后返回的结果。
/// </summary>
public record VideoProbeResult(
    TimeSpan Duration,
    int Width,
    int Height,
    string Codec,
    double FrameRate
);

/// <summary>
/// 直接调 ffprobe 命令行，跨平台。
///
/// 命令：
///   ffprobe -v quiet -print_format json -show_format -show_streams &lt;input&gt;
///
/// stdout 是标准 JSON（跨平台一致），用 System.Text.Json 解析。
/// </summary>
public static class FfprobeRunner
{
    public static async Task<VideoProbeResult?> ProbeAsync(string inputPath, CancellationToken ct = default)
    {
        var ffprobePath = FFmpegConfigurator.GetFFprobeBinaryPath();
        Trace.WriteLine($"[FfprobeRunner] exec: {ffprobePath}");

        var psi = new ProcessStartInfo
        {
            FileName = ffprobePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("quiet");
        psi.ArgumentList.Add("-print_format");
        psi.ArgumentList.Add("json");
        psi.ArgumentList.Add("-show_format");
        psi.ArgumentList.Add("-show_streams");
        psi.ArgumentList.Add(inputPath);

        using var proc = Process.Start(psi)!;
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        Trace.WriteLine($"[FfprobeRunner] exit: {proc.ExitCode}, stdout {stdout.Length} bytes");
        if (proc.ExitCode != 0)
        {
            Trace.WriteLine($"[FfprobeRunner] stderr: {stderr.Trim()}");
            throw new InvalidOperationException(
                $"ffprobe failed (exit {proc.ExitCode}): {stderr.Trim()}");
        }

        return ParseProbeJson(stdout);
    }

    private static VideoProbeResult? ParseProbeJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // duration（format.duration 是字符串，如 "24.040000"）
        if (!root.TryGetProperty("format", out var formatEl)) return null;
        if (!formatEl.TryGetProperty("duration", out var durEl)) return null;
        var durationStr = durEl.GetString();
        if (string.IsNullOrEmpty(durationStr)) return null;
        if (!double.TryParse(durationStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var durationSec))
            return null;

        // 第一个 video 流
        if (!root.TryGetProperty("streams", out var streamsEl)) return null;
        JsonElement? videoStream = null;
        foreach (var stream in streamsEl.EnumerateArray())
        {
            if (stream.TryGetProperty("codec_type", out var codecTypeEl) &&
                codecTypeEl.GetString() == "video")
            {
                videoStream = stream;
                break;
            }
        }
        if (videoStream == null) return null;

        var vs = videoStream.Value;
        var width = vs.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
        var height = vs.TryGetProperty("height", out var h) ? h.GetInt32() : 0;
        var codec = vs.TryGetProperty("codec_name", out var c) ? c.GetString() ?? "unknown" : "unknown";

        double frameRate = 0;
        if (vs.TryGetProperty("r_frame_rate", out var fr))
        {
            frameRate = ParseFrameRate(fr.GetString());
        }

        return new VideoProbeResult(
            TimeSpan.FromSeconds(durationSec),
            width,
            height,
            codec,
            frameRate);
    }

    /// <summary>
    /// 解析 "25/1" 或 "30000/1001" 这种分数形式的帧率。
    /// </summary>
    private static double ParseFrameRate(string? fraction)
    {
        if (string.IsNullOrEmpty(fraction)) return 0;
        var parts = fraction.Split('/');
        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var num) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var den) &&
            den != 0)
        {
            return num / den;
        }
        if (double.TryParse(fraction, NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
        {
            return f;
        }
        return 0;
    }
}