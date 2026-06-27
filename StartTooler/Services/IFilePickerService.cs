using System.Threading.Tasks;

namespace StartTooler.Services;

/// <summary>
/// 单文件选择器（区别于 IDirectoryPickerService）。
/// 给「指定 ffmpeg 可执行路径」之类需要选单个文件的场景用。
/// </summary>
public interface IFilePickerService
{
    /// <summary>
    /// 弹出系统文件选择器，让用户挑一个文件。
    /// </summary>
    /// <param name="title">对话框标题。</param>
    /// <param name="extensions">可选的文件扩展名过滤（如 "exe" / "mp4"），不带点号。</param>
    /// <returns>用户选中的绝对路径；取消或失败返回 null。</returns>
    Task<string?> PickFileAsync(string title, params string[]? extensions);
}