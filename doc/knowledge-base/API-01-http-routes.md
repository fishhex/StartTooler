# API-01 · HTTP 路由

PC 端启了一个 HTTP 服务（[UploadServerService](../../StartTooler/Services/UploadServerService.cs)），对第三方客户端（H5 浏览器、手机 App、curl）暴露一套 API。App 端通过扫 PC 端 QR 解出 `{ip, port, secret}` 后用本协议做业务对接。

> 协议版本：**v0.13**（移除 v0.12 的 UDP 广播 + 6 位数字 Token；改为 32 字符 hex secret）

## 一、协议基本信息

| 项 | 值 |
|---|---|
| 协议 | HTTP/1.1 |
| 默认端口 | 8765 |
| 监听地址 | `http://+:{port}/`（所有网卡） |
| 内容类型 | `application/json; charset=utf-8` |
| 编码 | UTF-8 |
| 字段命名 | camelCase |
| 鉴权机制 | 32 字符 hex secret（query `?k=` 或 header `X-Key`） |
| 单文件上限 | 500MB |
| 文件扩展名白名单 | `.jpg` `.jpeg` `.png` `.raw` `.avi` `.mp4` `.mov` `.mkv` `.webm` `.m4v` `.mpg` `.mpeg` |

## 二、路由总表

| # | 路径 | 方法 | 鉴权 | 用途 |
|---|---|---|---|---|
| 1 | `/upload` | GET | ❌ | H5 上传页面 |
| 2 | `/upload` | POST | ❌ | H5 上传（multipart） |
| 3 | `/api/v1/health` | GET | ❌ | 健康检查 + 取 `name` 作为工作空间标识 |
| 4 | `/api/v1/projects` | GET | ✅ | 项目列表 |
| 5 | `/api/v1/projects/{name}/upload` | POST | ✅ | 上传到指定项目 |

H5 端点（1、2）保留 v0.10 行为，新客户端应使用 API 端点（3、4、5）。

## 三、API 端点详解

### 3.1 `GET /api/v1/health`

**用途**：客户端扫码后第一件事。验证 PC 真实在线 + 拿到机器名（作为工作空间名）+ 拿到当前 secret（用于回包校验）。

**请求**：无

**响应 200**：

```json
{
  "ok": true,
  "service": "starttooler",
  "version": "0.13",
  "name": "鱼鱼的 MacBook",
  "port": 8765,
  "secret": "7f3a9b2c8e1d4f6a...",
  "currentProject": "m42-2025-12-13"
}
```

| 字段 | 类型 | 含义 |
|---|---|---|
| `ok` | bool | 永远 true |
| `service` | string | 固定 `"starttooler"` |
| `version` | string | 服务版本（`"0.13"`） |
| `name` | string | **本机机器名**（`Environment.MachineName`）—— App 端作为工作空间名 |
| `port` | int | HTTP 监听端口 |
| `secret` | string | 32 字符 hex 鉴权 secret |
| `currentProject` | string | 当前激活项目 basename（空 = 无） |

> ⚠️ **app 端处理**：
> - `name` 是工作空间的主键（取此值作为本地空间名）
> - `secret` 仅作"回包校验"用，App 端不应从这里取 secret（唯一权威源是 QR 里的 `?k=`）
> - `version` 不低于 `"0.13"` 才视为兼容

**响应 5xx**：服务异常（理论不应触发）

### 3.2 `GET /api/v1/projects`

**用途**：列出 PC 上所有可用项目（按 RecentDirectories），含每个项目的统计信息。

**请求**：

| 位置 | 字段 | 必填 | 备注 |
|---|---|---|---|
| query | `k` | ✅ | 从 QR 拿到的 secret |

**响应 200**：

```json
{
  "items": [
    {
      "name": "m42-2025-12-13",
      "path": "/Users/hex/Astro/m42-2025-12-13",
      "projectName": "猎户 M42",
      "fileCount": 234,
      "sizeMb": 4096,
      "isCurrent": true
    },
    {
      "name": "ngc7000",
      "path": "/Users/hex/Astro/ngc7000",
      "projectName": "北美洲星云",
      "fileCount": 12,
      "sizeMb": 256,
      "isCurrent": false
    }
  ]
}
```

| 字段 | 类型 | 含义 |
|---|---|---|
| `items[].name` | string | 项目文件夹 basename |
| `items[].path` | string | 项目绝对路径 |
| `items[].projectName` | string\|null | 用户给项目起的名字 |
| `items[].fileCount` | long | 媒体文件总数（含历史） |
| `items[].sizeMb` | long | 占磁盘 MB（真实文件） |
| `items[].isCurrent` | bool | 当前激活项目 |

**失效目录自动过滤**（磁盘不存在的路径不返回）。

**响应 401**：

```json
{ "error": "invalid secret" }
```

### 3.3 `POST /api/v1/projects/{name}/upload`

**用途**：批量上传文件到指定项目（multipart/form-data）。

**路径参数**：`{name}` —— 项目文件夹 basename（见 `GET /api/v1/projects` 的 `items[].name`）

**请求**：

| 位置 | 字段 | 必填 | 备注 |
|---|---|---|---|
| path | `{name}` | ✅ | 项目 basename |
| query | `k` | ✅ | 从 QR 拿到的 secret |
| header | `Content-Type` | ✅ | `multipart/form-data; boundary=...` |
| body | `file` | ✅×N | 文件（multiple） |

**响应 200**：

```json
{
  "success": true,
  "count": 2,
  "files": [
    {
      "name": "M42_001.jpg",
      "path": "/Users/hex/Astro/m42-2025-12-13/2025-12-13/M42_001.jpg"
    },
    {
      "name": "M42_002.jpg",
      "path": "/Users/hex/Astro/m42-2025-12-13/2025-12-13/M42_002.jpg"
    }
  ],
  "failed": [
    {
      "name": "notes.txt",
      "path": "",
      "reason": "unsupported extension .txt"
    }
  ]
}
```

| 字段 | 类型 | 含义 |
|---|---|---|
| `success` | bool | 永远 true（200 状态） |
| `count` | int | 成功数 |
| `files[]` | object[] | 成功落盘的每个文件 |
| `files[].name` | string | 落盘文件名（可能与上传名不同，重名时改名） |
| `files[].path` | string | 完整落盘路径 |
| `failed[]` | object[] | 失败项（部分失败时返回） |
| `failed[].name` | string | 原上传文件名 |
| `failed[].reason` | string | 失败原因 |

**响应 400**：

```json
{ "error": "Invalid content type. Use multipart/form-data." }
```

```json
{ "error": "No files uploaded." }
```

**响应 401**：

```json
{ "error": "invalid secret" }
```

**响应 404**：

```json
{ "error": "project 'xxx' not found" }
```

## 四、错误码总表

| 状态 | 含义 | 触发场景 | App 端反应 |
|---|---|---|---|
| 200 | 成功 | 正常响应（含部分失败） | 按 D01 处理 |
| 400 | 请求格式错误 | multipart 缺失 / 0 文件 | Toast 提示 + 列表保留 |
| 401 | 鉴权失败 | secret 错 / 缺 | 清 secret + 引导重新扫码 |
| 404 | 路径不存在 | 项目名不匹配 / 路径模板不匹配 | 项目失效 → 刷新项目列表 |
| 405 | 方法不允许 | 路径对但方法错 | 客户端代码 bug |
| 500 | 服务器异常 | 文件系统错误 / 解析失败 | Toast + Retry |

## 五、Secret 鉴权细则

### 5.1 Secret 生成

| 项 | 值 |
|---|---|
| 长度 | 16 字节 = 32 字符 hex |
| 生成 | 启动时 `RandomNumberGenerator.Fill(16)` |
| 范围 | `0-9a-f` |
| 持久化 | **不持久化**（每次启动重生成） |
| 重置 | UI 点"重置密钥"按钮立即重生成 |

### 5.2 Secret 传递方式

**优先**：URL query 参数

```
GET /api/v1/projects?k=7f3a9b2c8e1d4f6a...
```

**备选**：HTTP header

```
X-Key: 7f3a9b2c8e1d4f6a...
```

### 5.3 Secret 重置

PC 端 UI 点"重置密钥"按钮 → 新 secret 立即生效。**所有已有 App 端连接立刻 401** → App 端必须引导用户重新扫码。

### 5.4 安全边界

- 单 PC 范围（局域网 + 公网 relay 模式下不在同一网段）
- 不防 LAN 嗅探（同路由器下的客户端可看到明文）
- 防重放：仅防 LAN 内的合法用户**重置后**复用 secret
- PC 端重启即失效（不持久化）

## 六、文件落盘规则

### 6.1 路径

```
{project_root}/{yyyy-MM-dd}/{filename}
```

例：`/Users/hex/Astro/m42-2025-12-13/2025-12-13/M42_001.jpg`

### 6.2 重名处理

`name_1.ext` `name_2.ext` ... 依次递增，直到文件名不冲突。

### 6.3 扩展名白名单

只允许白名单内扩展名。其他扩展名（`txt` / `json` / `pdf` 等）走 `failed[]`，**不在 `files[]` 内**。

### 6.4 单文件大小

500MB 软上限。超过走 `failed[]`，**不在 `files[]` 内**。

### 6.5 异步副作用

成功落盘后，**异步**触发 `ScanDirectoryAsync` 扫描整个项目目录（不阻塞响应）。大约 1-2 秒后 `media_files` 新行写入。

## 七、完整交互流程

App 端通过 QR 拿到 `{ip, port, secret}` 后：

```
1. 扫码 → 解析 → 拿到 {ip, port, secret}
   ↓
2. GET /api/v1/health（无鉴权）
   ↓ 验证 PC 真实可达 + 取 name（作为工作空间名）
3. GET /api/v1/projects?k={secret}
   ↓ 拿到项目列表
4. POST /api/v1/projects/{name}/upload?k={secret}
   ↓ 拿到上传结果
```

QR 协议详见 [API-04-qr-protocol.md](API-04-qr-protocol.md)。

## 八、版本兼容

- API 路径前缀 `/api/v1/` 表示 major version
- App 端应检查 `version` 字段
- 客户端应忽略未知字段，向后兼容
- 字段删除 / `service` 改名：**破坏性变更**，需 major version bump

## 九、客户端实现参考

最简单的 curl 验证请先在 PC 端启服务，拿到密钥后：

```bash
# 1. 健康检查（无需 secret，确认 name 与 version）
curl -s http://192.168.1.10:8765/api/v1/health | jq .

# 2. 列项目
K="7f3a9b2c8e1d4f6a..."
curl -s "http://192.168.1.10:8765/api/v1/projects?k=$K" | jq .

# 3. 上传
curl -s -X POST "http://192.168.1.10:8765/api/v1/projects/m42-2025-12-13/upload?k=$K" \
  -F "file=@/tmp/test.jpg" | jq .

# 4. secret 错 → 401
curl -s "http://192.168.1.10:8765/api/v1/projects?k=wrong"
# {"error":"invalid secret"}
```

## 十、与 H5 端点的关系

`/upload` GET + POST 是 v0.10 保留的 H5 浏览器端点：

- `/upload` GET 返回 HTML 上传页面（PC 端 `Resources/upload.html`）
- `/upload` POST 接收 multipart，**走相同文件落盘逻辑**，但客户端（浏览器）**不需要 secret**

API 端点（`/api/v1/*`）与 H5 端点**共用底层文件落盘**，但**Secret 鉴权**仅 API 端点强制。

如果客户端能连到 PC（任何路由），建议优先用 API 端点（更安全、更可控）。

## 十一、典型错误排查

| 客户端现象 | 原因 | 修法 |
|---|---|---|
| 401 invalid secret | Secret 错 / Secret 重置 / PC 重启 | 删除工作空间，重新扫 PC 端 QR |
| 400 No files uploaded | multipart 格式错 | 检查 `Content-Type: multipart/form-data; boundary=...` |
| 404 project not found | 项目名错 / 项目被删 | 重新 `GET /api/v1/projects` 拿列表 |
| 200 但 `files` 空 / `failed` 有项 | 扩展名不支持 / 太大 | 改文件类型或压缩 |
| 200 但 `media_files` 没新行 | 异步扫描未完成 | 等 1-2 秒再查 |
| 连不上 health | IP / 端口错 / 不在同 LAN | 重新扫码 |
| `name` 字段空 | PC 端 `Environment.MachineName` 异常 | 检查 PC 端系统设置 |
