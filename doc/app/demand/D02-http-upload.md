# D02 · 移动端通过 HTTP 上传文件到 PC 端

> **状态**：需求稿（与 PC v0.12 实现对齐）
> **关联**：[`../02-pc-udp-protocol.md` §三](../02-pc-udp-protocol.md#三http-业务接口用广播拿到的-port--token-访问)、[`../../knowledge-base/API-01-http-routes.md`](../../knowledge-base/API-01-http-routes.md)（HTTP 路由权威）
> **PC 端代码**：`StartTooler/Services/UploadServerService.cs#L226-L292`（路由分发）、`L420-L563`（业务处理）

---

## 0. 元信息

| 项 | 值 |
|---|---|
| 文档版本 | **v0.1（需求稿）** |
| 目标用户 | 拍完想立刻把手机照片 / 视频推到 PC 端入库的用户 |
| 文档状态 | **需求 — 待评审** |
| PC 端能力状态 | ✅ 已实现（v0.12，[UploadServerService.HandleRequestAsync](../../StartTooler/Services/UploadServerService.cs#L216-L306)） |
| App 端目标 | 实现 3 个 API 客户端：health / projects / upload；支持鉴权、错误处理、上传进度 |

---

## 1. 需求总览

### 1.1 背景

| 现状 | 痛点 |
|---|---|
| PC 端已暴露 `/api/v1/health` / `/api/v1/projects` / `/api/v1/projects/{name}/upload` | App 必须实现对应客户端才能完成"选项目 → 上传" |
| 上传成功后 PC 端**异步**触发 `ScanDirectoryAsync` 写入 `media_files` | App 不需要轮询索引；上传响应即可视为"成功入库" |
| H5 浏览器上传仍可用 | 新 App 端不应混用 `/upload` POST，应走 `/api/v1/*` 鉴权通道 |

### 1.2 核心价值

- **批量上传**：一次提交 N 张照片（multipart 内多个 `file` 字段）
- **进度可见**：单次请求级 + 单文件级（chunked multipart）
- **明确失败**：扩展名 / 超大 / 单连接失败各自有原因

### 1.3 一句话概括

**移动端 App 用 UDP 拿到的 port + token，依次调 `health`（验证可达） → `projects`（拉项目列表，含 `isCurrent` 标记） → `projects/{name}/upload`（multipart 多文件上传，异步触发 PC 端扫描入库）。**

---

## 2. 用户场景

### 场景一：选 1 张 RAW 推送到当前项目

> 1. App 主页 → 当前项目 `m42-2025-12-13` 已显示
> 2. 点 [选择照片] → 系统 Photo Picker → 选 1 张 .NEF
> 3. 点 [上传] → 进度条 → "上传成功"
> 4. 用户走到 PC 端 → Gallery 已可见新文件

### 场景二：选 30 张混合照片 + 视频

> 1. 选 28 张 .jpg + 2 个 .mp4
> 2. 上传中显示"已传 18 / 30"
> 3. 全部成功后 Toast "已上传 30 个文件"
> 4. PC 端 `media_files` 异步写入

### 场景三：选 1 个 .txt

> 1. 选 1 张 .jpg + 1 个 notes.txt
> 2. 上传后响应 `failed[]` 含 `{ name: "notes.txt", reason: "unsupported extension .txt" }`
> 3. App 提示"30 个成功，1 个失败（不支持 .txt）"，详情可点开

### 场景四：选 600MB 视频

> 1. 上传后响应 `failed[]` 含 `{ name: "huge.mov", reason: "exceeds 500MB limit" }`
> 2. App 提示"文件过大，PC 端单文件上限 500MB"

---

## 3. 功能需求

### 3.1 必须实现的 3 个 HTTP 接口

#### 3.1.1 `GET /api/v1/health`（**首连验证**）

| 项 | 值 |
|---|---|
| 鉴权 | ❌ 无 |
| URL | `http://{ip}:{port}/api/v1/health` |
| 请求级超时 | **3s** |
| 用途 | 验证 PC 真实可达 + 服务存在（UDP 不可靠） |

**响应 200**：

```json
{
  "ok": true,
  "service": "starttooler",
  "version": "0.12",
  "name": "鱼鱼的 MacBook",
  "port": 8765,
  "token": "123456",
  "currentProject": "m42-2025-12-13"
}
```

**App 端处理**：

- 校验 `service == "starttooler"`、`port == 广播里的 port`、`version` 至少为 `"0.12"`
- **响应里的 `token` 仅作"回包校验"用，不作为主动 token 取值**（权威源仍是 UDP 广播）
- 任何字段不匹配 → 视为不可达
- 响应非 200 / 超时 → 标灰该 PC，可手动重试

#### 3.1.2 `GET /api/v1/projects`（**项目列表**）

| 项 | 值 |
|---|---|
| 鉴权 | ✅ token（`?token=xxx` 或 `X-Token: xxx`） |
| URL | `http://{ip}:{port}/api/v1/projects` |
| 请求级超时 | **5s** |

**响应 200**：

```json
{
  "items": [
    {
      "name": "m42-2025-12-13",
      "path": "/Users/.../m42-2025-12-13",
      "projectName": "M42",
      "fileCount": 1284,
      "sizeMb": 6420,
      "isCurrent": true
    }
  ]
}
```

**App 端处理**：

| 字段 | 处理 |
|---|---|
| `items[].name` | 用于 URL 路径（`/api/v1/projects/{name}/upload`） |
| `items[].path` | UI 副标题展示（绝对路径） |
| `items[].projectName` | **nullable**，缺失时显示文件夹名（basename） |
| `items[].fileCount` | 副标题"1284 个文件" |
| `items[].sizeMb` | 副标题"6.27 GB"（MB 单位，截断精度） |
| `items[].isCurrent` | 默认选中标记 |
| `items` 为空数组 | UI 显示"PC 上还未添加任何项目" |

#### 3.1.3 `POST /api/v1/projects/{name}/upload`（**上传文件**）

| 项 | 值 |
|---|---|
| 鉴权 | ✅ token |
| URL | `http://{ip}:{port}/api/v1/projects/{name}/upload?token=xxx` |
| 请求级超时 | **60s**（500MB 走 LAN 通常 < 60s；不够再延） |
| Content-Type | `multipart/form-data; boundary=...` |

**请求体**：multipart 内多个 `file` 字段，每段一个文件。

**响应 200**：

```json
{
  "success": true,
  "count": 2,
  "files": [
    { "name": "DSC_0001.NEF", "path": "/.../DSC_0001.NEF" }
  ],
  "failed": [
    { "name": "bad.exe", "reason": "unsupported extension .exe" }
  ]
}
```

**App 端处理**：

- `success=true` + `count` 表示成功数；`failed[]` 展示给用户（每项含原因）
- 成功后 PC 端**异步**触发扫描，App 不需要轮询索引
- 单文件超 500MB 走 `failed[]`，不会被服务端接收
- 扩展名不在白名单（12 种）走 `failed[]`

### 3.2 单文件大小限制

- **500MB**（PC 端硬限制，[UploadServerService.cs#L510-L514](file:///Users/hex/code/StartTooler/StartTooler/Services/UploadServerService.cs#L510-L514)）
- App 端**不**做客户端侧拦截——让服务端兜底；如有 UX 需求可在选完文件后客户端先提示"该文件 600MB，将被 PC 端拒绝"

### 3.3 扩展名白名单

PC 端只接受：

```
.jpg .jpeg .png .raw .avi .mp4 .mov .mkv .webm .m4v .mpg .mpeg
```

App 端可以：

- ✅ 客户端过滤：选完文件后**只**把白名单内的文件加入上传队列
- ✅ 服务端兜底：不过滤也行，让 PC 端 `failed[]` 回带
- 推荐：客户端先过滤（节省用户时间），不过滤时 fallback 到失败列表展示

### 3.4 重名策略

PC 端逻辑（[UploadServerService.GetUniqueFileName](../../StartTooler/Services/UploadServerService.cs#L785-L804)）：

- `name.jpg` 存在 → `name_1.jpg`
- 还存在 → `name_2.jpg`
- ……

App 端响应 `files[].name` 字段反映最终落盘文件名（可能与上传名不同），UI 列表展示应以响应为准。

### 3.5 上传进度

| 维度 | 实现建议 |
|---|---|
| 单文件进度 | HTTP client 自带（iOS `URLSession.upload(for:fromFile:)` delegate / Android OkHttp `RequestBody` CountingSink） |
| 总进度 | N 个文件顺序上传时，第 i 个 = `(i-1)/N + currentFile/N` |
| UI | 进度条 + 数字（"已传 18 / 30"），不显示百分比（避免小数） |

### 3.6 项目切换

- 主页 [切换项目] 弹窗：展示 `/api/v1/projects` 列表
- 默认勾选 `isCurrent=true` 的项目
- 切换后主页顶部更新项目名；上传 URL 用新 `name` 替换

---

## 4. 非功能需求

| 维度 | 要求 |
|---|---|
| 单次上传请求级超时 | 60s（500MB 走 LAN 通常足够） |
| 项目列表请求级超时 | 5s |
| 健康检查请求级超时 | 3s |
| 并发 | 单连接串行（multipart 多文件）；不要为每个文件开新连接 |
| 重试 | 401 → 等下一次广播 + 重试一次（详见 D03）；其他错误不自动重试，让用户决定 |

---

## 5. 边界情况

| 场景 | 处理 |
|---|---|
| PC 端返回 401 | 触发 D03 的 Token 状态机（等广播 + 重试一次） |
| PC 端返回 404（project not found） | 项目列表可能已变；提示"项目已失效，请重新选择" |
| PC 端返回 400（multipart 解析失败） | 客户端代码 bug；记日志 + 提示"上传格式错误" |
| PC 端返回 405（方法错） | 客户端代码 bug；记日志 |
| PC 端返回 500 | 提示"PC 服务异常，请稍后重试" |
| 上传中网络断 | 客户端断网错误；已成功文件已落盘；剩余文件未上传 |
| 上传中 PC 关闭 | 客户端断网错误；提示"PC 已离线" |
| `failed[]` 含超大文件 | UI 单独标注"文件过大 (500MB 上限)" |
| `failed[]` 含不支持扩展名 | UI 标注"扩展名不支持" |
| 服务端扫描入库失败 | App 端不感知（PC 端日志）；UI 仍显示"上传成功" |

---

## 6. 不做清单

| 内容 | 理由 |
|---|---|
| 客户端预校验 500MB | 让服务端兜底；客户端仅做 UX 提示 |
| 自动重试上传 | 用户决定，避免重复上传同一文件 |
| 断点续传 / 分片 | MVP 阶段单次上传够用 |
| 缩略图预览 | 超出 D02 范围，留给后续 |
| AI 自动打标触发 | PC 端不暴露此接口 |
| OSS 触发 | 同上 |
| HTTPS / 自签证书 | 家庭 LAN 风险可控 |

---

## 7. 验收标准

- [ ] 上传 1 张 .jpg → PC 端项目目录下 `{yyyy-MM-dd}/` 出现该文件
- [ ] 上传 1 张 .exe → App 收到 `failed[]` 含 `unsupported extension .exe`
- [ ] 上传 600MB 单文件 → App 收到 `failed[]` 含 `exceeds 500MB limit`
- [ ] 上传 2 张同文件名 → PC 端一个落地原名，一个加 `_1` 后缀
- [ ] 上传 30 张混合照片 + 视频 → 全部成功，PC 端 Gallery 1-2 s 内可见
- [ ] PC 端 `/api/v1/projects` 返回空数组 → App 显示"PC 上还未添加任何项目"
- [ ] `projectName` 为 null 时 UI 优雅降级（不显示 null）
- [ ] 切项目后再上传 → URL 中 `{name}` 正确切换
- [ ] 上传中网络断 → 客户端超时；UI 提示明确

---

## 8. 关联文档

- 接口权威：[`../../knowledge-base/API-01-http-routes.md`](../../knowledge-base/API-01-http-routes.md)
- 协议细节：[`../02-pc-udp-protocol.md` §三](../02-pc-udp-protocol.md#三http-业务接口用广播拿到的-port--token-访问)
- 自测清单：[`../03-mobile-checklist.md` §2.4](../03-mobile-checklist.md#24-http-业务调用)
- Token 鉴权：详见 [D03](D03-token-auth.md)