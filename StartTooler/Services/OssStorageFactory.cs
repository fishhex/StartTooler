using System;
using StartTooler.Services;

namespace StartTooler.Services;

/// <summary>
/// IOssStorage 工厂。OssConfig 在 Settings 加载前为空 / 加载后才完整，
/// 所以采用工厂延迟构造；调用方拿到的永远是「最新配置」对应的实例。
///
/// 当前唯一实现：AliyunOssStorage。未来加腾讯云 / S3 时切换实现即可。
/// </summary>
public interface IOssStorageFactory
{
    /// <summary>
    /// 根据当前配置返回一个 IOssStorage 实例。配置不完整时返回 null，
    /// 调用方负责 fallback（提示用户去 Settings 配 OSS）。
    /// </summary>
    IOssStorage? TryCreate();

    /// <summary>
    /// 当前配置是否已就绪（四个字段都非空）。
    /// </summary>
    bool IsConfigured(OssConfig config);
}

public sealed class OssStorageFactory : IOssStorageFactory
{
    private readonly Func<OssConfig> _configProvider;

    /// <param name="configProvider">返回当前最新的 OssConfig（如从 ConfigService 读）。</param>
    public OssStorageFactory(Func<OssConfig> configProvider)
    {
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
    }

    public IOssStorage? TryCreate()
    {
        var config = _configProvider();
        if (!IsConfigured(config)) return null;

        // 目前固定 Aliyun；未来按 config.Provider 分发
        return config.Provider switch
        {
            "Aliyun" or "" or null => new AliyunOssStorage(config),
            _ => throw new NotSupportedException($"OSS Provider '{config.Provider}' 暂未实现"),
        };
    }

    public bool IsConfigured(OssConfig config)
    {
        if (config == null) return false;
        return !string.IsNullOrWhiteSpace(config.Region)
            && !string.IsNullOrWhiteSpace(config.Bucket)
            && !string.IsNullOrWhiteSpace(config.AccessKeyId)
            && !string.IsNullOrWhiteSpace(config.AccessKeySecret);
    }
}
