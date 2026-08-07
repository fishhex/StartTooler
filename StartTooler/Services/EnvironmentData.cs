namespace StartTooler.Services;

/// <summary>
/// v0.12: 拍摄环境数据——地点 + 天气。供 DiaryViewModel 在日记页编辑时回填。
/// </summary>
public sealed class EnvironmentData
{
    /// <summary>地点（行政区域级，如"浙江省 杭州市 临安区"）。</summary>
    public string Location { get; init; } = "";

    public WeatherData? Weather { get; init; }

    public bool HasAny => !string.IsNullOrEmpty(Location) || Weather != null;
}