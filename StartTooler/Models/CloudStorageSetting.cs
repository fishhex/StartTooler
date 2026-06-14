using System;
using SQLite;

namespace StartTooler.Models;

/// <summary>
/// 云存储提供商类型（支持扩展）
/// </summary>
public enum CloudStorageProvider
{
    AliyunOss = 1,
    // AwsS3 = 2,
    // TencentCos = 3,
}

[Table("CloudStorageSettings")]
public class CloudStorageSetting
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    /// <summary>
    /// 云存储提供商类型
    /// </summary>
    public int Provider { get; set; } = (int)CloudStorageProvider.AliyunOss;

    /// <summary>
    /// 访问密钥 ID (AccessKey ID)
    /// </summary>
    public string AccessKeyId { get; set; } = string.Empty;

    /// <summary>
    /// 访问密钥 Secret (AccessKey Secret)
    /// </summary>
    public string AccessKeySecret { get; set; } = string.Empty;

    /// <summary>
    /// 存储桶名称
    /// </summary>
    public string BucketName { get; set; } = string.Empty;

    /// <summary>
    /// 区域节点 (Endpoint)
    /// </summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>
    /// 默认存储目录
    /// </summary>
    public string Dir { get; set; } = string.Empty;

    /// <summary>
    /// 扩展字段（JSON 格式，用于未来新增提供商的自定义配置）
    /// </summary>
    public string ExtraConfig { get; set; } = string.Empty;

    /// <summary>
    /// 是否启用该配置
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    public DateTime UpdatedTime { get; set; } = DateTime.Now;
}
