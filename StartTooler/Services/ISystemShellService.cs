using System;

namespace StartTooler.Services;

/// <summary>
/// 跨平台系统 Shell 调用服务。
/// 用于在系统文件管理器中显示文件（macOS Finder / Windows Explorer / Linux 文件管理器）。
/// </summary>
public interface ISystemShellService
{
    /// <summary>
    /// 在系统文件管理器中显示指定文件（高亮/选中）。
    /// </summary>
    /// <param name="filePath">文件的绝对路径。</param>
    /// <exception cref="FileNotFoundException">文件不存在时抛出。</exception>
    /// <exception cref="SystemShellException">系统调用失败时抛出。</exception>
    void RevealInFolder(string filePath);
}

/// <summary>
/// SystemShellException：系统 Shell 调用失败。
/// </summary>
public class SystemShellException : Exception
{
    public SystemShellException(string message) : base(message) { }
    public SystemShellException(string message, Exception inner) : base(message, inner) { }
}
