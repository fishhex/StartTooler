using System;
using System.Globalization;
using System.IO;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using StartTooler.Data;
using StartTooler.Models;

namespace StartTooler.Converters;

/// <summary>
/// v0.11: 灯箱预览窗口专用转换器集合（图片 only）。
///
/// 设计原则：薄转换器 + 纯函数，不引用 VM，只依赖 Models/Data。这样：
///   1. 单元测试时不用 mock VM
///   2. 复用方便 —— 其他场景（详情面板 / 导出报告）也能用
///   3. XAML binding 链短，编译期绑定能解析
///
/// 用法：XAML 静态引用
///   {Binding CurrentFile, Converter={x:Static conv:LightboxConverters.MediaFileToOriginalBitmap}}
/// </summary>
public static class LightboxConverters
{
    // ============================================================
    //  文件加载 / 路径
    // ============================================================

    /// <summary>
    /// MediaFile → 原图 Bitmap（灯箱专用，走 ProjectPath + RelativePath 原图路径，不走缩略图）。
    /// 文件不存在 / 解码失败返回 null（UI 自动隐藏或显示占位）。
    /// </summary>
    public static readonly IValueConverter MediaFileToOriginalBitmap =
        new FuncValueConverter<MediaFile?, Bitmap?>(file =>
        {
            if (file == null) return null;
            var path = Path.Combine(file.ProjectPath, file.RelativePath);
            if (!File.Exists(path)) return null;
            try
            {
                return new Bitmap(path);
            }
            catch
            {
                return null;
            }
        });

    /// <summary>
    /// 字符串路径 → Bitmap（视频模式缩略图加载用）。
    /// 空路径 / 文件不存在 / 解码失败 → null（UI 空白，▶ overlay 仍可见）。
    /// </summary>
    public static readonly IValueConverter FilePathToBitmap =
        new FuncValueConverter<string?, Bitmap?>(path =>
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (!File.Exists(path)) return null;
            try
            {
                return new Bitmap(path);
            }
            catch
            {
                return null;
            }
        });

    // ============================================================
    //  格式化
    // ============================================================

    /// <summary>
    /// long → "1.5 MB" / "256 B" 友好文件大小文本。0 / 负数 → "—"
    /// 跟 GalleryViewModel.FormatSize 行为一致（项目内不抽公共 helper，灯箱自带）。
    /// </summary>
    public static readonly IValueConverter LongToFileSize =
        new FuncValueConverter<long, string>(bytes =>
        {
            if (bytes <= 0) return "—";
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024.0:F1} MB";
            return $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GB";
        });

    /// <summary>
    /// DateTime? → "yyyy-MM-dd HH:mm" 本地时间。null → "—"
    /// </summary>
    public static readonly IValueConverter NullableDateTimeToDisplay =
        new FuncValueConverter<DateTime?, string>(dt =>
            dt.HasValue ? dt.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture) : "—");

    /// <summary>
    /// SyncStatus? → 中文显示文本。null（Uploading/Failed/Paused 期间）→ "—"
    /// </summary>
    public static readonly IValueConverter SyncStatusToDisplay =
        new FuncValueConverter<SyncStatus?, string>(s => s switch
        {
            SyncStatus.UploadedAndLocal => "已上传 · 本地存在",
            SyncStatus.UploadedButMissingLocal => "已上传 · 本地缺失",
            SyncStatus.NotUploaded => "未上传",
            _ => "—"
        });
}