# S03 Phase 3 — 服务层实现计划

> 关联：`spec/03-shooting-diary.md`（主规格）、`spec/03-shooting-diary-code-structure.md`（§3 服务层设计）、`doc/0.11/demand/08-shooting-session.md`（环境 API 复用）

---

## 目标

新建 3 个服务类：

1. `SessionClusteringService` — 按时间间隔聚类生成 Session
2. `EnvironmentService` — 逆地理编码 + 历史天气 API
3. `ExifReader` 扩展 — 解析 EXIF GPS 坐标

---

## Step 1: Converters/ExifInfoConverter.cs 扩展（GPS 解析）

**文件：** `StartTooler/Converters/ExifInfoConverter.cs`（修改）

### 1.1 ExifData 新增字段

```csharp
/// <summary>GPS 纬度，十进制度（-90..90）。正 = 北纬。</summary>
public double? GpsLatitude { get; set; }

/// <summary>GPS 经度，十进制度（-180..180）。正 = 东经。</summary>
public double? GpsLongitude { get; set; }

/// <summary>是否携带 GPS 坐标。UI 便捷属性。</summary>
public bool HasGps => GpsLatitude.HasValue && GpsLongitude.HasValue;
```

### 1.2 ExifReader 新增方法签名

```csharp
/// <summary>
/// 解析 GPS 坐标（十进制度）。失败/无 GPS → null。
/// 与 Read() 独立——Read() 解析图像元数据，ReadGps() 仅解析位置。
/// </summary>
public static (double Latitude, double Longitude)? ReadGps(string? path);
```

### 1.3 实现要点

**GPS 解析流程：**

```
1. 打开文件，定位到 TIFF header 偏移（Exif 头部固定 0x000E）
2. 检查 byte order: 0x4949 (little-endian) / 0x4D4D (big-endian)
3. 读取 IFD0 偏移，跳转到 IFD0
4. 遍历 IFD0 条目，寻找 tag 0x8825 (GPS IFD Pointer)
5. 跳转到 GPS IFD，遍历条目：
   - 0x0001 GPSLatitudeRef     → "N" / "S"
   - 0x0002 GPSLatitude        → 3 个 RATIONAL（度分秒）
   - 0x0003 GPSLongitudeRef    → "E" / "W"
   - 0x0004 GPSLongitude       → 3 个 RATIONAL
6. 度分秒 → 十进制：d + m/60 + s/3600
7. Ref 为 S/W 时取负
```

**关键技术点：**

- `ExifTag.GpsIFDOffset` (.NET 内置枚举) → 定位 GPS 子 IFD
- RATIONAL = 8 字节（前 4 字节分子，后 4 字节分母，需按 byte order 解码）
- 容错：任一字段缺失/损坏 → 返回 null，不抛异常
- GPS 解析在文件打开后使用与 `Read()` 相同的 `BinaryReader`，复用 byte order 检测

**复用现有 ExifReader 结构：**

- 现有 `Read(string? path)` 方法已有 TIFF 头解析、byte order 检测
- 提取共用 helper：`(BinaryReader, bool isBigEndian)` 让 Read 和 ReadGps 复用
- 如重构成本过高，可在 ReadGps 内**重复一份** header 解析逻辑（~40 行可接受）

---

## Step 2: Services/WeatherData.cs + EnvironmentData.cs

**文件：** `StartTooler/Services/WeatherData.cs`（新建）、`StartTooler/Services/EnvironmentData.cs`（新建）

### 2.1 WeatherData

```csharp
namespace StartTooler.Services;

/// <summary>
/// 历史天气数据（Open-Meteo Archive API 字段子集）。
/// </summary>
public sealed class WeatherData
{
    /// <summary>风向，8 方位："东"/"西"/"南"/"北"/"东南"等。空 = 未知。</summary>
    public string WindDir { get; init; } = "";

    /// <summary>风级（蒲福风级 0-12）。0 = 静风。</summary>
    public int WindLevel { get; init; }

    /// <summary>云量文字："晴"/"少云"/"多云"/"阴"/"雨"/"雪"/"雾"。</summary>
    public string CloudCover { get; init; } = "";
}
```

### 2.2 EnvironmentData

```csharp
namespace StartTooler.Services;

/// <summary>
/// 拍摄环境数据：地点 + 天气。
/// </summary>
public sealed class EnvironmentData
{
    public string Location { get; init; } = "";
    public WeatherData? Weather { get; init; }

    public bool HasAny => !string.IsNullOrEmpty(Location) || Weather != null;
}
```

### 2.3 实现要点

- `WindDir` / `CloudCover` 与 `Session` 模型一致（同一组字符串字面量）
- `WeatherData.CloudCover` 与 `Session.CloudCover` 字面量保持一致，便于 XAML 直接绑定 `WeatherIconKey`

---

## Step 3: Services/EnvironmentService.cs

**文件：** `StartTooler/Services/EnvironmentService.cs`（新建）

### 3.1 构造函数

```csharp
public EnvironmentService(HttpClient http, IConfigService configService)
{
    _http = http;
    _configService = configService;
}
```

### 3.2 公开方法

```csharp
/// <summary>
/// 逆地理编码：经纬度 → 地名（中国行政区划级）。
/// 优先级：高德 API（有 Key 时） → Nominatim（免费，回退）。
/// </summary>
public async Task<string?> ReverseGeocodeAsync(
    double lat, double lon, CancellationToken ct = default);

/// <summary>
/// 获取历史天气：经纬度 + 日期 → WindDir/WindLevel/CloudCover。
/// 使用 Open-Meteo Archive API（免费，无需 Key）。
/// </summary>
public async Task<WeatherData?> GetHistoricalWeatherAsync(
    double lat, double lon, DateTime date, CancellationToken ct = default);

/// <summary>
/// 一站式：从 EXIF 提取 GPS → 解析地点 → 解析天气。
/// 任一步失败 → 返回已有部分（非 null）。
/// </summary>
public async Task<EnvironmentData?> FetchAsync(
    string? imagePath, DateTime date, CancellationToken ct = default);
```

### 3.3 配置键

```csharp
private const string KeyAmapApiKey = "Diary.AmapApiKey";

/// <summary>用户设置中的高德 API Key。空 = 走 Nominatim 回退。</summary>
private async Task<string?> GetAmapKeyAsync()
{
    return await _configService.GetAsync<string>(KeyAmapApiKey);
}
```

### 3.4 ReverseGeocodeAsync 实现

**A. 高德优先路径（Key 存在时）：**

```
GET https://restapi.amap.com/v3/geocode/reverse
    ?key={API_KEY}
    &location={lon},{lat}
    &radius=1000
    &extensions=base
```

**响应字段：** `regeocode.addressComponent.province + city + district + township`，拼接为 "浙江省 杭州市 临安区 锦城街道"

**B. Nominatim 回退路径（无 Key 时）：**

```
GET https://nominatim.openstreetmap.org/reverse
    ?lat={lat}&lon={lon}
    &format=jsonv2
    &accept-language=zh-CN
    &zoom=14
    &addressdetails=1
```

**响应字段：** `display_name`，返回完整地址字符串

**限速：** Nominatim 要求 1 req/s，需在请求间 Sleep 1000ms（用 SemaphoreSlim 全局串行化）

### 3.5 GetHistoricalWeatherAsync 实现

**API 端点：**

```
GET https://archive-api.open-meteo.com/v1/archive
    ?latitude={lat}&longitude={lon}
    &start_date={YYYY-MM-DD}&end_date={YYYY-MM-DD}
    &daily=wind_direction_10m_dominant,wind_speed_10m_max,cloud_cover_mean
    &timezone=Asia%2FShanghai
    &wind_speed_unit=ms
```

**响应字段映射：**

| Open-Meteo 字段 | 转换 | 目标字段 |
|---|---|---|
| `wind_direction_10m_dominant[0]` | 0°=北、22.5°间隔 → 8 方位 | `WindDir` |
| `wind_speed_10m_max[0]` | m/s → 蒲福风级表 | `WindLevel` |
| `cloud_cover_mean[0]` | 0/25/50/75/100 → "晴"/"少云"/"多云"/"阴"/"雨" | `CloudCover` |

**蒲福风级（陆地版，0-12）：**
- 0: <0.3 m/s（静风）
- 1: 0.3-1.5（软风）
- 2: 1.6-3.3（轻风）
- 3: 3.4-5.4（微风）
- 4: 5.5-7.9（和风）
- 5: 8.0-10.7（清劲风）
- 6: 10.8-13.8（强风）
- 7: 13.9-17.1（疾风）
- 8: 17.2-20.7（大风）
- 9: 20.8-24.4（暴风）
- 10: 24.5-28.4（强烈暴风）
- 11: 28.5-32.6（风暴）
- 12: ≥32.7（飓风）

**云量映射：**
- 0-10%: "晴"
- 11-30%: "少云"
- 31-70%: "多云"
- 71-99%: "阴"
- ≥99.5% 且有降水历史 → "雨"（Open-Meteo 不直接给降水类型，可省略此条件）

### 3.6 FetchAsync 实现

```
1. ExifReader.ReadGps(imagePath) → (lat, lon) 或 null
2. null → 返回 null（无 GPS）
3. 并发执行：ReverseGeocodeAsync(lat, lon) || GetHistoricalWeatherAsync(lat, lon, date)
4. 任一失败 → 该字段保持空，但另一字段照常返回
5. 全部为空 → 返回 null；任一有值 → 返回 EnvironmentData
```

### 3.7 错误处理

- HttpClient 超时：5 秒（构造时设置 `Timeout = TimeSpan.FromSeconds(5)`）
- HTTP 4xx/5xx：catch HttpRequestException → 返回 null，不抛
- JSON 反序列化失败：catch JsonException → 返回 null
- Nominatim 限速：全局 `SemaphoreSlim(1, 1)` + Sleep 1000ms

### 3.8 实现要点

- HttpClient 通过 DI 注入（IHttpClientFactory 也可，但构造函数注入更直接）
- 不缓存结果（每次调用都查 API，限速由 SemaphoreSlim 处理）
- 日记页编辑场景：用户修改后下次 Fetch 不再覆盖手动输入（由 VM 层处理，不是 Service 职责）

---

## Step 4: Services/SessionClusteringService.cs

**文件：** `StartTooler/Services/SessionClusteringService.cs`（新建）

### 4.1 构造函数

```csharp
public SessionClusteringService(IMediaRepository mediaRepo, ISessionRepository sessionRepo)
{
    _mediaRepo = mediaRepo;
    _sessionRepo = sessionRepo;
}
```

### 4.2 公开方法

```csharp
/// <summary>
/// 对项目下所有"未关联会话"的照片按时间间隔聚类，生成/更新 Session。
/// - 已手动分配 session_id 的照片不会被覆盖
/// - 聚类后批量 UPDATE media_files.session_id
/// - 已有 Session 但未关联任何照片 → 保留（不删除，避免破坏手动编辑）
/// </summary>
/// <param name="projectPath">项目绝对路径</param>
/// <param name="intervalHours">切分间隔（小时），默认 4</param>
/// <returns>本轮新增/更新的 Session 列表</returns>
public async Task<IReadOnlyList<Session>> ClusterAsync(
    string projectPath, int intervalHours = 4, CancellationToken ct = default);
```

### 4.3 算法实现

**Step A：查询孤儿照片**

```sql
SELECT id, shot_at FROM media_files
WHERE project_path = @projectPath
  AND shot_at IS NOT NULL
  AND session_id IS NULL
  AND deleted_at IS NULL
ORDER BY shot_at ASC
```

**Step B：内存聚类（单趟扫描）**

```
current = null (Session in progress)
results = []
for each photo in photos:
    if current == null:
        current = new Session(photo.shot_at, photo.shot_at)
    else:
        gap = (photo.shot_at - current.EndTime).TotalHours
        if gap > intervalHours:
            results.Add(current)         // 提交上一个
            current = new Session(photo.shot_at, photo.shot_at)
        else:
            current.EndTime = photo.shot_at
    current.PhotoIds.Add(photo.id)
if current != null:
    results.Add(current)
```

**Step C：生成 Session 对象**

```csharp
var session = new Session
{
    Id = Guid.NewGuid().ToString("N"),
    ProjectPath = normalizedPath,
    Title = $"{startTime:yyyy-MM-dd} 出摊",
    StartTime = startTime,
    EndTime = endTime,
    CreatedAt = DateTime.UtcNow,
    UpdatedAt = DateTime.UtcNow,
};
```

**Step D：批量持久化**

```csharp
await _sessionRepo.UpsertBatchAsync(sessions, ct);
// UPDATE media_files SET session_id = @sid WHERE id IN (...)
```

**Step E：返回结果**

```csharp
return sessions;
```

### 4.4 关联照片写入

需要在 MediaRepository 新增批量更新方法：

```csharp
// IMediaRepository.cs 新增
Task SetSessionBatchAsync(
    IReadOnlyList<(long FileId, string SessionId)> assignments,
    CancellationToken ct = default);
```

**SQL 实现（事务 + 单条 UPDATE IN 模式）：**

```csharp
var groups = assignments.GroupBy(a => a.SessionId);
await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
foreach (var group in groups)
{
    var ids = group.Select(g => g.FileId).ToList();
    var placeholders = string.Join(",", ids.Select((_, i) => $"@id{i}"));
    var sql = $"UPDATE media_files SET session_id = @sid WHERE id IN ({placeholders})";
    // ...
}
await tx.CommitAsync(ct);
```

### 4.5 边界情况

| 场景 | 处理 |
|---|---|
| 没有孤儿照片 | 返回空 list |
| 全部照片同一天且间隔 < 4h | 聚为 1 个 session |
| 跨天但间隔 < 4h | 仍聚为 1 个 session（间隔 ≠ 跨天） |
| 单张照片（孤立） | 聚为 1 个 session（Start = End = 该时间） |
| 已有 session 但有新加入 | 不重算（ClusterAsync 只处理孤儿） |

### 4.6 实现要点

- 时间戳使用 `DateTimeOffset.FromUnixTimeMilliseconds(shot_at).LocalDateTime`
- Session 标题生成：暂固定 `{yyyy-MM-dd} 出摊`（后续可改为基于首张照片的拍摄目标）
- 不修改已有 Session（除 Photos 通过"孤儿优先"策略自然汇聚）

---

## Step 5: IMediaRepository 扩展（批量 Session 分配）

**文件：** `StartTooler/Data/IMediaRepository.cs`（修改）

```csharp
/// <summary>
/// 批量设置照片所属 Session（用于聚类结果写入）。
/// </summary>
Task SetSessionBatchAsync(
    IReadOnlyList<(long FileId, string SessionId)> assignments,
    CancellationToken ct = default);
```

**文件：** `StartTooler/Data/MediaRepository.cs`（修改）

按 §4.4 实现，分组 UPDATE + 事务。

---

## Step 6: App.axaml.cs DI 注册

**文件：** `StartTooler/App.axaml.cs`（修改）

在现有服务注册块中添加：

```csharp
services.AddSingleton<SessionClusteringService>();
services.AddSingleton<EnvironmentService>();
```

HttpClient 需要单独注册：

```csharp
services.AddSingleton(sp => new HttpClient
{
    Timeout = TimeSpan.FromSeconds(5),
    DefaultRequestHeaders = { { "User-Agent", "StartTooler/0.12" } }
});
```

**检查现有 DI 模式：** 需要先 grep `services.AddSingleton` 看现有注册风格，保持一致。

---

## Step 7: 编译验证

### 7.1 验证清单

- [ ] `dotnet build` 0 错误 0 警告
- [ ] ExifReader.ReadGps 可在损坏/无 GPS 文件上安全返回 null
- [ ] EnvironmentService 在无 Key 时自动走 Nominatim 回退
- [ ] SessionClusteringService 处理空集合不抛异常
- [ ] IMediaRepository.SetSessionBatchAsync 事务正确提交/回滚

### 7.2 编译命令

```bash
dotnet build StartTooler/StartTooler.csproj
```

---

## 文件变更清单

| 操作 | 文件 | 行数(估) |
|------|------|----------|
| 修改 | `Converters/ExifInfoConverter.cs` | +60（GPS 字段 + ReadGps） |
| 新建 | `Services/WeatherData.cs` | ~15 |
| 新建 | `Services/EnvironmentData.cs` | ~15 |
| 新建 | `Services/EnvironmentService.cs` | ~250 |
| 新建 | `Services/SessionClusteringService.cs` | ~150 |
| 修改 | `Data/IMediaRepository.cs` | +8 |
| 修改 | `Data/MediaRepository.cs` | +50 |
| 修改 | `App.axaml.cs` | +5 |

---

## 依赖关系

```
SessionClusteringService ──→ IMediaRepository, ISessionRepository
EnvironmentService ────────→ HttpClient, IConfigService
ExifReader.ReadGps ────────→ (无依赖，纯 I/O)
```

无 ViewModel 依赖，本 Phase 后可独立测试服务逻辑（Phase 5 接入 UI）。

---

## 关键设计决策

1. **逆地理编码：高德 + Nominatim 双路径**
   - 高德 Key 在设置中配置（Phase 4），有 Key → 精度高、限速低
   - 无 Key → Nominatim 回退，免费但 1 req/s 限速

2. **天气：仅用 Open-Meteo**
   - 无 Key 需求，Archive API 免费
   - 不支持国内气象局数据源（避免 Key 复杂度）

3. **聚类算法：单趟扫描**
   - 已分组的照片不动，只处理"孤儿"
   - 多次运行幂等（孤儿每次都被处理，但已分配的不变）

4. **GPS 解析：ExifReader.ReadGps 独立方法**
   - 避免在已有 Read() 中添加分支
   - 文件 I/O 共享：可在调用 Read() 路径上加缓存，但暂不实现（聚类时每张照片解析一次可接受）

5. **错误处理：返回 null 而非抛异常**
   - 网络失败、API 限速、JSON 损坏 → null
   - 上层（VM）决定是否提示用户或使用缓存值