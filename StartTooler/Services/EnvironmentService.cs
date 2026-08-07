using System;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StartTooler.Converters;

namespace StartTooler.Services;

/// <summary>
/// v0.12: 拍摄环境服务 —— 逆地理编码（高德/Nominatim）+ 历史天气（Open-Meteo Archive）。
/// 所有 API 在失败/超时/空数据时返回 null，由调用方决定是否提示用户。
///
/// 依赖：
/// - HttpClient（DI 注入，Timeout=5s）
/// - IConfigService（读取 AmapApiKey）
/// </summary>
public class EnvironmentService
{
    private readonly HttpClient _http;
    private readonly IConfigService _configService;

    // Nominatim 1 req/s 限速——全局串行化
    private static readonly SemaphoreSlim _nominatimGate = new(1, 1);

    public EnvironmentService(HttpClient http, IConfigService configService)
    {
        _http = http;
        _configService = configService;
    }

    /// <summary>
    /// 逆地理编码：经纬度 → 中文行政区划级地名。
    /// 优先级：高德 API（有 Key 时）→ Nominatim（免费回退）。
    /// </summary>
    public async Task<string?> ReverseGeocodeAsync(double lat, double lon, CancellationToken ct = default)
    {
        var amapKey = await GetAmapKeyAsync();
        if (!string.IsNullOrWhiteSpace(amapKey))
        {
            return await ReverseGeocodeAmapAsync(lat, lon, amapKey, ct);
        }
        return await ReverseGeocodeNominatimAsync(lat, lon, ct);
    }

    /// <summary>
    /// 获取历史天气：经纬度 + 日期 → WindDir/WindLevel/CloudCover。
    /// 使用 Open-Meteo Archive API（免费，无 Key）。
    /// </summary>
    public async Task<WeatherData?> GetHistoricalWeatherAsync(double lat, double lon, DateTime date, CancellationToken ct = default)
    {
        var url = $"https://archive-api.open-meteo.com/v1/archive" +
                  $"?latitude={lat.ToString(CultureInfo.InvariantCulture)}" +
                  $"&longitude={lon.ToString(CultureInfo.InvariantCulture)}" +
                  $"&start_date={date:yyyy-MM-dd}&end_date={date:yyyy-MM-dd}" +
                  $"&daily=wind_direction_10m_dominant,wind_speed_10m_max,cloud_cover_mean" +
                  $"&timezone=Asia%2FShanghai" +
                  $"&wind_speed_unit=ms";

        try
        {
            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("daily", out var daily)) return null;

            // Open-Meteo 返回数组形式，单元素数组（一天）
            if (!daily.TryGetProperty("wind_direction_10m_dominant", out var wdArr)) return null;
            if (!daily.TryGetProperty("wind_speed_10m_max", out var wsArr)) return null;
            if (!daily.TryGetProperty("cloud_cover_mean", out var ccArr)) return null;

            if (wdArr.GetArrayLength() == 0 || wsArr.GetArrayLength() == 0 || ccArr.GetArrayLength() == 0)
                return null;

            var windDeg = wdArr[0].ValueKind == JsonValueKind.Null ? (double?)null : wdArr[0].GetDouble();
            var windSpeedMs = wsArr[0].ValueKind == JsonValueKind.Null ? (double?)null : wsArr[0].GetDouble();
            var cloudCover = ccArr[0].ValueKind == JsonValueKind.Null ? (double?)null : ccArr[0].GetDouble();

            return new WeatherData
            {
                WindDir = WindDegreeToDirection(windDeg),
                WindLevel = WindSpeedMsToBeaufort(windSpeedMs ?? 0),
                CloudCover = CloudCoverPercentToText(cloudCover ?? 0),
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// 一站式：从 EXIF GPS → 解析地点 + 天气。任一步失败 → 该字段保持空。
    /// 无 GPS → 返回 null。
    /// </summary>
    public async Task<EnvironmentData?> FetchAsync(string? imagePath, DateTime date, CancellationToken ct = default)
    {
        var gps = ExifReader.ReadGps(imagePath);
        if (gps == null) return null;

        var lat = gps.Value.Latitude;
        var lon = gps.Value.Longitude;

        // 并发执行两路 API
        var locationTask = ReverseGeocodeAsync(lat, lon, ct);
        var weatherTask = GetHistoricalWeatherAsync(lat, lon, date, ct);

        await Task.WhenAll(locationTask, weatherTask);

        var location = locationTask.Result;
        var weather = weatherTask.Result;

        if (string.IsNullOrEmpty(location) && weather == null) return null;

        return new EnvironmentData
        {
            Location = location ?? "",
            Weather = weather,
        };
    }

    // === 私有 ===

    private async Task<string?> GetAmapKeyAsync()
    {
        var key = await _configService.GetAsync<string>(ConfigKeys.DiaryAmapApiKey);
        return string.IsNullOrWhiteSpace(key) ? null : key.Trim();
    }

    private async Task<string?> ReverseGeocodeAmapAsync(double lat, double lon, string apiKey, CancellationToken ct)
    {
        // 高德文档: https://lbs.amap.com/api/webservice/guide/api/georegeo
        var url = $"https://restapi.amap.com/v3/geocode/reverse" +
                  $"?key={Uri.EscapeDataString(apiKey)}" +
                  $"&location={lon.ToString(CultureInfo.InvariantCulture)},{lat.ToString(CultureInfo.InvariantCulture)}" +
                  $"&radius=1000&extensions=base";

        try
        {
            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            // status=1 表示成功
            if (!doc.RootElement.TryGetProperty("status", out var status)) return null;
            if (status.GetString() != "1") return null;

            if (!doc.RootElement.TryGetProperty("regeocode", out var regeocode)) return null;
            if (!regeocode.TryGetProperty("addressComponent", out var ac)) return null;

            var parts = new[]
            {
                GetJsonString(ac, "province"),
                GetJsonString(ac, "city"),
                GetJsonString(ac, "district"),
                GetJsonString(ac, "township"),
            };

            var sb = new System.Text.StringBuilder();
            foreach (var p in parts)
            {
                if (string.IsNullOrEmpty(p)) continue;
                // 直辖市：province 与 city 重复
                if (sb.Length > 0 && sb.ToString().EndsWith(p, StringComparison.Ordinal)) continue;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(p);
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private async Task<string?> ReverseGeocodeNominatimAsync(double lat, double lon, CancellationToken ct)
    {
        await _nominatimGate.WaitAsync(ct);
        try
        {
            // Nominatim 政策: 1 req/s + User-Agent 必填
            await Task.Delay(1000, ct);

            var url = $"https://nominatim.openstreetmap.org/reverse" +
                      $"?lat={lat.ToString(CultureInfo.InvariantCulture)}" +
                      $"&lon={lon.ToString(CultureInfo.InvariantCulture)}" +
                      $"&format=jsonv2&accept-language=zh-CN&zoom=14&addressdetails=1";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            // User-Agent 由构造 HttpClient 时统一设置；Nominatim 也接受 Referer
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("display_name", out var dn)) return null;
            var displayName = dn.GetString();
            return string.IsNullOrEmpty(displayName) ? null : displayName;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return null;
        }
        finally
        {
            _nominatimGate.Release();
        }
    }

    private static string? GetJsonString(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var prop)) return null;
        return prop.ValueKind == JsonValueKind.Null ? null : prop.GetString();
    }

    // === 转换表 ===

    /// <summary>风向度数（0-360，正北=0）→ 8 方位中文。</summary>
    private static string WindDegreeToDirection(double? deg)
    {
        if (deg == null) return "";
        var d = ((deg.Value % 360) + 360) % 360;
        // 22.5°间隔映射
        return d switch
        {
            < 22.5 or >= 337.5 => "北",
            < 67.5 => "东北",
            < 112.5 => "东",
            < 157.5 => "东南",
            < 202.5 => "南",
            < 247.5 => "西南",
            < 292.5 => "西",
            _ => "西北",
        };
    }

    /// <summary>风速 (m/s) → 蒲福风级 0-12（陆地版）。</summary>
    private static int WindSpeedMsToBeaufort(double ms)
    {
        if (ms < 0.3) return 0;
        if (ms < 1.6) return 1;
        if (ms < 3.4) return 2;
        if (ms < 5.5) return 3;
        if (ms < 8.0) return 4;
        if (ms < 10.8) return 5;
        if (ms < 13.9) return 6;
        if (ms < 17.2) return 7;
        if (ms < 20.8) return 8;
        if (ms < 24.5) return 9;
        if (ms < 28.5) return 10;
        if (ms < 32.7) return 11;
        return 12;
    }

    /// <summary>云量百分比 → 5 档中文（与 Session.CloudCover 字符串保持一致）。</summary>
    private static string CloudCoverPercentToText(double percent)
    {
        var p = Math.Clamp(percent, 0, 100);
        if (p <= 10) return "晴";
        if (p <= 30) return "少云";
        if (p <= 70) return "多云";
        if (p <= 99) return "阴";
        return "阴"; // 100% 也归"阴"，避免 5 档变 6 档
    }
}