# 04 — 对象存储与上传

> 对应代码：`Services/IOssStorage.cs`、`Services/AliyunOssStorage.cs`、`Services/OssStorageFactory.cs`、`Services/OssConfig.cs`，以及 `ViewModels/GalleryViewModel.cs` 的上传/续传/下载方法。

---

## 1. 模块边界

```
GalleryViewModel.UploadSingle / BatchUpload / ResumeInterruptedAsync
  └─ OssStorageFactory.TryCreate()
        ├─ 配置不完整 → null → 弹「OSS 未配置」对话框（MainWindowVM 提供）
        └─ "Aliyun" / 空 → new AliyunOssStorage(config)
            ├─ UploadAsync  (单 PUT, <5MB)
            ├─ InitiateMultipartAsync → UploadPart × N → CompleteMultipart
            ├─ ListPartsAsync (续传核心)
            ├─ GetCoverUrlAsync (签名 URL)
            ├─ DownloadAsync (服务端凭据流式落盘)
            └─ AbortMultipartAsync (占位，未调用)
```

---

## 2. IOssStorage 接口（`Services/IOssStorage.cs`）

```csharp
public interface IOssStorage {
    Task<OssUploadResult> UploadAsync(string localPath, string objectKey, CancellationToken ct = default);
    Task<string> GetCoverUrlAsync(string objectKey, TimeSpan expiry, CancellationToken ct = default);
    Task DownloadAsync(string objectKey, string localPath, CancellationToken ct = default);
    long MultipartThresholdBytes { get; }   // 默认 5MB

    // Multipart（断点续传）
    Task<MultipartHandle> InitiateMultipartAsync(string objectKey, CancellationToken ct = default);
    Task<PartETag> UploadPartAsync(MultipartHandle handle, int partNumber, Stream data, long length, CancellationToken ct = default);
    Task<IReadOnlyList<PartETag>> ListPartsAsync(MultipartHandle handle, CancellationToken ct = default);
    Task CompleteMultipartAsync(MultipartHandle handle, IReadOnlyList<PartETag> parts, CancellationToken ct = default);
    Task AbortMultipartAsync(MultipartHandle handle, CancellationToken ct = default);
}

public sealed class MultipartHandle {
    public string ObjectKey { get; init; } = "";
    public string UploadId  { get; init; } = "";
    public int PartSize     { get; init; }
}

public sealed class PartETag {
    public int PartNumber { get; init; }
    public string ETag     { get; init; } = "";
}

public sealed class OssUploadResult {
    public string ObjectKey { get; init; } = "";
    public string? ETag     { get; init; }
    public bool Success     { get; init; }
    public string? Error    { get; init; }
}
```

### 2.1 接口设计要点

- **Provider-agnostic**：未来加腾讯云 COS / AWS S3 时只需加一个 `XxxOssStorage : IOssStorage`，`OssStorageFactory` 加 switch 分支
- **桶假定私有**：Upload 走服务端凭据直传，Download 走签名 URL，外部不能直链
- **Aliyun SDK 是同步阻塞**：所有方法在内部 `await Task.Run(() => _client.PutObject(...))` 让出线程（`AliyunOssStorage.cs:69-75`、`AliyunOssStorage.cs:166-170` 等）

---

## 3. OssConfig（`Services/OssConfig.cs`）

```csharp
public class OssConfig {
    public string Provider       { get; set; } = "Aliyun";
    public string Region         { get; set; } = "";
    public string Bucket         { get; set; } = "";
    public string AccessKeyId    { get; set; } = "";
    public string AccessKeySecret{ get; set; } = "";
    public string PathPrefix     { get; set; } = "";
}
```

| 字段 | 含义 |
|---|---|
| `Provider` | OSS 提供商（v0.1 硬编码 "Aliyun"，但 UI/Config 留口子） |
| `Region` | 阿里云 region（**不带** "oss-" 前缀，例如 `oss-cn-hangzhou`） |
| `Bucket` | 桶名 |
| `AccessKeyId/Secret` | 凭据（v0.1 明文，**待迁移到安全存储**） |
| `PathPrefix` | 对象 key 前缀（可空，留空走桶根，可加 `/`） |

**安全承认账**：`AliyunOssStorage.cs:20-22` 自承：v0.1 AccessKeySecret 是明文持久化在 `Config.db`，应迁移到 Keychain / DPAPI / 加密 Config（v0.2+）。

---

## 4. OssStorageFactory（`Services/OssStorageFactory.cs`）

### 4.1 延迟构造模式

```csharp
public sealed class OssStorageFactory : IOssStorageFactory {
    private readonly Func<OssConfig> _configProvider;

    public OssStorageFactory(Func<OssConfig> configProvider) { _configProvider = configProvider; }

    public IOssStorage? TryCreate() {
        var config = _configProvider();
        if (!IsConfigured(config)) return null;
        return config.Provider switch {
            "Aliyun" or "" or null => new AliyunOssStorage(config),
            _ => throw new NotSupportedException($"OSS Provider '{config.Provider}' 暂未实现"),
        };
    }

    public bool IsConfigured(OssConfig config) =>
        !string.IsNullOrWhiteSpace(config.Region) &&
        !string.IsNullOrWhiteSpace(config.Bucket) &&
        !string.IsNullOrWhiteSpace(config.AccessKeyId) &&
        !string.IsNullOrWhiteSpace(config.AccessKeySecret);
}
```

### 4.2 为什么用工厂

- 用户**先**进 Gallery（没配 OSS），**后**到 Settings 配 OSS —— `OssConfig` 在 Settings 加载前为空
- 工厂 provider 每次调用都从 `ConfigService` 读最新值 —— **永远拿到最新版**
- `TryCreate()` 返回 null 是 **正常状态**，UI 用弹「OSS 未配置」处理（不抛）

### 4.3 配置

在 `MainWindowViewModel` 构造器（`MainWindowViewModel.cs:57-61`）注入工厂：

```csharp
IOssStorageFactory ossFactory = new OssStorageFactory(() =>
    configService.GetAsync<OssConfig>(ConfigKeys.Oss).GetAwaiter().GetResult() ?? new OssConfig());
```

> 用了 `.GetAwaiter().GetResult()` 是因为构造器不能 `async`。这会同步阻塞，但只有一次，且 DB 在 LocalAppData 上微秒级 — 实践可接受。

---

## 5. AliyunOssStorage（`Services/AliyunOssStorage.cs`）

### 5.1 构造期校验（`AliyunOssStorage.cs:30-51`）

四个字段都得非空（Region / Bucket / AccessKeyId / AccessKeySecret）。任一为空抛 InvalidOperationException，UI 层 catch 后弹 toast。

### 5.2 Endpoint 构造（`BuildEndpoint`）

```csharp
private static string BuildEndpoint(string region) {
    region = region.Trim();
    if (region.StartsWith("http://", ...) || region.StartsWith("https://", ...)) {
        return region.TrimEnd('/');    // 已是完整 URL → 原样
    }
    return $"https://oss-{region}.aliyuncs.com";  // 默认补 https + 模板
}
```

> **踩坑承认**（`AliyunOssStorage.cs:46-50`）：用户曾传 `region="oss-cn-hangzhou.aliyuncs.com"`，结果拼出 `https://oss-oss-cn-hangzhou.aliyuncs.com.aliyuncs.com` — 修复：先判 `http(s)://` 起始则原样返回。构造器末尾打印 `endpoint=` + `region(in)=` 让用户一眼核对。

### 5.3 单 PUT vs 分片

```csharp
public long MultipartThresholdBytes => 5 * 1024 * 1024;   // 5MB

private const int PartSizeBytes = 5 * 1024 * 1024;        // 5MB 每片
```

`GalleryViewModel.UploadOneAsync`（`GalleryViewModel.cs:688-718`）决策：

```csharp
if (fileSize < storage.MultipartThresholdBytes)
    return await UploadSinglePutAsync(file, storage, localPath, objectKey, ct);
return await UploadMultipartNewAsync(file, storage, localPath, objectKey, fileSize, ct);
```

### 5.4 Multipart 流程（`AliyunOssStorage.cs:160-278`）

```
InitiateMultipartAsync(objectKey)
   ↓
[MultipartHandle { ObjectKey, UploadId, PartSize=5MB }]
   ↓
UploadPartAsync(handle, partNumber=1..N, stream, length)
   ↓ PartETag[]
[GalleryVM 每片成功 → UpsertAsync upload_jobs 加一片（断点保护）]
   ↓
CompleteMultipartAsync(handle, ordered ETags)
   ↓ oss success
[GalleryVM ApplyUploadSuccessAsync → UpdateUploadStateAsync + UploadStatus=Uploaded]
```

### 5.5 BoundedReadStream（`AliyunOssStorage.cs:329-365`）

阿里云 SDK `UploadPartRequest.InputStream` 读满 `length` 就停的约定。`BoundedReadStream` 是对内 `Stream` 的 wrapper：

```csharp
internal sealed class BoundedReadStream : Stream {
    private readonly Stream _inner;
    private long _remaining;
    public override int Read(byte[] buffer, int offset, int count) {
        if (_remaining <= 0) return 0;
        var toRead = (int)Math.Min(count, _remaining);
        var n = _inner.Read(buffer, offset, toRead);   // 不 seek，调用方自己控制位置
        _remaining -= n;
        return n;
    }
    // 不支持 Seek/Write/Length/Position — 按需抛 NotSupportedException
}
```

> **关键不变量**：inner 流的位置由调用方 (`GalleryViewModel.UploadMissingPartsAsync`) seek 到正确 offset。BoundedReadStream **只负责「读够 N 字节就停」，不负责 seek**。

### 5.6 签名 URL（`GetCoverUrlAsync`）

```csharp
var expiresAt = DateTime.Now.Add(expiry);     // 本地时间！不能 UTC
var uri = _client.GeneratePresignedUri(_config.Bucket, objectKey, expiresAt);
return Task.FromResult(uri.ToString());
```

**承认**（`AliyunOssStorage.cs:111-115`）：OSS 服务端按本地时区校验签名，**必须 `DateTime.Now`（本地），不能 UTC**，否则过期时刻错位。

---

## 6. GalleryViewModel 上传状态机（`GalleryViewModel.cs`）

### 6.1 上传总流程

```
UploadManyAsync(files, storage, ossCfg)           // GalleryVM.cs:583
  ├─ 设 IsUploading=true, UploadTotalCount/CompletedCount=0
  ├─ 创 _uploadCts
  └─ foreach file:
        └─ UploadOneAsync(file, storage, ossCfg, ct)
              ├─ 设 UploadStatus=Uploading, UploadError=null
              ├─ File.Exists check → Failed if missing
              ├─ 读 UploadJob GetByFileAsync(project, relPath)
              │     ├─ null           → UploadSinglePut / UploadMultipartNew
              │     └─ UploadJob      → ResumeUploadAsync
              └─ 累加 ok/fail/cancelled；UploadCompletedCount++
  finally: IsUploading=false
  ShowToast(summary)
  if errors.Count>0: ShowAlert 列出每个失败项
```

### 6.2 三种上传路径（`GalleryViewModel.cs:688-878`）

#### `UploadSinglePutAsync` — 小文件

```csharp
var result = await storage.UploadAsync(localPath, objectKey, ct);
if (result.Success) {
    var url = await storage.GetCoverUrlAsync(objectKey, TimeSpan.FromHours(1), ct);
    await ApplyUploadSuccessAsync(file, url);
    return Success;
}
file.UploadError = result.Error;
return Failed;
```

#### `UploadMultipartNewAsync` — 大文件全新

```csharp
var handle = await storage.InitiateMultipartAsync(objectKey, ct);
var job = new UploadJob {
    ProjectPath = file.ProjectPath,
    RelativePath = file.RelativePath,
    ObjectKey = objectKey,
    UploadId = handle.UploadId,
    FileSize = fileSize,
    PartSize = handle.PartSize,
    PartsUploaded = new List<UploadedPart>(),
    CreatedAt = now, UpdatedAt = now,
};
await _uploadJobRepo.UpsertAsync(job, ct);          // 创建 resume 锚点

var uploaded = await UploadMissingPartsAsync(file, storage, localPath, handle, fileSize, job, new HashSet<int>(), ct);
var allParts = uploaded.OrderBy(p => p.PartNumber).ToList();
await storage.CompleteMultipartAsync(handle, allParts, ct);

await ApplyUploadSuccessAsync(file, url);
await _uploadJobRepo.DeleteAsync(job.Id, ct);        // 成功清 job
```

#### `ResumeUploadAsync` — 续传（核心）

```csharp
// 决策 1: 本地文件大小变了 → job 失效 → 删 job 走全新
if (job.FileSize != fileSize) {
    await _uploadJobRepo.DeleteAsync(job.Id, ct);
    return ... (重新 Init+Upload)
}

// 决策 2: OSS 端 ListParts 失败（job 已被服务端清理）→ 删 job 走全新
IReadOnlyList<PartETag> ossParts;
try { ossParts = await storage.ListPartsAsync(handle, ct); }
catch {
    await _uploadJobRepo.DeleteAsync(job.Id, ct);
    file.UploadError = "OSS 端任务已失效，重新上传";
    return Failed;
}

// 合并：OSS 已传 ∪ DB 已传
var uploadedSet = new HashSet<int>(ossParts.Select(p => p.PartNumber));
foreach (var p in job.PartsUploaded) uploadedSet.Add(p.PartNumber);

// 同步 DB：把 OSS 端多出来的分片写回 DB（DB 可能落后于 OSS）
job.PartsUploaded = ossParts.Select(p => new UploadedPart { PartNumber = p.PartNumber, ETag = p.ETag }).ToList();
await _uploadJobRepo.UpsertAsync(job, ct);

// 传缺失分片
var newParts = await UploadMissingPartsAsync(file, storage, localPath, handle, fileSize, job, uploadedSet, ct);
// 合并最终 part 列表
var finalParts = new Dictionary<int, PartETag>();
foreach (var p in ossParts) finalParts[p.PartNumber] = p;
foreach (var p in newParts) finalParts[p.PartNumber] = p;

await storage.CompleteMultipartAsync(handle, finalParts.Values.OrderBy(p => p.PartNumber).ToList(), ct);
await ApplyUploadSuccessAsync(file, url);
await _uploadJobRepo.DeleteAsync(job.Id, ct);
return Success;
```

### 6.3 `UploadMissingPartsAsync` 每片持久化（`GalleryViewModel.cs:843-878`）

```csharp
foreach (var file = File.OpenRead(localPath)) {
    for (int partNumber = 1; partNumber <= partCount; partNumber++) {
        if (alreadyUploaded.Contains(partNumber)) continue;
        stream.Seek(offset, SeekOrigin.Begin);
        using var bounded = new BoundedReadStream(stream, length);
        var etag = await storage.UploadPartAsync(handle, partNumber, bounded, length, ct);
        uploaded.Add(etag);
        // 每片成功 → 立刻写 DB（崩溃后最多少传一片）
        job.PartsUploaded.Add(new UploadedPart { PartNumber = partNumber, ETag = etag.ETag });
        job.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await _uploadJobRepo.UpsertAsync(job, ct);
    }
}
```

### 6.4 退出 / 取消策略

| 路径 | 行为 |
|---|---|
| `UploadSinglePut` 中途取消 | 抛 OperationCanceledException — 上半截留在 OSS 但没 complete，**OSS 后台会清理未 complete 的 part**（阿里云 TTL 默认保留一天后清理）|
| `UploadMultipartNew` 取消 | 抛 OperationCanceledException — **job 留着不删**，下次可续 |
| `ResumeUpload` 取消 | 同上，job 留着 |
| Complete 后 DB 写失败 | UI 状态已是 Uploaded（已设 `MediaFile.IsUploaded=true`），下次重试会再写 DB，不影响正确性（`GalleryViewModel.cs:880-897`）|

### 6.5 启动恢复（`ResumeInterruptedAsync` + `MainWindowViewModel.TryPromptResumeInterruptedAsync`）

```
MainWindowViewModel.TryPromptResumeInterruptedAsync
  └─ UploadJobRepository.GetInProgressAsync(projectPath)
        └─ if jobs.Count>0 → ShowConfirmAsync("是否恢复?") → 调用 GalleryVM.ResumeInterruptedAsync

GalleryViewModel.ResumeInterruptedAsync(jobs)
  └─ 把 jobs 投影到当前 CurrentMediaFiles 里的 MediaFile
       （过滤掉本地已删除的）
  └─ ExitMultiSelect / ShowToast
  └─ UploadManyAsync(filteredFiles, storage, ossCfg)
       └─ 每个 file 走 ResumeUploadAsync（job 已存在）
```

---

## 7. ObjectKey 拼接（`AliyunOssStorage.BuildObjectKey`）

```csharp
public static string BuildObjectKey(string pathPrefix, string relativePath) {
    relativePath = relativePath.Replace('\\', '/').TrimStart('/');
    if (string.IsNullOrWhiteSpace(pathPrefix)) return relativePath;
    var prefix = pathPrefix.Replace('\\', '/').Trim('/');
    return string.IsNullOrEmpty(prefix) ? relativePath : $"{prefix}/{relativePath}";
}
```

- 把 `\`（Windows 路径）转 `/`（OSS 永远用 /）
- TrimStart('/') 防止相对路径带前缀
- PathPrefix 自动 trim '/'
- 例：`relativePath = "2024-08-15/IMG_001.CR2"` + `prefix = "shots/astro"` → `shots/astro/2024-08-15/IMG_001.CR2`

---

## 8. 错误处理

| 异常源 | 处理 |
|---|---|
| `AliyunOssStorage` OSS SDK 异常 | catch → `OssUploadResult { Success=false, Error="OSS 错误 [code]: msg" }` |
| `_uploadJobRepo` 写 DB 异常 | catch（`ApplyUploadSuccessAsync`）— UI 已是 Uploaded，错误明细已记 |
| `storage.ListPartsAsync` 抛异常 | `ResumeUploadAsync` catch → 删 job + 置 UploadStatus=Failed |
| 用户取消 `CancellationToken` | `OperationCanceledException` 抛出，被外层 try/catch → cancelled++ |
| 取消时 multipart 半上传 | **不 Abort**（OSS 后台清理）— v0.1 简化 |

---

## 9. 多选 / 单选上传入口

| 入口 | 调用 | 走法 |
|---|---|---|
| Gallery 工具栏「批量上传」 | `GalleryViewModel.BatchUploadCommand` | `SelectedFiles.ToList()` → `UploadManyAsync` |
| 单文件右键菜单「上传」 | `UploadSingleCommand(MediaFile)` | `UploadManyAsync(new[]{file})` |
| 启动恢复 | `ResumeInterruptedAsync(jobs)` | 把 jobs → 当前可见 files → 调 `UploadManyAsync` |
| 下载（OSS 拉到本地）| `OpenFileAsync`（点击文件） | `storage.DownloadAsync(objectKey, localPath)` + 重新生成缩略图 |

---

## 10. 修改这个模块前的检查清单

| 改动 | 涉及 | 验证 |
|---|---|---|
| 加 Provider（腾讯云 COS）| 新建 `TencentCosStorage.cs` + `OssStorageFactory` switch | UI 选 COS 后能保存 + 上传 |
| 改 `MultipartsThresholdBytes` | `AliyunOssStorage.MultipartThresholdBytes` | 大文件边界走 path 切换正确 |
| 改 PartSize | `AliyunOssStorage.PartSizeBytes` | OSS API 限制：1B-5GB，5MB 平衡 |
| 加密 AccessKeySecret | `ConfigService` 加 SecureString / 走 Keychain (`02-data-layer.md`) | macOS Keychain 可正常存读 |
| 改 ListParts 策略 | `ResumeUploadAsync` | OSS 端分片被服务端清理的情况（7 天后）能感知 |
| 加 abort | `AbortMultipartAsync` 调用点 | 用户取消时主动 abort 避免 OSS 端占用配额 |
| 加并发上传 | `UploadManyAsync` | Task.WhenAll + 限并发数（SemaphoreSlim） |

---

## 11. 已知陷阱（详见 `10-trap-book.md`）

- **Region 带 "oss-" 前缀** → BuildEndpoint 已修，但 Trace log 仍打出提醒
- **签名 URL 时区** → 必须 `DateTime.Now` 本地时间，不能 UTC（已固化）
- **OSS SDK 是同步阻塞** → 已经 `await Task.Run(() => ...)` 让线程（已固化）
- **DB 写失败不撤销** → ApplyUploadSuccessAsync 内 catch 已 silent
- **取消时 multipart 不 abort** → 简化处理，依赖 OSS 后台清理
- **`AbortMultipartAsync` 占位** → v0.1 没调用方
- **`UploadPartAsync` 在 SDK 调用中用 `Task.Run` + stream closure** → 必须 stream 在 Task.Run 闭包内被持有（已修）
- **OSS 错误码 ETag 必须完整** → 截前 8 位做日志不够，上传时不能丢（已修）
