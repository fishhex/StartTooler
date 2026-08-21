# API-06 · 错误码 i18n（v0.13）

PC 端 HTTP 返回的错误码是英文 / 简短字符串。App 端给用户看的中文 / 多语言提示要按本文档映射。

> 配套：[05-mobile-app.md](05-mobile-app.md) §七「错误处理」

## 一、错误分类

| 类别 | 字段 | 例子 |
|---|---|---|
| HTTP 状态码 | 401 / 404 / 405 / 500 | `401`、`404` |
| 业务错误字符串 | `error` 字段 | `"invalid secret"` |
| App 端自定义错误 | - | 网络断开 / 二维码无效 |

App 端需要映射：

```
HTTP status → 错误类型
error 字符串 → 用户提示文案
App 自定义错误 → 业务级 i18n
```

## 二、HTTP 状态码映射

| 状态 | 中文 | 英文 | App 反应 |
|---|---|---|---|
| 200 | ✅ 成功 | Success | 继续 |
| 400 | 请求格式错，请重试 | Bad request | Toast + 取消 |
| 401 | 密钥已过期，请重新扫码 | Session expired | 清 secret + 跳空间列表 |
| 404 | PC 不存在该项目 | Not found | 重新拉项目列表 |
| 405 | PC 不支持此操作 | Not supported | Toast |
| 500 | PC 端服务异常，请稍后重试 | Server error | Toast + Retry |

### 2.1 401 详解（v0.13 简化）

| App 当前状态 | 反应 |
|---|---|
| **未持久化** | 跳空间列表（用户扫码） |
| **持久化 secret** | 清 secret + 跳空间列表 + Toast"密钥已过期" |
| **重新扫码后** | 重新进入验证流程 |

> v0.13 起，401 不再有"等下一次广播 + 重试一次"流程（UDP 移除）。

### 2.2 404 详解

| App 当前状态 | 反应 |
|---|---|
| health 阶段 404 | 跳空间列表（PC 路由不存在） |
| projects 阶段 404 | 重新拉项目列表 |
| upload 阶段 404 | 项目失效 → 重新拉项目列表 |

## 三、PC 端 `error` 字符串映射

### 3.1 精确匹配

| PC 端 error | i18n key | 中文 | 英文 |
|---|---|---|---|
| `invalid secret` | `error.invalid_secret` | 密钥已过期，请重新扫码 | Session expired |
| `invalid project name` | `error.invalid_project` | 项目名无效 | Invalid project |
| `project 'X' not found` | `error.project_not_found` | 项目 `X` 在 PC 端不存在 | Project not found |
| `No files uploaded.` | `error.no_files` | 未选择文件 | No files |
| `Invalid content type. Use multipart/form-data.` | `error.bad_content_type` | 上传格式错误 | Bad format |
| `multipart parse failed: X` | `error.parse_failed` | 服务端解析失败 | Parse failed |
| `method not allowed` | `error.method_not_allowed` | PC 不支持此操作 | Not supported |
| `not found` | `error.not_found` | 接口不存在 | Not found |

### 3.2 模式匹配（带参数）

```regex
^project '(.+)' not found$
  → 捕获组 1 = 项目名
  → 用户提示：项目 "捕获组 1" 在 PC 端不存在
```

```regex
^multipart parse failed: (.+)$
  → 捕获组 1 = 详细原因
  → 用户提示：服务端解析失败（捕获组 1）
```

### 3.3 兜底匹配

未匹配到的 error 字符串 → 显示原始 + 提示"未知错误"：

```
未知错误
Raw: invalid project format
```

## 四、上传业务失败（failed[]）

`POST /api/v1/projects/{name}/upload` 响应 200 状态，但 `failed[]` 可能有失败项。

### 4.1 失败原因映射

| `failed[].reason` | 中文 | 反应 |
|---|---|---|
| `unsupported extension X` | 不支持的文件类型：X | 跳过该文件 |
| `exceeds 500MB limit` | 超过 500MB 上限 | 跳过该文件 |
| 其它 | 上传失败：reason | 跳过该文件 |

### 4.2 用户提示

```
✓ 已上传 5 张
✗ 失败 2 张
  - IMG_001.txt：不支持的文件类型 .txt
  - IMG_002.bmp：超过 500MB 上限
```

## 五、App 端自定义错误（v0.13 新增）

App 端独立发生的错误（不在 PC 端响应里）：

### 5.1 QR 解析错误

| 场景 | i18n key | 中文 |
|---|---|---|
| host 非 IPv4 | `error.qr_invalid` | 二维码无效 |
| port 越界 | `error.qr_invalid` | 二维码无效 |
| path 不是 `/upload` | `error.qr_invalid` | 二维码无效 |
| `?k` 缺失或非 32 hex | `error.qr_invalid` | 二维码无效 |
| 公网 relay QR（无 `?k`）| `error.qr_invalid` | 二维码无效 |

### 5.2 网络层错误

| 场景 | i18n key | 中文 |
|---|---|---|
| DNS 失败 | `error.dns` | 域名解析失败 |
| TCP 拒绝 | `error.network` | 连不上 PC |
| 超时 3s（health）| `error.pc_unreachable` | PC 不可达，请检查同 Wi-Fi |
| 超时 60s（upload）| `error.timeout` | 上传超时，可重试 |

### 5.3 健康检查 / 持久化

| 场景 | i18n key | 中文 |
|---|---|---|
| PC 端重启换 secret | `error.secret_expired` | 密钥已过期，请重新扫码 |
| PC 换 IP | `error.pc_unreachable` | 连不上 PC |
| 持久化损坏 | `error.persistence` | 本地存储损坏，请重新扫码 |

### 5.4 客户端层

| 场景 | i18n key | 中文 |
|---|---|---|
| 选择 0 张照片 | `error.no_photos_selected` | 请先选择照片 |
| 选 > 50 张 | `error.too_many_photos` | 一次最多选 50 张 |
| 访问相机被拒 | `error.camera_denied` | 请在系统设置中开启相机权限 |
| 设备存储满 | `error.storage_full` | 设备存储不足 |

## 六、i18n key 完整列表

```
error.invalid_secret = "密钥已过期，请重新扫码"
error.invalid_project = "项目名无效"
error.project_not_found = "项目 %@ 在 PC 端不存在"
error.no_files = "未选择文件"
error.bad_content_type = "上传格式错误"
error.parse_failed = "服务端解析失败（%@）"
error.method_not_allowed = "PC 不支持此操作"
error.not_found = "接口不存在"
error.network = "连不上 PC"
error.dns = "域名解析失败"
error.timeout = "请求超时"
error.qr_invalid = "二维码无效"
error.pc_unreachable = "PC 不可达，请检查同 Wi-Fi"
error.secret_expired = "密钥已过期，请重新扫码"
error.persistence = "本地存储损坏，请重新扫码"
error.no_photos_selected = "请先选择照片"
error.too_many_photos = "一次最多选 50 张"
error.camera_denied = "请在系统设置中开启相机权限"
error.storage_full = "设备存储不足"
error.unknown = "未知错误"
```

参数化使用占位符（iOS `%@` / Android `%1$s`）。

## 七、状态码 → i18n key 映射表

| 状态 | i18n key |
|---|---|
| 400 | `error.bad_request` |
| 401 | `error.invalid_secret` |
| 404 | `error.not_found` |
| 405 | `error.method_not_allowed` |
| 500 | `error.server` |
| 200 | （成功无需 key）|

## 八、错误提示级别

| 级别 | 适用 | UI |
|---|---|---|
| INFO | 通知类（连接成功）| Toast |
| WARN | 一般失败（上传失败）| 条幅 + 重试 |
| ERROR | 严重失败（PC 不可达）| 全屏错误页 |

### 8.1 实现建议

| 级别 | iOS | Android |
|---|---|---|
| INFO | `SwiftUI .toast()` | `Snackbar.make().show()` |
| WARN | `Alert` + `Dismiss` | `AlertDialog` |
| ERROR | 全屏错误页 | 全屏错误页 |

## 九、错误响应处理流程

```
App 收到 HTTP 响应
  ↓
检查 status
  ├── 200 → 解析 JSON body
  │       ├── success=true → 业务成功
  │       └── success=false → 业务失败（failed[]）
  ├── 401 → 清 secret + 跳空间列表
  ├── 404 → 重新拉项目列表
  ├── 405 → Toast "PC 不支持此操作"
  └── 500 → Toast "PC 端服务异常" + Retry
```

## 十、版本兼容

- PC 端新增 error 字符串 → App 端未识别 → 兜底"未知错误"
- PC 端删除 error 字符串 → App 端永远不显示

**双向宽容**。

## 十一、错误码示例

### 11.1 401 错误

PC 端：

```json
{ "error": "invalid secret" }
```

App 端处理：

```swift
if status == 401 {
    persistence.clearSecret(for: space.name)
    navigateToSpaceList()
    showToast(NSLocalizedString("error.invalid_secret", comment: ""))
}
```

### 11.2 404 错误

PC 端：

```json
{ "error": "project 'm42' not found" }
```

App 端处理：

```swift
if status == 404 {
    let format = NSLocalizedString("error.project_not_found", comment: "")
    let message = String(format: format, projectName)
    showWarning(message)
    refreshProjectsList()
}
```

### 11.3 上传业务失败

PC 端：

```json
{
  "success": true,
  "count": 3,
  "files": [...],
  "failed": [
    { "name": "notes.txt", "reason": "unsupported extension .txt" }
  ]
}
```

App 端处理：

```swift
if let failed = response.failed, !failed.isEmpty {
    for f in failed {
        let msg = "\(f.name): \(f.reason)"
        showWarning(msg)
    }
}
```

## 十二、调试

### PC 端日志

`Debug.WriteLine` 写在 `UploadServerService.cs` 各 catch 块里。

```
[UploadServer] Handle error: System.IO.IOException: ...
```

### App 端日志

iOS / Android 都在 console 输出。**注意**：禁止日志明文 secret。

```swift
// ❌ 错
print("[ERROR] status=\(status) secret=\(secret)")

// ✅ 对
print("[ERROR] status=\(status) secretLen=\(secret.count)")
```

## 十三、用户可读错误 vs 开发者可读错误

| 维度 | 用户 | 开发者 |
|---|---|---|
| 渠道 | UI 提示 | 日志 |
| 内容 | 简明 + 下一步 | 详细 + 上下文 |
| 例子 | "密钥已过期，请重新扫码" | "401 from /api/v1/projects/foo: invalid secret, last_seen=2026-08-21" |

App 端需要**双通道**：UI 给用户，日志给开发者。

## 十四、避坑指南

| 坑 | 缓解 |
|---|---|
| 错误文案英文 | 强制 i18n 流程 |
| 错误文案太长 | 简短 + 截断 |
| 错误提示重复 | 50ms 防抖 |
| 错误提示错过 | Toast 4 秒 + 可点开详情 |
| 用户看不懂 | "复制错误" 按钮 + 客服邮箱 |
| 错误码漂移 | error 字符串**不依赖**业务细节（用 HTTP 状态码） |

## 十五、跨平台一致性

| 维度 | iOS | Android |
|---|---|---|
| 错误层级 | Info / Warn / Error | Info / Warn / Error |
| UI 控件 | SwiftUI `.alert()` / fullScreenCover | Compose AlertDialog / 全屏页 |
| 日志 | OSLog | Logcat |
| 国际化 | Localizable.strings | strings.xml |

**错误码到 i18n key 映射**两边一致。

## 十六、变更记录

| 日期 | 版本 | 内容 |
|---|---|---|
| 2026-08-21 | v0.13 | 改写：移除"等下一次广播 + 重试一次"；新增 QR 解析错误 i18n |
