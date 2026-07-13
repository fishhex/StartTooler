using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Avalonia.Data.Converters;
using StartTooler.Data;

namespace StartTooler.Converters;

// === v0.11: EXIF 信息读取（spec doc/0.11/spec/05-ui-interaction-review.md §11.2）
//
// 策略：手工解析 JPEG EXIF（不引新依赖），SkiaSharp 已可用但不依赖其 SKCodec。
//   - JPEG 起始 marker 0xFFD8 → 找 0xFFE1 (APP1) → "Exif\0\0" 开头 → TIFF header
//   - TIFF header: 'II' (little-endian) 或 'MM' (big-endian) + 0x002A + IFD0 偏移
//   - IFD0 关键 tags: 0x010F Make / 0x0110 Model / 0x829A ExposureTime / 0x829D FNumber
//     / 0x8827 ISO / 0x920A FocalLength
//
// 7 个独立 Converter 共享 ReadExif(string?) 静态方法。
//   任何非 JPEG / 无 EXIF / 解析失败 → 返回空值；UI 用 IsVisible 把整段 EXIF 区块隐藏。

/// <summary>
/// 原始 EXIF 数据载体。HasAny 在 Camera/Aperture/Shutter/ISO/Focal 任一有值时为 true。
/// </summary>
public sealed class ExifData
{
    public string? CameraMake { get; set; }
    public string? CameraModel { get; set; }
    public string? Aperture { get; set; }     // "f/2.8"
    public string? ShutterSpeed { get; set; } // "1/200s"
    public int? Iso { get; set; }
    public string? FocalLength { get; set; }  // "50mm"

    public string? Camera => string.IsNullOrEmpty(CameraModel) ? CameraMake : CameraModel;
    public bool HasAny => !string.IsNullOrEmpty(Camera)
        || !string.IsNullOrEmpty(Aperture)
        || !string.IsNullOrEmpty(ShutterSpeed)
        || Iso.HasValue
        || !string.IsNullOrEmpty(FocalLength);
}

internal static class ExifReader
{
    /// <summary>从 JPEG 文件路径读 EXIF。失败 / 非 JPEG / 无 EXIF → 返回 null。</summary>
    public static ExifData? Read(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            using var fs = File.OpenRead(path);
            return ParseFromStream(fs);
        }
        catch
        {
            return null;
        }
    }

    private static ExifData? ParseFromStream(Stream stream)
    {
        using var br = new BinaryReader(stream, Encoding.ASCII, leaveOpen: false);

        // 1. JPEG SOI
        if (br.ReadUInt16() != 0xFFD8) return null;

        // 2. 遍历 markers 找 APP1 (0xFFE1) + "Exif\0\0"
        byte[]? tiff = null;
        while (true)
        {
            int marker;
            try { marker = br.ReadUInt16(); }
            catch (EndOfStreamException) { return null; }
            if ((marker & 0xFF00) != 0xFF00) return null;

            if (marker == 0xFFE1)
            {
                var len = br.ReadUInt16();
                var payload = br.ReadBytes(len - 2);
                // "Exif\0\0" 头
                if (payload.Length >= 8
                    && payload[0] == (byte)'E' && payload[1] == (byte)'x'
                    && payload[2] == (byte)'i' && payload[3] == (byte)'f'
                    && payload[4] == 0 && payload[5] == 0)
                {
                    tiff = new byte[len - 2 - 6];
                    Array.Copy(payload, 6, tiff, 0, tiff.Length);
                    break;
                }
            }
            else
            {
                // 跳过此 marker payload
                var len = br.ReadUInt16();
                if (len < 2) return null;
                br.BaseStream.Seek(len - 2, SeekOrigin.Current);
            }
        }

        if (tiff == null || tiff.Length < 8) return null;
        return ParseTiff(tiff);
    }

    private static ExifData ParseTiff(byte[] tiff)
    {
        var data = new ExifData();

        // 3. TIFF header
        bool littleEndian;
        if (tiff[0] == (byte)'I' && tiff[1] == (byte)'I') littleEndian = true;
        else if (tiff[0] == (byte)'M' && tiff[1] == (byte)'M') littleEndian = false;
        else return data;
        if (tiff[2] != 0x2A || tiff[3] != 0x2A) return data;

        int ifd0Offset = (int)ReadUInt32(tiff, 4, littleEndian);
        if (ifd0Offset < 0 || ifd0Offset + 2 > tiff.Length) return data;

        // 4. IFD0 entries
        int numEntries = ReadUInt16(tiff, ifd0Offset, littleEndian);
        int exifSubIfdOffset = -1;
        for (int i = 0; i < numEntries; i++)
        {
            int entryOffset = ifd0Offset + 2 + i * 12;
            if (entryOffset + 12 > tiff.Length) break;

            int tag = ReadUInt16(tiff, entryOffset, littleEndian);
            int type = ReadUInt16(tiff, entryOffset + 2, littleEndian);
            int count = (int)ReadUInt32(tiff, entryOffset + 4, littleEndian);
            int valueOffset = entryOffset + 8;

            if (tag == 0x010F) // Make
                data.CameraMake = ReadString(tiff, valueOffset, type, count, littleEndian);
            else if (tag == 0x0110) // Model
                data.CameraModel = ReadString(tiff, valueOffset, type, count, littleEndian);
            else if (tag == 0x8769) // ExifIFDPointer
            {
                exifSubIfdOffset = (int)ReadUInt32(tiff, valueOffset, littleEndian);
            }
        }

        // 5. ExifSubIFD: ISO / Shutter / Aperture / FocalLength
        if (exifSubIfdOffset >= 0 && exifSubIfdOffset + 2 <= tiff.Length)
        {
            int subEntries = ReadUInt16(tiff, exifSubIfdOffset, littleEndian);
            for (int i = 0; i < subEntries; i++)
            {
                int entryOffset = exifSubIfdOffset + 2 + i * 12;
                if (entryOffset + 12 > tiff.Length) break;
                int tag = ReadUInt16(tiff, entryOffset, littleEndian);
                int type = ReadUInt16(tiff, entryOffset + 2, littleEndian);
                int count = (int)ReadUInt32(tiff, entryOffset + 4, littleEndian);
                int valueOffset = entryOffset + 8;

                if (tag == 0x829A) // ExposureTime (rational)
                {
                    var r = ReadRational(tiff, valueOffset, littleEndian);
                    if (r.HasValue) data.ShutterSpeed = FormatShutter(r.Value);
                }
                else if (tag == 0x829D) // FNumber
                {
                    var r = ReadRational(tiff, valueOffset, littleEndian);
                    if (r.HasValue) data.Aperture = $"f/{r.Value.n / (double)r.Value.d:F1}";
                }
                else if (tag == 0x8827) // ISOSpeedRatings (short)
                {
                    data.Iso = (int)ReadUInt16(tiff, valueOffset, littleEndian);
                }
                else if (tag == 0x920A) // FocalLength (rational, mm)
                {
                    var r = ReadRational(tiff, valueOffset, littleEndian);
                    if (r.HasValue) data.FocalLength = $"{r.Value.n / (double)r.Value.d:F0}mm";
                }
            }
        }

        return data;
    }

    private static ushort ReadUInt16(byte[] buf, int offset, bool littleEndian)
    {
        if (offset + 2 > buf.Length) return 0;
        if (littleEndian) return (ushort)(buf[offset] | (buf[offset + 1] << 8));
        return (ushort)((buf[offset] << 8) | buf[offset + 1]);
    }

    private static uint ReadUInt32(byte[] buf, int offset, bool littleEndian)
    {
        if (offset + 4 > buf.Length) return 0;
        if (littleEndian) return (uint)(buf[offset] | (buf[offset + 1] << 8) | (buf[offset + 2] << 16) | (buf[offset + 3] << 24));
        return (uint)((buf[offset] << 24) | (buf[offset + 1] << 16) | (buf[offset + 2] << 8) | buf[offset + 3]);
    }

    private static (long n, long d)? ReadRational(byte[] buf, int offset, bool littleEndian)
    {
        if (offset + 8 > buf.Length) return null;
        long n = (long)ReadUInt32(buf, offset, littleEndian);
        long d = (long)ReadUInt32(buf, offset + 4, littleEndian);
        if (d == 0) return null;
        return (n, d);
    }

    private static string? ReadString(byte[] buf, int valueOffset, int type, int count, bool littleEndian)
    {
        // type=2 (ASCII)；data 4 字节内能塞下就 inline，否则按 offset 读
        int stringOffset;
        if (count <= 4) stringOffset = valueOffset;
        else stringOffset = (int)ReadUInt32(buf, valueOffset, littleEndian);
        if (stringOffset < 0 || stringOffset + count > buf.Length) return null;
        var bytes = new byte[count];
        Array.Copy(buf, stringOffset, bytes, 0, count);
        // 去尾 null
        var s = Encoding.ASCII.GetString(bytes).TrimEnd('\0').Trim();
        return string.IsNullOrEmpty(s) ? null : s;
    }

    private static string FormatShutter((long n, long d) r)
    {
        if (r.n == 0) return "";
        // < 1s → "1/Ns"；≥ 1s → "N.Ns"
        if (r.n < r.d)
        {
            var denom = (long)Math.Round((double)r.d / r.n);
            return $"1/{denom}s";
        }
        return $"{(r.n / (double)r.d):F1}s";
    }
}

/// <summary>Base class: file path → ExifData 缓存 + 子属性读取。</summary>
public abstract class ExifConverterBase : IValueConverter
{
    protected static ExifData? GetData(object? value)
    {
        // value 是 MediaFile? 或 string?（绝对路径）。两种都要兼容。
        string? path = value switch
        {
            MediaFile mf => string.IsNullOrEmpty(mf.ProjectPath) || string.IsNullOrEmpty(mf.RelativePath)
                ? null
                : System.IO.Path.Combine(mf.ProjectPath, mf.RelativePath),
            string s => s,
            _ => null,
        };
        return ExifReader.Read(path);
    }

    public abstract object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class ExifToVisConverter : ExifConverterBase
{
    public override object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => GetData(value)?.HasAny ?? false;
}

public class ExifToCameraConverter : ExifConverterBase
{
    public override object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => GetData(value)?.Camera ?? "";
}

public class ExifToApertureConverter : ExifConverterBase
{
    public override object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => GetData(value)?.Aperture ?? "";
}

public class ExifToShutterConverter : ExifConverterBase
{
    public override object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => GetData(value)?.ShutterSpeed ?? "";
}

public class ExifToIsoConverter : ExifConverterBase
{
    public override object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => GetData(value)?.Iso?.ToString() ?? "";
}

public class ExifToFocalLengthConverter : ExifConverterBase
{
    public override object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => GetData(value)?.FocalLength ?? "";
}
