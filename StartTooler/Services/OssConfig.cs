namespace StartTooler.Services;

public class OssConfig
{
    public string Provider { get; set; } = "Aliyun";
    public string Endpoint { get; set; } = "";
    public string Bucket { get; set; } = "";
    public string AccessKeyId { get; set; } = "";
    public string AccessKeySecret { get; set; } = "";
    public string PathPrefix { get; set; } = "";
    public bool UseHttps { get; set; } = true;
    public bool EnableCdn { get; set; }
    public string CdnDomain { get; set; } = "";
}
