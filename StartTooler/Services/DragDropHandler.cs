using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using StartTooler.ViewModels;

// v0.11 spec/06: 用的是 Avalonia 11 过渡期 API(DragEventArgs.Data / DataFormats.Files / GetFileNames)，
// 新版推荐 DataTransfer / DataFormat.File / GetFiles,功能等价。升级留到 v0.12 导航重构一并做。
#pragma warning disable CS0618

namespace StartTooler.Services;

/// <summary>
/// v0.11 spec/06: 全局拖拽路由 + 文件/文件夹处理。
/// v0.11 ADR-01: v0.11 阶段接收 <see cref="ViewPage"/> enum 路由（v0.12 导航重构后改为 string）。
///
/// 路由分发:
///   - Gallery: 拖入文件/文件夹 → 复制到 {projectDir}/{YYYY-MM-DD}/，触发 Gallery 刷新
///   - UploadServer: 拖入文件 → 复制 + 记录上传历史，触发 Gallery 刷新
///   - Settings: 拖入**文件夹** → 设为项目目录（不直接保存，等用户点保存）
///   - 其他页面: DragDropEffects.None
/// </summary>
public class DragDropHandler
{
    private readonly IConfigService _configService;
    private readonly Func<GalleryViewModel?> _getGalleryVm;
    private readonly Func<UploadServerViewModel?> _getUploadVm;
    private readonly Func<SettingsViewModel?> _getSettingsVm;
    private readonly Func<ViewPage> _getCurrentPage;

    public DragDropHandler(
        IConfigService configService,
        Func<GalleryViewModel?> getGalleryVm,
        Func<UploadServerViewModel?> getUploadVm,
        Func<SettingsViewModel?> getSettingsVm,
        Func<ViewPage> getCurrentPage)
    {
        _configService = configService;
        _getGalleryVm = getGalleryVm;
        _getUploadVm = getUploadVm;
        _getSettingsVm = getSettingsVm;
        _getCurrentPage = getCurrentPage;
    }

    /// <summary>判断是否接受拖放(由 MainWindow 在 DragOver 调用)</summary>
    public DragDropEffects OnDragOver(DragEventArgs e)
    {
        if (e.Data.Contains(DataFormats.Files) == false) return DragDropEffects.None;
        var paths = e.Data.GetFileNames()?.ToList();
        if (paths == null || paths.Count == 0) return DragDropEffects.None;

        var page = _getCurrentPage();

        // Settings 页:只接受**单个文件夹**
        if (page == ViewPage.Settings)
        {
            if (paths.Count == 1 && Directory.Exists(paths[0]))
                return DragDropEffects.Link;
            return DragDropEffects.None;
        }

        // Gallery / UploadServer:接受文件(和文件夹,handler 内会递归展开)
        if (page == ViewPage.Gallery || page == ViewPage.UploadServer)
        {
            // Gallery 需要项目目录已设置
            if (page == ViewPage.Gallery)
            {
                var projPath = _configService.GetAsync<ProjectConfig>(ConfigKeys.Project)
                    .GetAwaiter().GetResult()?.CurrentDirectory;
                if (string.IsNullOrEmpty(projPath)) return DragDropEffects.None;
            }
            return DragDropEffects.Copy;
        }

        // Trash / 其他: 不接受
        return DragDropEffects.None;
    }

    /// <summary>放下时执行文件操作</summary>
    public async Task OnDropAsync(DragEventArgs e, CancellationToken ct = default)
    {
        if (e.Data.Contains(DataFormats.Files) == false) return;
        var rawPaths = e.Data.GetFileNames()?.ToList();
        if (rawPaths == null || rawPaths.Count == 0) return;

        var page = _getCurrentPage();

        if (page == ViewPage.Settings)
        {
            // 必须是单个文件夹
            if (rawPaths.Count == 1 && Directory.Exists(rawPaths[0]))
            {
                HandleSettingsDrop(rawPaths[0]);
            }
            return;
        }

        if (page == ViewPage.Gallery || page == ViewPage.UploadServer)
        {
            // 展开文件夹为文件列表
            var files = ExpandPathsToFiles(rawPaths);
            if (files.Count == 0) return;

            if (page == ViewPage.Gallery)
                await HandleGalleryDropAsync(files, ct);
            else
                await HandleUploadDropAsync(files, ct);
        }
    }

    /// <summary>把混合的文件+文件夹路径展开为文件列表(递归)</summary>
    private static List<string> ExpandPathsToFiles(List<string> paths)
    {
        var result = new List<string>();
        foreach (var p in paths)
        {
            try
            {
                if (File.Exists(p))
                {
                    result.Add(p);
                }
                else if (Directory.Exists(p))
                {
                    // 递归枚举所有文件(spec §8 边界:混合内容展开)
                    foreach (var f in Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories))
                    {
                        result.Add(f);
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[DragDrop] 展开路径失败 {p}: {ex.Message}");
            }
        }
        return result;
    }

    private async Task HandleGalleryDropAsync(List<string> filePaths, CancellationToken ct)
    {
        var vm = _getGalleryVm();
        if (vm == null) return;

        var projectDir = _configService.GetAsync<ProjectConfig>(ConfigKeys.Project)
            .GetAwaiter().GetResult()?.CurrentDirectory;
        if (string.IsNullOrEmpty(projectDir)) return;

        var copyResult = await CopyFilesToProjectAsync(filePaths, projectDir, ct);

        // 触发 Gallery 刷新(走 debounce 避免短时间多次扫描)
        if (copyResult.Copied > 0)
        {
            await Task.Delay(300, ct);
            vm.RequestRefreshDebounced(delayMs: 500);
        }

        ShowCopyToast(copyResult);
    }

    private async Task HandleUploadDropAsync(List<string> filePaths, CancellationToken ct)
    {
        var uploadVm = _getUploadVm();
        if (uploadVm == null) return;

        var projectDir = _configService.GetAsync<ProjectConfig>(ConfigKeys.Project)
            .GetAwaiter().GetResult()?.CurrentDirectory;
        if (string.IsNullOrEmpty(projectDir)) return;

        var copyResult = await CopyFilesToProjectAsync(filePaths, projectDir, ct);

        // 记录上传历史(跟手机上传同一通道,显示拖入的源文件名)
        if (copyResult.Copied > 0)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                foreach (var src in filePaths)
                {
                    if (!File.Exists(src)) continue;
                    var name = Path.GetFileName(src);
                    uploadVm.UploadHistory.Insert(0, new UploadHistoryEntry
                    {
                        FileName = name,
                        Timestamp = DateTime.Now,
                        IsSuccess = true,
                    });
                }
                // 限制 50 条
                while (uploadVm.UploadHistory.Count > 50)
                    uploadVm.UploadHistory.RemoveAt(uploadVm.UploadHistory.Count - 1);
            });
        }

        // 上传页拖入的文件同样出现在 Gallery 中,通知 Gallery 刷新
        var galleryVm = _getGalleryVm();
        if (galleryVm != null && copyResult.Copied > 0)
        {
            await Task.Delay(300, ct);
            galleryVm.RequestRefreshDebounced(delayMs: 500);
        }

        ShowCopyToast(copyResult);
    }

    private void HandleSettingsDrop(string folderPath)
    {
        var vm = _getSettingsVm();
        if (vm == null) return;

        vm.SetProjectDirectoryFromDrag(folderPath);
        Trace.WriteLine($"[DragDrop] Settings: 项目目录已设为 {folderPath}（未保存）");
    }

    private static async Task<CopyResult> CopyFilesToProjectAsync(
        List<string> filePaths, string projectDir, CancellationToken ct)
    {
        var destDir = Path.Combine(projectDir, DateTime.Now.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(destDir);

        var result = new CopyResult();
        const int maxSuffixAttempts = 100;

        foreach (var sourcePath in filePaths)
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(sourcePath)) { result.Skipped++; continue; }

            var fileName = Path.GetFileName(sourcePath);
            var destPath = Path.Combine(destDir, fileName);

            // 重名追加序号
            if (File.Exists(destPath))
            {
                var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
                var ext = Path.GetExtension(fileName);
                int suffix = 1;
                do
                {
                    destPath = Path.Combine(destDir, $"{nameWithoutExt}_{suffix++}{ext}");
                    if (suffix > maxSuffixAttempts) break;
                } while (File.Exists(destPath));

                if (suffix > maxSuffixAttempts)
                {
                    result.Errors.Add($"{fileName}: 重名超 {maxSuffixAttempts} 次,跳过");
                    continue;
                }
            }

            try
            {
                await Task.Run(() => File.Copy(sourcePath, destPath, overwrite: false), ct);
                result.Copied++;
            }
            catch (Exception ex)
            {
                result.Errors.Add($"{fileName}: {ex.Message}");
            }
        }

        return result;
    }

    private static void ShowCopyToast(CopyResult result)
    {
        if (result.Copied == 0 && result.Errors.Count == 0) return;

        var title = result.Copied > 0 ? $"已导入 {result.Copied} 个文件" : "导入失败";
        var body = result.Errors.Count > 0
            ? $"{result.Copied} 成功,{result.Skipped} 跳过,{result.Errors.Count} 失败"
            : (result.Skipped > 0 ? $"{result.Copied} 成功,{result.Skipped} 跳过" : "");

        var type = result.Errors.Count > 0 && result.Copied == 0
            ? NotificationType.Error
            : NotificationType.Success;
        NotificationService.Current.Show(title, body, type);
    }

    private class CopyResult
    {
        public int Copied;
        public int Skipped;
        public List<string> Errors = new();
    }
}
