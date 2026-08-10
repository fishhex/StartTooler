using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace StartTooler.Services;

/// <summary>
/// ZWO ASICAP / SharpCap 私有格式 SER（Sequence Exposure Recording）解析。
///
/// 头部定义：https://www.grischa-hahn.homepage.t-online.de/astro/ser/
/// 文件由 178 字节头部 + 连续 N 帧裸像素数据组成（无压缩、无 codec）。
/// 每帧字节数 = width × height × (bpp / 8)。
///
/// 本类只解析头部 + 读取首帧用于缩略图；不实现跳帧/播放（spec 决策：首帧 + 元数据面板）。
/// </summary>
public static class SerReader
{
    /// <summary>标准 SER 头部大小（bytes）。</summary>
    public const int StandardHeaderSize = 178;

    /// <summary>ZWO ASICAP "LUCAM-RECORDER" 头部大小（bytes）。实测 = 0xA0 = 160。</summary>
    public const int AsicapHeaderSize = 160;

    /// <summary>读取缓冲上限（标准 + ASICAP 中较大的一个）。</summary>
    private const int MaxHeaderSize = StandardHeaderSize;

    /// <summary>SER 像素格式枚举（ColorID 字段）。</summary>
    public enum SerColorMode : uint
    {
        Monochrome = 0,
        BayerRGGB = 1,
        BayerGRBG = 2,
        BayerGBRG = 3,
        BayerBGGR = 4,
        BayerCYYM = 8,
        BayerYCMY = 9,
        BayerYMCY = 16,
        BayerMYCY = 17,
        RGB = 18,
        BGR = 20,
    }

    /// <summary>
    /// SER 头部解析结果。失败时返回 null。
    /// 字段语义详见 https://www.grischa-hahn.homepage.t-online.de/astro/ser/SerFileFormat.html
    /// </summary>
    public sealed class SerHeader
    {
        public int Width { get; init; }
        public int Height { get; init; }
        /// <summary>每像素位数（8 / 16 / 32）。</summary>
        public int BitsPerPixel { get; init; }
        public long FrameCount { get; init; }
        public SerColorMode ColorMode { get; init; }
        public DateTime? ObservationTimeUtc { get; init; }
        public string Observer { get; init; } = "";
        public string Telescope { get; init; } = "";

        /// <summary>头部到首帧起始位置的偏移。标准 SER = 178；ASICAP LUCAM-RECORDER = 160。</summary>
        public int PixelDataOffset { get; init; } = StandardHeaderSize;

        /// <summary>单帧字节数 = Width × Height × Bpp/8。</summary>
        public long FrameBytes => (long)Width * Height * (BitsPerPixel / 8);

        /// <summary>是否彩色（需 debayer 或按 RGB 解析）。</summary>
        public bool IsColor => ColorMode != SerColorMode.Monochrome;

        /// <summary>是否 Bayer 模式（需要 debayer 才能显示成正常图片）。</summary>
        public bool IsBayer => ColorMode is SerColorMode.BayerRGGB
            or SerColorMode.BayerGRBG
            or SerColorMode.BayerGBRG
            or SerColorMode.BayerBGGR
            or SerColorMode.BayerCYYM
            or SerColorMode.BayerYCMY
            or SerColorMode.BayerYMCY
            or SerColorMode.BayerMYCY;

        /// <summary>UI 友好文字描述像素布局。</summary>
        public string ColorModeText => ColorMode switch
        {
            SerColorMode.Monochrome => "灰度",
            SerColorMode.BayerRGGB => "Bayer RGGB",
            SerColorMode.BayerGRBG => "Bayer GRBG",
            SerColorMode.BayerGBRG => "Bayer GBRG",
            SerColorMode.BayerBGGR => "Bayer BGGR",
            SerColorMode.BayerCYYM => "Bayer CYYM",
            SerColorMode.BayerYCMY => "Bayer YCMY",
            SerColorMode.BayerYMCY => "Bayer YMCY",
            SerColorMode.BayerMYCY => "Bayer MYCY",
            SerColorMode.RGB => "RGB",
            SerColorMode.BGR => "BGR",
            _ => $"未知({(uint)ColorMode})",
        };
    }

    /// <summary>
    /// 解析 SER 文件头部。失败时返回 null。
    /// 支持两种变体：
    ///   - 标准 SER：magic "V00703" / "V00804"，little-endian，header=178。
    ///   - ZWO ASICAP "LUCAM-RECORDER"：big-endian，header=160；像素起点 = 0xA0。
    /// </summary>
    public static SerHeader? ReadHeader(string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, FileOptions.SequentialScan);
            return ReadHeader(fs);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[SerReader] ReadHeader FAILED: {filePath} ex={ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 从已打开的流读取头部。流读完后位置 = header 末尾（即首帧起点）。
    /// </summary>
    public static SerHeader? ReadHeader(Stream stream)
    {
        Span<byte> buf = stackalloc byte[MaxHeaderSize];
        // 先读够 14 字节判定 magic，再决定要不要继续读满
        if (stream.Read(buf[..14]) < 14) return null;

        var magic = Encoding.ASCII.GetString(buf[..14]).TrimEnd('\0', ' ');

        // === ASICAP LUCAM-RECORDER（ZWO 私有变体）：big-endian，header=160 ===
        if (magic.StartsWith("LUCAM-RECORDER", StringComparison.Ordinal))
        {
            // 补读到 160 字节
            if (stream.Read(buf.Slice(14, AsicapHeaderSize - 14)) < AsicapHeaderSize - 14) return null;
            return ReadAsicapHeader(buf);
        }

        // === 标准 SER：little-endian，header=178 ===
        if (magic.StartsWith("V00703", StringComparison.Ordinal)
            || magic.StartsWith("V00804", StringComparison.Ordinal))
        {
            // 补读到 178 字节
            if (stream.Read(buf.Slice(14, StandardHeaderSize - 14)) < StandardHeaderSize - 14) return null;
            return ReadStandardHeader(buf);
        }

        // 不识别的格式：当作 "non-SER"，让上层走别的解码路径或返回失败。
        Trace.WriteLine($"[SerReader] ReadHeader: unknown magic '{magic}' (file is not SER/ASICAP?)");
        return null;
    }

    /// <summary>标准 SER v3/v4 头部解析（little-endian, 178 bytes）。</summary>
    private static SerHeader? ReadStandardHeader(Span<byte> buf)
    {
        var width = BinaryPrimitives.ReadInt32LittleEndian(buf.Slice(22, 4));
        var height = BinaryPrimitives.ReadInt32LittleEndian(buf.Slice(26, 4));
        var bpp = BinaryPrimitives.ReadInt32LittleEndian(buf.Slice(30, 4));
        var frameCount = BinaryPrimitives.ReadInt64LittleEndian(buf.Slice(34, 8));
        var colorModeRaw = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(94, 4));

        // 时间戳：offset 102..114，11 字节 ASCII（YYYYMMDDHHM）
        DateTime? obsTime = null;
        var tsStr = Encoding.ASCII.GetString(buf.Slice(102, 11));
        if (DateTime.TryParseExact(tsStr, "yyyyMMddHHm",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            obsTime = parsed;
        }

        // offset 122..146：ObserverInfo（40 字节 ASCII）
        // offset 162..202：TelescopeInfo（40 字节 ASCII）
        // 与社区常见的 122/146 offset 略有出入，统一以 122/162 为准。
        var observer = Encoding.ASCII.GetString(buf.Slice(122, 40)).TrimEnd('\0', ' ');
        var telescope = Encoding.ASCII.GetString(buf.Slice(162, 40)).TrimEnd('\0', ' ');

        // InstrumentInfo：实际格式较乱；不解析，置空
        return new SerHeader
        {
            Width = width,
            Height = height,
            BitsPerPixel = bpp,
            FrameCount = frameCount,
            ColorMode = (SerColorMode)colorModeRaw,
            ObservationTimeUtc = obsTime,
            Observer = observer,
            Telescope = telescope,
            PixelDataOffset = StandardHeaderSize,
        };
    }

    /// <summary>
    /// ZWO ASICAP "LUCAM-RECORDER" 私有头部解析（big-endian, 160 bytes）。
    /// 实测文件布局（hex dump）：
    ///   offset 0x00 (16) : "LUCAM-RECORDER\0\0"
    ///   offset 0x10 (4)  : ?? 固定 0x00000800
    ///   offset 0x14 (4)  : ?? 固定 0x00000000
    ///   offset 0x18 (4)  : width    (big-endian)
    ///   offset 0x1C (4)  : height   (big-endian)
    ///   offset 0x20 (4)  : bpp + color 标志位（实测 0x00001000 = 16-bit 灰度）
    ///   offset 0x24 (4)  : frame count (big-endian)
    ///   offset 0x28..0x9F : 填充/其他字段（暂无明确语义）
    ///   offset 0xA0    : 首帧像素数据起点
    /// </summary>
    private static SerHeader? ReadAsicapHeader(Span<byte> buf)
    {
        // magic 已确认。Big-endian 读。
        var width = BinaryPrimitives.ReadInt32BigEndian(buf.Slice(0x18, 4));
        var height = BinaryPrimitives.ReadInt32BigEndian(buf.Slice(0x1C, 4));
        var bppFlags = BinaryPrimitives.ReadUInt32BigEndian(buf.Slice(0x20, 4));
        var frameCount = BinaryPrimitives.ReadUInt32BigEndian(buf.Slice(0x24, 4));

        // bppFlags 解析：实测 0x00001000 → bpp=16；尚无实测 8-bit 样本，先用位运算取 bpp。
        // 已知两种 layout：
        //   0x00001000 → 16-bit 单色（实测 file1/file2 都是这个值）
        // 头部低 8 位看上去没有 bpp 直接含义；保守从 high byte 取近似：
        var bpp = (int)((bppFlags >> 12) & 0xF) * 8;
        if (bpp == 0) bpp = 16; // 兜底：实测 ASICAP 文件都 16-bit

        // ASICAP 颜色：实测文件未带 RGB 信息，按 16-bit 单色处理（Bayer/monochrome 之外的像素布局未知）。
        // 用户可在 UI 看灰度缩略图。如果未来发现 8-bit 或彩色变体，再扩展。
        var colorMode = SerColorMode.Monochrome;

        // ASICAP 头部不带时间戳/observer/telescope 字符串，统一置 null / 空
        return new SerHeader
        {
            Width = width,
            Height = height,
            BitsPerPixel = bpp,
            FrameCount = frameCount,
            ColorMode = colorMode,
            ObservationTimeUtc = null,
            Observer = "",
            Telescope = "",
            PixelDataOffset = AsicapHeaderSize,
        };
    }

    /// <summary>
    /// 读取 SER 文件首帧，转换为 8-bit RGB 字节数组（每像素 3 字节，行优先）。
    ///
    /// 性能优化：
    /// - 8-bit 灰度：直接拷贝 + 灰度→RGB 复制三通道。
    /// - 16-bit 灰度：归一化到 8-bit（取 max 当作 1.0，其他按比例拉伸，提升暗部细节可见度）。
    /// - Bayer 模式：暂用最简单的 "RGGB 灰度当作 R 单通道" 近似；不做真正 debayer。
    ///   UI 标注「Bayer 预览，非真实彩色」即可，对缩略图辨识够用。
    /// - RGB / BGR：直接拷字节。
    ///
    /// 返回值：byte[Width*Height*3]，失败时 null。
    /// </summary>
    public static byte[]? ReadFirstFrameRgb24(string filePath, SerHeader header)
    {
        if (header.Width <= 0 || header.Height <= 0) return null;

        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 64 * 1024, FileOptions.SequentialScan);
            fs.Seek(header.PixelDataOffset, SeekOrigin.Begin);
            return ReadFirstFrameRgb24(fs, header);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[SerReader] ReadFirstFrameRgb24 FAILED: {filePath} ex={ex.Message}");
            return null;
        }
    }

    /// <summary>从已 seek 到首帧起始位置的流读取一帧并转 8-bit RGB24。</summary>
    public static byte[]? ReadFirstFrameRgb24(Stream stream, SerHeader header)
    {
        var w = header.Width;
        var h = header.Height;
        var rgb = new byte[w * h * 3];

        if (header.ColorMode == SerColorMode.Monochrome && header.BitsPerPixel == 8)
        {
            // 灰度 8-bit：每像素 1 字节 → RGB24（每个像素 R=G=B）
            var raw = new byte[w * h];
            var read = ReadExact(stream, raw, 0, raw.Length);
            if (read < raw.Length) return null;
            for (int i = 0; i < raw.Length; i++)
            {
                rgb[i * 3] = raw[i];
                rgb[i * 3 + 1] = raw[i];
                rgb[i * 3 + 2] = raw[i];
            }
            return rgb;
        }

        if (header.ColorMode == SerColorMode.Monochrome && header.BitsPerPixel == 16)
        {
            // 灰度 16-bit：扫描全帧找 max，按比例拉伸到 8-bit
            var raw = new byte[w * h * 2];
            var read = ReadExact(stream, raw, 0, raw.Length);
            if (read < raw.Length) return null;

            ushort max = 1;
            for (int i = 0; i + 1 < raw.Length; i += 2)
            {
                var v = (ushort)(raw[i] | (raw[i + 1] << 8));
                if (v > max) max = v;
            }

            for (int i = 0, j = 0; i + 1 < raw.Length; i += 2, j += 3)
            {
                var v = (ushort)(raw[i] | (raw[i + 1] << 8));
                var scaled = (byte)Math.Min(255, v * 255 / max);
                rgb[j] = scaled;
                rgb[j + 1] = scaled;
                rgb[j + 2] = scaled;
            }
            return rgb;
        }

        if (header.IsBayer && header.BitsPerPixel == 8)
        {
            // Bayer 8-bit：当作灰度展示（不做 debayer）
            var raw = new byte[w * h];
            var read = ReadExact(stream, raw, 0, raw.Length);
            if (read < raw.Length) return null;
            for (int i = 0; i < raw.Length; i++)
            {
                rgb[i * 3] = raw[i];
                rgb[i * 3 + 1] = raw[i];
                rgb[i * 3 + 2] = raw[i];
            }
            return rgb;
        }

        if (header.IsBayer && header.BitsPerPixel == 16)
        {
            // Bayer 16-bit：归一化拉伸
            var raw = new byte[w * h * 2];
            var read = ReadExact(stream, raw, 0, raw.Length);
            if (read < raw.Length) return null;

            ushort max = 1;
            for (int i = 0; i + 1 < raw.Length; i += 2)
            {
                var v = (ushort)(raw[i] | (raw[i + 1] << 8));
                if (v > max) max = v;
            }

            for (int i = 0, j = 0; i + 1 < raw.Length; i += 2, j += 3)
            {
                var v = (ushort)(raw[i] | (raw[i + 1] << 8));
                var scaled = (byte)Math.Min(255, v * 255 / max);
                rgb[j] = scaled;
                rgb[j + 1] = scaled;
                rgb[j + 2] = scaled;
            }
            return rgb;
        }

        if (header.ColorMode == SerColorMode.RGB && header.BitsPerPixel == 8)
        {
            // RGB 8-bit：直接拷贝
            var raw = new byte[w * h * 3];
            var read = ReadExact(stream, raw, 0, raw.Length);
            if (read < raw.Length) return null;
            Buffer.BlockCopy(raw, 0, rgb, 0, raw.Length);
            return rgb;
        }

        if (header.ColorMode == SerColorMode.BGR && header.BitsPerPixel == 8)
        {
            // BGR 8-bit：交换 R/B 通道
            var raw = new byte[w * h * 3];
            var read = ReadExact(stream, raw, 0, raw.Length);
            if (read < raw.Length) return null;
            for (int i = 0; i + 2 < raw.Length; i += 3)
            {
                rgb[i] = raw[i + 2];
                rgb[i + 1] = raw[i + 1];
                rgb[i + 2] = raw[i];
            }
            return rgb;
        }

        Trace.WriteLine($"[SerReader] ReadFirstFrameRgb24 unsupported: color={header.ColorMode} bpp={header.BitsPerPixel}");
        return null;
    }

    private static int ReadExact(Stream stream, byte[] buffer, int offset, int count)
    {
        var total = 0;
        while (total < count)
        {
            var n = stream.Read(buffer, offset + total, count - total);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }
}