using CommunityToolkit.Mvvm.ComponentModel;
using StartTooler.Models;
using StartTooler.Services;

namespace StartTooler.ViewModels;

public partial class CloudStorageSettingsViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _accessKeyId = string.Empty;

    [ObservableProperty]
    private string _accessKeySecret = string.Empty;

    [ObservableProperty]
    private string _bucketName = string.Empty;

    [ObservableProperty]
    private string _endpoint = string.Empty;

    public CloudStorageSettingsViewModel()
    {
        LoadSettings();
    }

    private void LoadSettings()
    {
        var setting = DatabaseService.Instance.GetCloudStorageSetting(CloudStorageProvider.AliyunOss);
        if (setting != null)
        {
            AccessKeyId = setting.AccessKeyId;
            AccessKeySecret = setting.AccessKeySecret;
            BucketName = setting.BucketName;
            Endpoint = setting.Endpoint;
        }
    }

    public void Save()
    {
        var setting = new CloudStorageSetting
        {
            Provider = (int)CloudStorageProvider.AliyunOss,
            AccessKeyId = AccessKeyId,
            AccessKeySecret = AccessKeySecret,
            BucketName = BucketName,
            Endpoint = Endpoint
        };

        DatabaseService.Instance.SaveCloudStorageSetting(setting);
    }
}
