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

    /// <summary>原始曝光时长，秒（用于统计）。</summary>
    public double? ExposureTimeSeconds { get; set; }

    /// <summary>原始焦距，mm（用于统计）。</summary>
    public double? FocalLengthMm { get; set; }

    /// <summary>35mm 等效焦距，mm（用于统计）。</summary>
    public double? FocalLength35Mm { get; set; }

    /// <summary>v0.12: GPS 纬度，十进制度（-90..90）。正 = 北纬。</summary>
    public double? GpsLatitude { get; set; }

    /// <summary>v0.12: GPS 经度，十进制度（-180..180）。正 = 东经。</summary>
    public double? GpsLongitude { get; set; }

    /// <summary>v0.12: 是否携带 GPS 坐标。</summary>
    public bool HasGps => GpsLatitude.HasValue && GpsLongitude.HasValue;

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

    /// <summary>
    /// v0.12: 仅解析 GPS 坐标（十进制度）。失败 / 非 JPEG / 无 GPS → 返回 null。
    /// 与 Read() 独立调用 —— 调用方按需读取，避免覆盖照片主元数据路径的开销。
    /// </summary>
    public static (double Latitude, double Longitude)? ReadGps(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            using var fs = File.OpenRead(path);
            var data = ParseFromStream(fs);
            if (data == null || !data.HasGps) return null;
            return (data.GpsLatitude!.Value, data.GpsLongitude!.Value);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 解析 GPS 子 IFD（IFD0 → tag 0x8825 指向）。
    /// 关键字段：GPSLatitudeRef(0x0001) / GPSLatitude(0x0002) / GPSLongitudeRef(0x0003) / GPSLongitude(0x0004)。
    /// </summary>
    private static (double lat, double lon)? ParseGpsSubIfd(byte[] tiff, int offset, bool littleEndian)
    {
        if (offset < 0 || offset + 2 > tiff.Length) return null;
        int numEntries = ReadUInt16(tiff, offset, littleEndian);

        string? latRef = null;
        double? lat = null;
        string? lonRef = null;
        double? lon = null;

        for (int i = 0; i < numEntries; i++)
        {
            int entryOffset = offset + 2 + i * 12;
            if (entryOffset + 12 > tiff.Length) break;

            int tag = ReadUInt16(tiff, entryOffset, littleEndian);
            int count = (int)ReadUInt32(tiff, entryOffset + 4, littleEndian);
            int valueOffset = entryOffset + 8;

            if (tag == 0x0001) // GPSLatitudeRef (ASCII "N"/"S")
            {
                latRef = ReadAsciiInline(tiff, valueOffset, count);
            }
            else if (tag == 0x0002) // GPSLatitude (3 rationals: 度/分/秒)
            {
                var r = ReadThreeRationals(tiff, valueOffset, littleEndian);
                if (r.HasValue) lat = r.Value;
            }
            else if (tag == 0x0003) // GPSLongitudeRef (ASCII "E"/"W")
            {
                lonRef = ReadAsciiInline(tiff, valueOffset, count);
            }
            else if (tag == 0x0004) // GPSLongitude
            {
                var r = ReadThreeRationals(tiff, valueOffset, littleEndian);
                if (r.HasValue) lon = r.Value;
            }
        }

        if (!lat.HasValue || !lon.HasValue) return null;

        // 度分秒 → 十进制度
        var latitude = lat.Value;
        var longitude = lon.Value;
        if (latRef == "S") latitude = -latitude;
        if (lonRef == "W") longitude = -longitude;
        return (latitude, longitude);
    }

    /// <summary>
    /// 读 3 个连续 RATIONAL（度/分/秒）。tag 值 8 字节内能放下 3 个？不行，
    /// 因此总是按 valueOffset 指向的位置读 24 字节。
    /// </summary>
    private static double? ReadThreeRationals(byte[] tiff, int valueOffset, bool littleEndian)
    {
        if (valueOffset + 24 > tiff.Length) return null;

        // IFD valueOffset 对 type=RATIONAL(5) 且 count>1 时指向首 RATIONAL 位置
        var d = ReadRational(tiff, valueOffset, littleEndian);
        var m = ReadRational(tiff, valueOffset + 8, littleEndian);
        var s = ReadRational(tiff, valueOffset + 16, littleEndian);
        if (!d.HasValue || !m.HasValue || !s.HasValue) return null;
        if (d.Value.d == 0) return null;

        return d.Value.n / (double)d.Value.d
             + m.Value.n / (double)m.Value.d / 60.0
             + s.Value.n / (double)s.Value.d / 3600.0;
    }

    /// <summary>读取 4 字节内 ASCII 字符串（N/S/E/W 等单字符 ref 标记）。</summary>
    private static string? ReadAsciiInline(byte[] buf, int offset, int count)
    {
        if (count < 1 || offset + count > buf.Length) return null;
        var bytes = new byte[count];
        Array.Copy(buf, offset, bytes, 0, count);
        return System.Text.Encoding.ASCII.GetString(bytes).TrimEnd('\0', ' ').Trim();
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
        int gpsIfdOffset = -1;
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
            else if (tag == 0x8825) // v0.12: GPSIFDPointer
            {
                gpsIfdOffset = (int)ReadUInt32(tiff, valueOffset, littleEndian);
            }
        }

        // v0.12: 解析 GPS 子 IFD（独立子例程，失败不影响主 ExifData）
        if (gpsIfdOffset >= 0)
        {
            var gps = ParseGpsSubIfd(tiff, gpsIfdOffset, littleEndian);
            if (gps.HasValue)
            {
                data.GpsLatitude = gps.Value.lat;
                data.GpsLongitude = gps.Value.lon;
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
                    if (r.HasValue)
                    {
                        data.ExposureTimeSeconds = r.Value.n / (double)r.Value.d;
                        data.ShutterSpeed = FormatShutter(r.Value);
                    }
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
                    if (r.HasValue)
                    {
                        var mm = r.Value.n / (double)r.Value.d;
                        data.FocalLengthMm = mm;
                        data.FocalLength = $"{mm:F0}mm";
                    }
                }
                else if (tag == 0xA405) // FocalLengthIn35mmFilm (short)
                {
                    data.FocalLength35Mm = ReadUInt16(tiff, valueOffset, littleEndian);
                }
            }
        }

        // 6. 补全 35mm 等效焦距：未记录时用原始焦距兜底。
        if (data.FocalLength35Mm == null && data.FocalLengthMm != null)
            data.FocalLength35Mm = data.FocalLengthMm;

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
