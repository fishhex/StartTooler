using System.Threading.Tasks;

namespace StartTooler.Services;

public interface IDirectoryPickerService
{
    Task<string?> PickFolderAsync(string title = "选择文件夹");
}
