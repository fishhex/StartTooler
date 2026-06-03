using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using StartTooler.Models;
using System;
using System.Diagnostics;
using System.IO;

namespace StartTooler.Views;

public partial class PreviewWindow : Window
{
    private Image? _previewImage;

    public PreviewWindow()
    {
        InitializeComponent();
        _previewImage = this.FindControl<Image>("PreviewImage");
    }

    public void ShowFile(MediaFile mediaFile)
    {
        Title = Path.GetFileName(mediaFile.FilePath);

        if (mediaFile.FileType == "视频")
        {
            // 视频：使用系统默认播放器打开
            OpenWithDefaultPlayer(mediaFile.FilePath);
            // 关闭预览窗口
            Close();
        }
        else
        {
            // 图片：在窗口中显示
            ShowImage(mediaFile.FilePath);
        }
    }

    private void ShowImage(string filePath)
    {
        if (_previewImage == null)
        {
            Console.WriteLine("PreviewImage control is null");
            return;
        }

        if (!File.Exists(filePath))
        {
            Console.WriteLine($"File not found: {filePath}");
            return;
        }

        try
        {
            // 先释放旧的 Bitmap 资源
            if (_previewImage.Source is IDisposable disposable)
            {
                disposable.Dispose();
            }

            using var stream = File.OpenRead(filePath);
            var bitmap = new Bitmap(stream);
            _previewImage.Source = bitmap;
            
            Console.WriteLine($"Image loaded successfully: {filePath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading image: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
        }
    }

    private void OpenWithDefaultPlayer(string filePath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = filePath,
                UseShellExecute = true
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error opening file: {ex.Message}");
        }
    }
}
