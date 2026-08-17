# API-06 · 错误码 i18n

PC 端 HTTP 返回的错误码是英文 / 简短字符串。App 端给用户看的中文 / 多语言提示要按本文档映射。

本文档定义 App 端如何把 PC 端响应映射到本地化提示。

## 一、错误分类

PC 端错误分三类：

| 类别 | 字段 | 例子 |
|---|---|---|
| HTTP 状态码 | 401 / 404 / 405 / 500 | `401`、`404` |
| 业务错误字符串 | `error` 字段 | `"invalid token"` |
| App 端自定义错误 | - | 网络断开 / UDP 无响应 |

App 端需要映射：

```
HTTP status → 错误类型
error 字符串 → 用户提示文案
```

## 二、HTTP 状态码映射

| 状态 | 用户提示（中文） | 用户提示（英文） | App 反应 |
|---|---|---|---|
| 200 | ✅ 成功 | Success | 继续 |
| 400 | 请求格式错，请重试 | Bad request | Toast + 取消 |
| 401 | 鉴权失败，Token 失效 | Session expired | 跳 Token 验证页 |
| 404 | 服务未找到 | Not found | 跳连接页 |
| 405 | 该 PC 不支持此操作 | Not supported | Toast |
| 500 | PC 端服务异常，请稍后重试 | Server error | Toast + 重试按钮 |

### 401 详解

| App 当前状态 | 反应 |
|---|---|
| 未持久化任何 Token | 跳 Token 验证页（用户输入） |
| 持久化 Token | 跳 Token 验证页（已回填，旧 Token 失败提示） |
| 验证后再失败 | 提示"PC 端 Token 已重置" |

### 404 详解

| App 当前状态 | 反应 |
|---|---|
| Token 验证阶段 404 | 跳连接页（项目不存在） |
| 项目列表阶段 404 | 提示"PC 端项目被删除" |
| 上传阶段 404 | 提示"项目不存在，请重选" |

## 三、PC 端 `error` 字符串映射

### 3.1 精确匹配

PC 端 `error` 字段是**英文短串**，App 端直接匹配：

| PC 端 error | App 端 i18n key | 中文 | 英文 |
|---|---|---|---|
| `invalid token` | `error.invalid_token` | Token 错误，请重新输入 | Invalid token |
| `invalid project name` | `error.invalid_project` | 项目名无效 | Invalid project |
| `project 'X' not found` | `error.project_not_found` | 项目 `X` 在 PC 端不存在 | Project not found |
| `No files uploaded.` | `error.no_files` | 未选择文件 | No files |
| `Invalid content type. Use multipart/form-data.` | `error.bad_content_type` | 上传格式错误 | Bad format |
| `multipart parse failed: X` | `error.parse_failed` | 服务端解析失败 | Parse failed |
| `method not allowed` | `error.method_not_allowed` | 服务不支持此操作 | Not supported |
| `not found` | `error.not_found` | 接口不存在 | Not found |

### 3.2 模式匹配（带参数）

部分 error 字符串含参数，App 端提取：

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
未知错误，请联系 PC 端开发者
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

成功 N 张 + 失败 M 张：

```
✓ 已上传 5 张
✗ 失败 2 张
  - IMG_001.txt：不支持的文件类型 .txt
  - IMG_002.bmp：超过 500MB 上限
```

## 五、App 端自定义错误

App 端独立发生的错误（不在 PC 端响应里）：

### 5.1 网络层

| 场景 | App 提示 | 反应 |
|---|---|---|
| UDP 无响应（5 秒扫描结束） | 未找到 PC，请确认 PC 端已启动服务 | 展示"手动输入 IP" |
| TCP 连接失败 | 连接失败，请检查网络 | Retry 按钮 |
| TLS 握手失败 | （PC 端用 HTTP，不会触发） | - |
| DNS 失败 | 域名解析失败 | 检查 IP 拼写 |
| 超时 30 秒 | 上传超时，可重试 | Retry 按钮 |

### 5.2 客户端层

| 场景 | App 提示 | 反应 |
|---|---|---|
| 选择 0 张照片 | 请先选择照片 | 禁用"上传"按钮 |
| 选 > 50 张 | 一次最多选 50 张（建议） | 提示 |
| 访问相册被拒 | 请在系统设置中开启相册权限 | 跳设置 |
| 设备存储满 | 设备存储不足 | 提示 |

### 5.3 系统层

| iOS | Android | 提示 |
|---|---|---|
| 本地网络权限被拒 | INTERNET 权限被拒 | 跳系统设置 |
| 后台被挂起 | Doze 模式 | 提示 |
| App 处于飞行模式 | 飞行模式 | 提示 |

## 六、错误码 i18n key 规范

App 端 i18n 文件（iOS `Localizable.strings` / Android `strings.xml`）：

```
error.invalid_token = "Token 错误，请重新输入"
error.invalid_project = "项目名无效"
error.project_not_found = "项目 %@ 在 PC 端不存在"
error.no_files = "未选择文件"
error.bad_content_type = "上传格式错误"
error.parse_failed = "服务端解析失败（%@）"
error.method_not_allowed = "服务不支持此操作"
error.not_found = "接口不存在"
error.network = "网络连接失败"
error.timeout = "请求超时"
error.unknown = "未知错误"
```

参数化使用占位符（iOS `%@` / Android `%1$s`）。

## 七、状态码 → i18n key 映射表

| 状态 | i18n key | 中文 | 英文 |
|---|---|---|---|
| 400 | `error.bad_request` | 请求格式错误 | Bad request |
| 401 | `error.unauthorized` | 鉴权失败 | Unauthorized |
| 404 | `error.not_found` | 接口不存在 | Not found |
| 405 | `error.method_not_allowed` | 不支持的操作 | Not allowed |
| 500 | `error.server` | 服务异常 | Server error |
| 200 | （成功无需 key） | - | - |

## 八、错误提示级别

| 级别 | 适用 | UI |
|---|---|---|
| INFO | 通知类（连接成功） | Toast |
| WARN | 一般失败（上传失败） | 条幅 + 重试 |
| ERROR | 严重失败（PC 不可达） | 全屏错误页 |

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
  ├── 401 → 清 Token + 跳验证页
  ├── 404 → 跳连接页 / 重新拉项目列表
  ├── 405 → Toast "PC 端不支持此操作"
  └── 500 → Toast "PC 端服务异常" + Retry
```

## 十、新版本兼容

PC 端新增 error 字符串 → App 端未识别 → 兜底"未知错误"。

PC 端删除 error 字符串 → App 端永远不显示。

**双向宽容**。

## 十一、错误码示例

### 11.1 401 错误

PC 端：

```json
{ "error": "invalid token" }
```

App 端处理：

```swift
// iOS Swift
if status == 401 {
    Keychain.delete("pc.last.token")
    navigateToTokenValidationPage()
    showToast(NSLocalizedString("error.unauthorized", comment: ""))
}
```

### 11.2 404 错误

PC 端：

```json
{ "error": "project 'm42' not found" }
```

App 端处理：

```swift
// iOS Swift
if status == 404 {
    let format = NSLocalizedString("error.project_not_found", comment: "")
    let message = String(format: format, projectName)
    showErrorPage(message)
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
// iOS Swift
if let failed = response.failed, !failed.isEmpty {
    for f in failed {
        let msg = "\(f.name): \(f.reason)"
        showWarning(msg)
    }
}
```

## 十二、调试

### PC 端日志

`Debug.WriteLine` 写在 `UploadServerService.cs` 各 catch 块里。开发期可看：

```
[UploadServer] Handle error: System.IO.IOException: ...
[UploadServer] UDP send failed: Network is unreachable
```

### App 端日志

iOS / Android 都在 console 输出。**注意**：禁止日志明文 Token。

```swift
// ❌ 错
print("[ERROR] status=\(status) token=\(token)")

// ✅ 对
print("[ERROR] status=\(status) retrySuggester=\(suggester)")
```

## 十三、用户可读错误 vs 开发者可读错误

| 维度 | 用户 | 开发者 |
|---|---|---|
| 渠道 | UI 提示 | 日志 |
| 内容 | 简明 + 下一步 | 详细 + 上下文 |
| 例子 | "Token 失效，请重新输入" | "401 from /api/v1/projects/foo: invalid token, last_seen_min=15, retry_count=3" |

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

## 十六、版本兼容

| App 版本 | 错误映射 |
|---|---|
| 1.0.0 | 上文 |
| 1.1.0 | 加 ERR_NETWORK_CHANGED / ERR_PC_OFFLINE |
| 1.2.0 | 加 ERR_VERSION_MISMATCH（PC 端版本不兼容） |

PC 端错误字符串**永不删**（兼容老 App 端）。
