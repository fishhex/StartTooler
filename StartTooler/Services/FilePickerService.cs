using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace StartTooler.Services;

public class FilePickerService : IFilePickerService
{
    public async Task<string?> PickFileAsync(string title, params string[]? extensions)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = desktop.MainWindow;
            if (window == null)
                return null;

            var options = new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
            };

            if (extensions != null && extensions.Length > 0)
            {
                options.FileTypeFilter = new[]
                {
                    new FilePickerFileType("FFmpeg")
                    {
                        Patterns = extensions
                            .Select(e => $"*.{e.TrimStart('.')}")
                            .ToArray()
                    }
                };
            }

            var files = await window.StorageProvider.OpenFilePickerAsync(options);
            return files.Count > 0 ? files[0].Path.LocalPath : null;
        }

        return null;
    }

    public async Task<string?> SaveFileAsync(string title, string defaultFileName, string? extensionHint = null)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = desktop.MainWindow;
            if (window == null)
                return null;

            var options = new FilePickerSaveOptions
            {
                Title = title,
                DefaultExtension = extensionHint ?? "",
                SuggestedFileName = defaultFileName,
                ShowOverwritePrompt = true,
            };

            if (!string.IsNullOrEmpty(extensionHint))
            {
                var pattern = $"*.{extensionHint.TrimStart('.')}";
                options.FileTypeChoices = new[]
                {
                    new FilePickerFileType(extensionHint.ToUpperInvariant()) { Patterns = new[] { pattern } }
                };
            }

            var file = await window.StorageProvider.SaveFilePickerAsync(options);
            return file?.Path.LocalPath;
        }

        return null;
    }
}