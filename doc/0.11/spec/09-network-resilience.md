# 0.11 — 网络感知、OSS 重试策略 & 磁盘空间预警

> 对应需求量文档 `doc/0.11/demand/07-general-improve.md` §「网络与离线」+「磁盘空间」。
> 核心改动：网络状态监控服务、OSS 操作按钮离线禁用、上传失败自动重试（指数退避）、下载前磁盘空间检查、状态栏空间显示。

---

## 1. 模块边界

```
┌─────────────────────────────────────────────────────┐
│  NetworkStatusService (新增)                         │
│  ├─ 定时 Ping aliyuncs.com / 检测网卡状态             │
│  ├─ NetworkStatusChanged 事件                        │
│  ├─ IsOnline 属性                                   │
│  └─ 订阅方：MainWindow 状态栏 / OSS 操作按钮         │
├─────────────────────────────────────────────────────┤
│  OSS 重试 (AliyunOssStorage.cs 修改)                 │
│  ├─ 瞬时错误 → 重试 3 次 (1s / 3s / 9s)             │
│  ├─ 永久错误 → 立即失败                              │
│  └─ photo-tile 旋转箭头图标（上传重试中）             │
├─────────────────────────────────────────────────────┤
│  磁盘空间 (DiskSpaceService.cs 新增)                  │
│  ├─ 批量下载前检查剩余空间                           │
│  ├─ 下载中动态更新剩余空间                           │
│  └─ 状态栏显示可用空间                               │
└─────────────────────────────────────────────────────┘
```

---

## 2. 新增/修改文件清单

| 文件 | 用途 | 类型 |
|------|------|------|
| `Services/NetworkStatusService.cs` | 网络在线/离线检测 + Ping 探活 | 新增 |
| `Services/DiskSpaceService.cs` | 磁盘空间查询 + 批量操作预警 | 新增 |
| `Services/AliyunOssStorage.cs` | 添加重试策略 + 网络感知 | 修改 |
| `ViewModels/MainWindowViewModel.cs` | 状态栏集成：网络指示器 + 磁盘空间 | 修改 |
| `Views/MainWindow.axaml` | 状态栏 UI：在线/离线 + 磁盘空间 | 修改 |
| `Views/GalleryView.axaml` | photo-tile 右上角重试箭头图标 | 修改 |
| `Themes/Icons.axaml` | 新增 RotateArrow / WifiOn / WifiOff 图标 | 修改 |

---

## 3. NetworkStatusService

### 3.1 接口

```csharp
namespace StartTooler.Services;

public class NetworkStatusService : IDisposable
{
    /// <summary>当前是否在线（有可用网络 + 能连通 OSS 端点）</summary>
    public bool IsOnline { get; private set; }

    /// <summary>网络状态变化事件（UI 线程）</summary>
    public event Action<bool>? NetworkStatusChanged;

    /// <summary>启动周期性检测</summary>
    public void Start(TimeSpan checkInterval);

    /// <summary>手动立即检测一次</summary>
    public async Task<bool> CheckNowAsync(CancellationToken ct = default);

    public void Dispose(); // 停止 Timer
}
```

### 3.2 检测策略

```
检测流程（每 30 秒）:
  ├─ Step 1: 检查 NetworkInterface 是否有活跃连接
  │    └─ 无 → 离线，不浪费网络请求
  ├─ Step 2: HTTP HEAD https://oss-cn-hangzhou.aliyuncs.com/ (超时 5s)
  │    └─ 成功 → 在线
  │    └─ 失败 → 用配置的 OSS Endpoint 重试一次
  ├─ Step 3: 两次都失败 → 离线
  └─ 状态变化 → 触发 NetworkStatusChanged
```

> **设计决策**：不依赖 `NetworkChange.NetworkAvailabilityChanged`（.NET 事件在 macOS 上不可靠）。改用定时 Ping + 网卡检测组合。30 秒间隔足够响应网络变化，不对 OSS 造成压力。

### 3.3 启动与生命周期

```csharp
// App.axaml.cs OnFrameworkInitializationCompleted:
_networkStatusService = new NetworkStatusService(ossEndpoint);
_networkStatusService.NetworkStatusChanged += OnNetworkChanged;
_networkStatusService.Start(TimeSpan.FromSeconds(30));

// 析构
// App.OnExit → _networkStatusService.Dispose()
```

---

## 4. OSS 离线处理

### 4.1 操作按钮禁用

在 `MainWindow` / `GalleryViewModel` 中监听 `NetworkStatusChanged`：

```csharp
private void OnNetworkChanged(bool isOnline)
{
    Dispatcher.UIThread.Post(() =>
    {
        IsOnline = isOnline;
        // 绑定到所有 OSS 操作按钮的 IsEnabled
        // 离线时 tooltip 显示 "无网络连接"
    });
}
```

**受影响的按钮/操作**：
- 上传按钮 → `IsEnabled="{Binding IsOnline}"`，tooltip "无网络连接"
- 下载按钮 → 同上
- OSS 配置「测试连接」→ 离线时禁用
- 「同步状态」刷新 → 离线时禁用

### 4.2 离线队列

上传任务在离线时仍可加入队列，状态标记为 `Queued`（而非直接 Failed）：

```csharp
// UploadJobRepository
public enum UploadJobStatus
{
    Queued,         // 等待上传（包含离线等待）
    Uploading,      // 上传中
    Completed,      // 完成
    Failed,         // 失败（永久错误）
    Retrying,       // 重试中（新增 ←）
}
```

网络恢复后：
1. `NetworkStatusChanged(true)` 触发
2. 检查 `UploadJob` 表中 `Status == Queued` 的任务
3. Toast：「网络已恢复，检测到 N 个待上传文件，是否继续？」[是] [稍后]

---

## 5. OSS 重试策略

### 5.1 错误分类

| 错误类型 | HTTP 状态码 | 重试？ | 退避策略 |
|---|---|---|---|
| 瞬时网络错误 | `IOException` / `TimeoutException` / `SocketException` | ✅ 重试 | 指数退避 1s/3s/9s |
| 限流 | `429` / `503` (ServiceUnavailable) | ✅ 重试 | 指数退避 3s/9s/27s |
| 永久错误 | `403` (Forbidden) / `404` (NotFound) / `400` (BadRequest) | ❌ 直接 Failed | — |
| 鉴权失败 | `403` + SignatureDoesNotMatch | ❌ 直接 Failed + toast "OSS 密钥错误" | — |

### 5.2 实现（AliyunOssStorage.cs）

```csharp
private async Task<T> ExecuteWithRetryAsync<T>(
    Func<Task<T>> operation,
    string operationName,
    CancellationToken ct)
{
    const int maxRetries = 3;
    int delaySeconds = 1;

    for (int attempt = 0; attempt <= maxRetries; attempt++)
    {
        try
        {
            if (attempt > 0)
            {
                Trace.WriteLine($"[OSS] {operationName} 第 {attempt} 次重试...");
            }
            return await operation();
        }
        catch (Exception ex)
        {
            // 永久错误 → 不重试
            if (IsPermanentError(ex))
            {
                Trace.WriteLine($"[OSS] {operationName} 永久错误: {ex.Message}");
                throw;
            }

            // 最后一次尝试 → 抛出
            if (attempt == maxRetries)
            {
                Trace.WriteLine($"[OSS] {operationName} 重试 {maxRetries} 次后仍失败");
                throw;
            }

            // 瞬时错误 → 退避重试
            Trace.WriteLine($"[OSS] {operationName} 瞬时错误，{delaySeconds}s 后重试: {ex.Message}");
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct);
            delaySeconds *= 3; // 1 → 3 → 9
        }
    }

    throw new InvalidOperationException("Unreachable");
}

private static bool IsPermanentError(Exception ex)
{
    return ex is OssException ossEx
        && (ossEx.ErrorCode?.Contains("InvalidAccessKey") == true
            || ossEx.ErrorCode?.Contains("SignatureDoesNotMatch") == true
            || ossEx.ErrorCode?.Contains("NoSuchBucket") == true
            || ossEx.ErrorCode?.Contains("NoSuchKey") == true
            || ossEx.ErrorCode?.Contains("AccessDenied") == true);
}
```

### 5.3 photo-tile 重试中图标

重试中（`Status == Retrying`）时，photo-tile 右上角显示旋转箭头：

```xml
<!-- GalleryView.axaml photo-tile template -->
<Path Data="{StaticResource Icon.RotateArrow}"
      IsVisible="{Binding IsRetrying}"
      Foreground="{DynamicResource Text.Warning}"
      Width="16" Height="16">
    <Path.RenderTransform>
        <RotateTransform Angle="0" />
    </Path.RenderTransform>
    <Path.Animations>
        <Animation Duration="0:0:1" IterationCount="INFINITE">
            <KeyFrame KeyTime="0:0:0">
                <Setter Property="(Path.RenderTransform).(RotateTransform.Angle)" Value="0"/>
            </KeyFrame>
            <KeyFrame KeyTime="0:0:1">
                <Setter Property="(Path.RenderTransform).(RotateTransform.Angle)" Value="360"/>
            </KeyFrame>
        </Animation>
    </Path.Animations>
</Path>
```

---

## 6. 磁盘空间预警

### 6.1 DiskSpaceService

```csharp
namespace StartTooler.Services;

public class DiskSpaceService
{
    /// <summary>获取指定路径所在磁盘的可用空间（字节）</summary>
    public static long GetAvailableFreeSpace(string path)
    {
        var driveInfo = new DriveInfo(Path.GetPathRoot(path) ?? "/");
        return driveInfo.AvailableFreeSpace;
    }

    /// <summary>批量下载前检查空间</summary>
    /// <returns>true=空间充足, false=空间不足需要弹警告</returns>
    public static (bool Sufficient, string? Warning) CheckBeforeDownload(
        string targetDir,
        IEnumerable<(string Key, long Size)> filesToDownload)
    {
        var totalSize = filesToDownload.Sum(f => f.Size);
        var available = GetAvailableFreeSpace(targetDir);

        // 预留 200MB 缓冲
        var requiredSize = totalSize + 200L * 1024 * 1024;

        if (requiredSize > available * 0.9)
        {
            var warning = $"磁盘空间不足！预计需要 {FormatBytes(totalSize)}，" +
                          $"剩余仅 {FormatBytes(available)}。" +
                          "是否继续？";
            return (false, warning);
        }

        return (true, null);
    }

    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 40 => $"{bytes / (1L << 40):F1} TB",
        >= 1L << 30 => $"{bytes / (1L << 30):F1} GB",
        >= 1L << 20 => $"{bytes / (1L << 20):F1} MB",
        _ => $"{bytes / 1024} KB"
    };
}
```

### 6.2 状态栏磁盘显示

在 `MainWindow` 状态栏显示当前项目目录所在磁盘的可用空间：

```
┌─────────────────────────────────────────┐
│  14,256 张  │  🔵 在线  │  💾 123.5 GB  │
└─────────────────────────────────────────┘
```

更新频率：每次扫描/下载/删除操作完成后更新一次（不需要实时定时器）。

```csharp
// MainWindowViewModel.cs
[ObservableProperty] private string _diskSpaceText = "—";

private void UpdateDiskSpace()
{
    var projectPath = GalleryViewModel?.ProjectPath;
    if (string.IsNullOrEmpty(projectPath)) return;

    try
    {
        var free = DiskSpaceService.GetAvailableFreeSpace(projectPath);
        DiskSpaceText = DiskSpaceService.FormatBytes(free);
    }
    catch
    {
        DiskSpaceText = "—";
    }
}
```

### 6.3 下载中动态更新

批量下载每个文件完成后更新一次 `DiskSpaceText`：

```csharp
// 在 batch download loop 中
foreach (var file in filesToDownload)
{
    await DownloadOneAsync(file, ct);
    downloadedCount++;

    if (downloadedCount % 10 == 0) // 每 10 个文件更新一次
    {
        UpdateDiskSpace();
    }
}
```

---

## 7. 边界情况

| 场景 | 处理 |
|---|---|
| 网络检测 Ping 超时但实际网络正常 | 允许用户手动重试（OSS 操作按钮不锁定 Ping 状态，只锁定最近一次 CheckNow 结果） |
| OSS Endpoint 未配置 | `NetworkStatusService` 跳过 OSS Ping，仅靠网卡检测 |
| 重试 3 次后仍然失败 | 标记为 Failed，toast 提示「上传失败，请在网络恢复后重试」 |
| 下载中磁盘空间不足 | 已下载文件保留，未下载的取消。toast：「磁盘空间不足，已停止下载（已完成 N/M）」 |
| 项目目录在可移动磁盘上（如 U 盘/SD 卡） | 正常工作，但磁盘空间检测与本地一致 |
| 状态栏空间显示 N/A | 项目目录未设置时显示 `—` |
| 多个 OSS Bucket/Endpoint | 取配置中第一个 Endpoint 作为探活目标 |

---

## 8. 不做清单

| 内容 | 理由 |
|---|---|
| 网络速度/带宽测试 | OSS 上传/下载进度条已有，额外测速无增益 |
| 代理/VPN 配置 | 走系统代理设置，OS 层面处理 |
| 断点续传（分片上传中断恢复） | Aliyun OSS SDK 已内置 multipart 断点，不需要自实现 |
| 网络诊断工具（ping/traceroute UI） | 非用户需求 |
| 磁盘碎片/健康度检测 | 超出应用范围 |
