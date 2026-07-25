using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace StartTooler.Services;

/// <summary>
/// 将普通视频文件（MP4/MOV/AVI 等）转换为天文摄影 SER 序列格式。
///
/// 输出规格：
///   - ColorID = 0（Mono 灰度）
///   - PixelDepth = 8（每像素 8 bit）
///   - LittleEndian = 0（按主流软件约定，0 表示小端）
///   - 帧数据为 FFmpeg 解码后的 8-bit grayscale rawvideo
///
/// SER 头结构遵循 Siril / SER.Lib / ffmpeg serdec 的 178-byte 布局。
/// </summary>
public static class SerConverter
{
    /// <summary>
    /// 将视频转换为 SER 文件。
    /// </summary>
    /// <param name="inputPath">源视频绝对路径。</param>
    /// <param name="outputPath">目标 SER 绝对路径。</param>
    /// <param name="ct">取消令牌。</param>
    public static async Task ConvertToSerAsync(
        string inputPath,
        string outputPath,
        CancellationToken ct = default)
    {
        if (!File.Exists(inputPath))
            throw new FileNotFoundException("源视频文件不存在", inputPath);

        var probe = await FfprobeRunner.ProbeAsync(inputPath, ct);
        if (probe == null)
            throw new InvalidOperationException("无法解析视频信息");
        if (probe.Width <= 0 || probe.Height <= 0)
            throw new InvalidOperationException($"视频分辨率无效：{probe.Width}x{probe.Height}");

        var tempRaw = Path.Combine(Path.GetTempPath(), $"starttooler-ser-{Guid.NewGuid():N}.raw");
        try
        {
            Trace.WriteLine($"[SerConverter] {inputPath} -> {tempRaw} ({probe.Width}x{probe.Height})");
            await DecodeToRawGrayAsync(inputPath, tempRaw, ct);
            await WriteSerAsync(tempRaw, outputPath, probe.Width, probe.Height, ct);
        }
        finally
        {
            try
            {
                if (File.Exists(tempRaw))
                    File.Delete(tempRaw);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[SerConverter] 清理临时文件失败: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 用 FFmpeg 把视频解码为 8-bit 灰度 rawvideo 临时文件。
    /// </summary>
    private static async Task DecodeToRawGrayAsync(
        string inputPath,
        string rawOutputPath,
        CancellationToken ct)
    {
        var ffmpegPath = FFmpegConfigurator.GetFFmpegBinaryPath();
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
        psi.ArgumentList.Add("-an");
        psi.ArgumentList.Add("-pix_fmt");
        psi.ArgumentList.Add("gray");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("rawvideo");
        psi.ArgumentList.Add(rawOutputPath);

        Trace.WriteLine($"[SerConverter] exec: {ffmpegPath} {string.Join(" ", psi.ArgumentList)}");

        using var proc = Process.Start(psi)!;
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        var stderr = await stderrTask;

        Trace.WriteLine($"[SerConverter] ffmpeg exit: {proc.ExitCode}");
        if (proc.ExitCode != 0)
        {
            Trace.WriteLine($"[SerConverter] ffmpeg stderr: {stderr.Trim()}");
            throw new InvalidOperationException($"ffmpeg 解码失败 (exit {proc.ExitCode}): {stderr.Trim()}");
        }

        if (!File.Exists(rawOutputPath))
            throw new InvalidOperationException("ffmpeg 未生成中间文件");
    }

    /// <summary>
    /// 将 rawvideo 数据打包为 SER 文件。
    /// </summary>
    private static async Task WriteSerAsync(
        string rawPath,
        string serPath,
        int width,
        int height,
        CancellationToken ct)
    {
        var fileInfo = new FileInfo(rawPath);
        var frameSize = (long)width * height;
        if (frameSize == 0)
            throw new InvalidOperationException("帧大小为 0");

        var totalBytes = fileInfo.Length;
        var frameCount = totalBytes / frameSize;
        if (frameCount <= 0)
            throw new InvalidOperationException("未从视频中提取到任何帧");

        if (totalBytes % frameSize != 0)
        {
            Trace.WriteLine($"[SerConverter] WARN: 中间文件大小 {totalBytes} 不是帧大小 {frameSize} 的整数倍，末尾不完整帧将被丢弃");
        }

        var copyBytes = frameCount * frameSize;

        using var rawStream = File.OpenRead(rawPath);
        using var serStream = File.Create(serPath);

        WriteHeader(serStream, width, height, frameCount);

        var buffer = new byte[81920];
        long copied = 0;
        while (copied < copyBytes)
        {
            ct.ThrowIfCancellationRequested();
            var toRead = (int)Math.Min(buffer.Length, copyBytes - copied);
            var read = await rawStream.ReadAsync(buffer.AsMemory(0, toRead), ct);
            if (read == 0)
                throw new InvalidOperationException("中间文件在复制过程中被截断");
            await serStream.WriteAsync(buffer.AsMemory(0, read), ct);
            copied += read;
        }

        await serStream.FlushAsync(ct);
        Trace.WriteLine($"[SerConverter] SER 完成: {serPath}, 帧数={frameCount}");
    }

    /// <summary>
    /// 写入 178-byte SER 文件头。
    /// </summary>
    private static void WriteHeader(Stream stream, int width, int height, long frameCount)
    {
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        // 0-13: 文件签名
        var signature = Encoding.ASCII.GetBytes("LUCAM-RECORDER");
        writer.Write(signature);

        // 14-17: Camera Series ID
        writer.Write((uint)0);

        // 18-21: Color ID (0 = Mono)
        writer.Write((uint)0);

        // 22-25: LittleEndian (0 表示小端，与 Siril/PIPP/SER.Lib 约定一致)
        writer.Write((uint)0);

        // 26-29: Image Width
        writer.Write((uint)width);

        // 30-33: Image Height
        writer.Write((uint)height);

        // 34-37: Pixel Depth
        writer.Write((uint)8);

        // 38-41: Frame Count（SER v2 为 32-bit）
        writer.Write((uint)Math.Min(frameCount, uint.MaxValue));

        // 42-81: Observer (40 bytes)
        writer.Write(new byte[40]);

        // 82-121: Instrument (40 bytes)
        writer.Write(new byte[40]);

        // 122-161: Telescope (40 bytes)
        writer.Write(new byte[40]);

        // 162-169: Local capture time (.NET DateTime ticks)
        var now = DateTime.Now;
        writer.Write((ulong)now.Ticks);

        // 170-177: UTC capture time (.NET DateTime ticks)
        var nowUtc = DateTime.UtcNow;
        writer.Write((ulong)nowUtc.Ticks);

        writer.Flush();
    }
}
