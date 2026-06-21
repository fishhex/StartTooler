using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StartTooler.Services;

namespace StartTooler.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly IDirectoryPickerService _directoryPicker;
    private string? _lastSavedDirectory;

    [ObservableProperty] private string? selectedProjectDirectory;
    [ObservableProperty] private ObservableCollection<string> recentDirectories;
    [ObservableProperty] private bool isDirty;
    [ObservableProperty] private bool isSaving;
    [ObservableProperty] private string? statusMessage;

    public SettingsViewModel(IDirectoryPickerService directoryPicker)
    {
        _directoryPicker = directoryPicker;
        RecentDirectories = new ObservableCollection<string>();
        IsDirty = false;
        IsSaving = false;
    }

    partial void OnSelectedProjectDirectoryChanged(string? value)
    {
        RecomputeDirty();
        StatusMessage = null;
    }

    private void RecomputeDirty()
    {
        IsDirty = SelectedProjectDirectory != _lastSavedDirectory;
    }

    [RelayCommand]
    private async Task BrowseDirectory()
    {
        var folder = await _directoryPicker.PickFolderAsync("选择项目目录");
        if (string.IsNullOrEmpty(folder))
            return;

        SelectedProjectDirectory = folder;
        AddToRecentDirectories(folder);
    }

    [RelayCommand]
    private void SelectRecentDirectory(string? directory)
    {
        if (string.IsNullOrEmpty(directory))
            return;

        SelectedProjectDirectory = directory;
        AddToRecentDirectories(directory);
    }

    [RelayCommand]
    private void ClearRecentDirectories()
    {
        RecentDirectories.Clear();
    }

    private void AddToRecentDirectories(string directory)
    {
        // 移除已存在的
        if (RecentDirectories.Contains(directory))
            RecentDirectories.Remove(directory);

        // 插入头部
        RecentDirectories.Insert(0, directory);

        // 裁剪超过10个
        while (RecentDirectories.Count > 10)
            RecentDirectories.RemoveAt(RecentDirectories.Count - 1);
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task Save()
    {
        if (string.IsNullOrEmpty(SelectedProjectDirectory))
        {
            StatusMessage = "请选择项目目录";
            return;
        }

        // 校验目录存在
        if (!Directory.Exists(SelectedProjectDirectory))
        {
            StatusMessage = $"目录不存在：{SelectedProjectDirectory}";
            return;
        }

        IsSaving = true;
        StatusMessage = null;

        try
        {
            // 模拟保存
            await Task.Delay(500);

            // 刷新快照
            _lastSavedDirectory = SelectedProjectDirectory;
            IsDirty = false;
            StatusMessage = "已保存";
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存失败：{ex.Message}";
        }
        finally
        {
            IsSaving = false;
        }
    }

    private bool CanSave() => IsDirty && !IsSaving && !string.IsNullOrEmpty(SelectedProjectDirectory);
}
