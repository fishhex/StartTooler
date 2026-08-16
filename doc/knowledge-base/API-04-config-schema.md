# API-04 · 配置文件 Schema

星助的配置存在 **SQLite** (`config.db`) 的 `config` 表里。**不是 JSON 文件**——所有用户能修改的设置都进 SQL 库。

本文档是配置的 schema 定义：哪些 key、字段含义、默认值、迁移规则。

## 一、存储结构

### 1.1 数据库

```
路径：AppPaths.ConfigDbPath
       Windows：%APPDATA%\StartTooler\config.db
       macOS：~/Library/Application Support/StartTooler/config.db
       Linux：~/.config/StartTooler/config.db
```

代码：[AppPaths.cs](../../StartTooler/Services/AppPaths.cs)。

### 1.2 表结构

```sql
CREATE TABLE IF NOT EXISTS config (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL,
    created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);
```

- `key`：配置名（字符串）
- `value`：JSON 序列化内容（字符串）
- `created_at` / `updated_at`：ISO 8601 UTC 字符串

代码：[ConfigService.cs:34-38](../../StartTooler/Services/ConfigService.cs#L34)。

### 1.3 值序列化

每个 key 的 `value` 是**对应类的 JSON 序列化**（camelCase）。读取时反序列化成具体类型。

```csharp
// 读取
var proj = await configService.GetAsync<ProjectConfig>(ConfigKeys.Project);

// 写入
await configService.SetAsync(ConfigKeys.Project, proj);
```

代码：[IConfigService](../../StartTooler/Services/IConfigService.cs)。

## 二、Key 列表

| Key | 类型 | 用途 |
|---|---|---|
| `project` | `ProjectConfig` | 当前项目 |
| `app` | `AppConfig` | 应用全局设置 |
| `oss` | `OssConfig` | 阿里云 OSS 配置 |
| `publicRelay` | `PublicRelayConfig` | 公网代理配置 |
| `ai` | `AIConfig` | AI 打标厂商配置 |
| `project_history` | `List<string>` | 项目历史路径 |
| `dont_ask_again` | `DontAskAgain` | 一次性提示的"不再问" |
| `onboarding_v1` | `OnboardingState` | 首次打开引导状态 |
| `diary_amap_api_key` | `string` | 高德地图 API Key |

代码：[ConfigKeys.cs](../../StartTooler/Services/ConfigKeys.cs)。

## 三、ProjectConfig

### 3.1 字段

```json
{
  "currentDirectory": "/Users/hex/Astro/m42-2025-12-13",
  "projectName": "猎户 M42",
  "recentDirectories": [
    "/Users/hex/Astro/m42-2025-12-13",
    "/Users/hex/Astro/ngc7000",
    "/Users/hex/Astro/m51"
  ]
}
```

| 字段 | 类型 | 必填 | 默认 | 含义 |
|---|---|---|---|---|
| `currentDirectory` | string | 否 | `""` | 当前激活项目目录的绝对路径 |
| `projectName` | string&#124;null | 否 | `null` | 当前项目的用户给起的名字（v0.12 D02 引入） |
| `recentDirectories` | string[] | 是 | `[]` | 最近打开过的项目目录列表 |

代码：[ProjectConfig.cs](../../StartTooler/Services/ProjectConfig.cs)。

### 3.2 业务语义

- `currentDirectory` 切换 = 重新打开 Setting 选目录
- `recentDirectories` 长度上限 10，超过 FIFO
- `projectName` 跨设备用——LAN 上传响应里返回

## 四、AppConfig

### 4.1 字段

```json
{
  "theme": "DeepSpace",
  "ffmpegPath": null,
  "ffprobePath": null,
  "sessionIntervalHours": 4,
  "amapApiKey": null
}
```

| 字段 | 类型 | 默认 | 含义 |
|---|---|---|---|
| `theme` | string | `"DeepSpace"` | 主题名（`DeepSpace` / `RedNightVision`） |
| `ffmpegPath` | string&#124;null | `null` | ffmpeg 可执行文件绝对路径（空 = 走 PATH） |
| `ffprobePath` | string&#124;null | `null` | ffprobe 同上 |
| `sessionIntervalHours` | int | 4 | 拍摄会话间隔阈值（小时） |
| `amapApiKey` | string&#124;null | `null` | 高德地图 API Key（空 = 走 Nominatim 兜底） |

代码：[AppConfig.cs](../../StartTooler/Services/AppConfig.cs)。

### 4.2 字段语义

- `theme`：UI 主题切换
- `ffmpegPath` / `ffprobePath`：本地有 ffmpeg 不用 PATH 时填
- `sessionIntervalHours`：[SessionClusteringService](../../StartTooler/Services/SessionClusteringService.cs) 用，间隔超过 N 小时视为不同会话
- `amapApiKey`：日记功能用，逆地理编码（经纬度 → 地名）

## 五、OssConfig

### 5.1 字段

```json
{
  "provider": "Aliyun",
  "region": "oss-cn-hangzhou",
  "bucket": "my-astro-bucket",
  "accessKeyId": "LTAI5tXXXXXXXXXXXXX",
  "accessKeySecret": "XXXXXXXXXXXXXXXXXXXXXXXX",
  "pathPrefix": "astro-photos/"
}
```

| 字段 | 类型 | 默认 | 含义 |
|---|---|---|---|
| `provider` | string | `"Aliyun"` | 厂商（当前只支持 Aliyun） |
| `region` | string | `""` | 阿里云 Region |
| `bucket` | string | `""` | 桶名 |
| `accessKeyId` | string | `""` | AK |
| `accessKeySecret` | string | `""` | AS |
| `pathPrefix` | string | `""` | 桶内前缀（多项目共享桶时区分） |

代码：[OssConfig.cs](../../StartTooler/Services/OssConfig.cs)。

### 5.2 敏感字段

- `accessKeySecret` 是写入**明文**（**SQLite 文件本身无加密**）
- 泄露风险：电脑被入侵直接 dump config.db
- **缓解**：建议阿里云 RAM 子账号 + 桶读写策略

### 5.3 缺字段时

| 字段填错 | 现象 |
|---|---|
| `region` 错 | 404 ObjectNotFound |
| `bucket` 错 | 403 NoSuchBucket |
| `accessKeyId` 错 | 403 InvalidAccessKeyId |
| `accessKeySecret` 错 | 403 SignatureDoesNotMatch |
| `pathPrefix` 错 | 上传到的文件夹不对 |

## 六、PublicRelayConfig

### 6.1 字段

```json
{
  "sshHost": "vps.example.com",
  "sshPort": 22,
  "sshUser": "root",
  "sshPassword": null,
  "sshKeyPath": "/Users/hex/.ssh/id_rsa",
  "sshRemotePath": "~/starttooler",
  "httpPort": 8765,
  "tcpPort": 8766,
  "publicHost": "vps.example.com",
  "remoteArch": "auto",
  "syncPollIntervalSec": 5,
  "syncBatchSize": 5
}
```

| 字段 | 类型 | 默认 | 含义 |
|---|---|---|---|
| `sshHost` | string | `""` | VPS 域名/IP |
| `sshPort` | int | 22 | SSH 端口 |
| `sshUser` | string | `""` | SSH 用户 |
| `sshPassword` | string&#124;null | `null` | SSH 密码（key 优先） |
| `sshKeyPath` | string&#124;null | `null` | SSH 私钥路径 |
| `sshRemotePath` | string | `"~/starttooler"` | VPS 上部署目录 |
| `httpPort` | int | 8765 | upload-relay HTTP 端口 |
| `tcpPort` | int | 8766 | upload-relay TCP 端口 |
| `publicHost` | string&#124;null | `null` | 公网域名（空 = 用 sshHost） |
| `remoteArch` | string | `"auto"` | VPS CPU 架构：`auto` / `amd64` / `arm64` |
| `syncPollIntervalSec` | int | 5 | 同步轮询间隔（秒） |
| `syncBatchSize` | int | 5 | 每批 SCP 文件数 |

代码：[PublicRelayConfig.cs](../../StartTooler/Services/PublicRelayConfig.cs)。

### 6.2 字段语义

- `sshPassword` + `sshKeyPath` 二选一，key 优先
- `remoteArch` 默认自动检测（`uname -m` 推断）
- `syncPollIntervalSec` / `syncBatchSize` 控制带宽，避免打爆 VPS

## 七、AIConfig

### 7.1 字段

```json
{
  "provider": "Anthropic",
  "apiKey": "sk-ant-XXXXXXXX",
  "baseUrl": "",
  "model": "claude-sonnet-4-5",
  "protocol": "Anthropic",
  "testPrompt": "请分析这张天文照片的主体、质量和拍摄参数"
}
```

| 字段 | 类型 | 默认 | 含义 |
|---|---|---|---|
| `provider` | string | `"Anthropic"` | 厂商枚举字符串 |
| `apiKey` | string | `""` | 对应厂商 API Key |
| `baseUrl` | string | `""` | API 端点（空 = 厂商默认） |
| `model` | string | `""` | 模型名（空 = 厂商推荐列表第一个） |
| `protocol` | string | `""` | API 协议：`OpenAI` / `Anthropic`（空 = 强制用户选） |
| `testPrompt` | string | 中文默认 | 连接测试 prompt（v0.11 起用户可改） |

代码：[AIConfig.cs](../../StartTooler/Services/AIConfig.cs)。

### 7.2 字段语义

- `provider` 枚举值：`Anthropic` / `OpenAI` / `Dashscope` / `DeepSeek` / `Volcengine` / `Zhipu` / `Siliconflow` / `Custom`
- `protocol` **不允许默认值**——空字符串强制用户选，避免误打
- `testPrompt` 用于"测试连接"按钮

### 7.3 老 config 兼容

`anthropic` key（v0.6 老格式）已废弃，**不再读取**。旧数据保留在 db 不动。

老 db 反序列化 `protocol` 缺失 → 默认空串 → UI 强制让用户选。

## 八、project_history

```json
[
  "/Users/hex/Astro/m42-2025-12-13",
  "/Users/hex/Astro/ngc7000",
  "/Users/hex/Astro/m51"
]
```

`List<string>`，项目路径列表，**与 ProjectConfig.RecentDirectories 同步**。

代码：[ConfigKeys.cs](../../StartTooler/Services/ConfigKeys.cs)。

## 九、dont_ask_again

```json
{
  "deleteConfirm": true,
  "publicRelayDeployConfirm": true
}
```

每个 bool 表示一个"不再问"对话框被勾选过。

代码：[DontAskAgainService.cs](../../StartTooler/Services/DontAskAgainService.cs)。

## 十、onboarding_v1

```json
{
  "currentStep": 3,
  "completed": false,
  "firstOpenAt": "2025-12-13T22:30:00.000Z"
}
```

| 字段 | 含义 |
|---|---|
| `currentStep` | 用户停在哪一步（0-indexed） |
| `completed` | 是否完成全部引导 |
| `firstOpenAt` | 首次打开时间 |

代码：[OnboardingState.cs](../../StartTooler/Models/OnboardingState.cs)。

## 十一、diary_amap_api_key

```json
"a1234567890abcdef1234567890abcdef"
```

保留字段，**实际读取在 AppConfig.AmapApiKey**。此 key 后续可能移除。

代码：[ConfigKeys.cs:39](../../StartTooler/Services/ConfigKeys.cs#L39)。

## 十二、迁移规则

### 12.1 字段新增

向后兼容——老 db 没字段时，`System.Text.Json` 反序列化给字段默认值。

```csharp
public class OssConfig {
    public string Region { get; set; } = "";  // 老 db 没这字段 → 默认 ""
}
```

### 12.2 字段删除

破坏性变更。要走迁移：

1. 老字段从 db 读
2. 新字段缺失给默认值
3. 显式 set 新字段回 db

### 12.3 字段重命名

类似字段删除——读老字段映射到新字段。

### 12.4 config 表迁移

代码：[ConfigService.cs:25-100](../../StartTooler/Services/ConfigService.cs#L25)。

历史迁移：

- PascalCase 表名 `Config` → 小写 `config`（10-trap-book.md §5）
- `Key` → `key` 列名
- `Value` → `value` 列名
- `UpdatedAt` → `updated_at` 列名
- `created_at` 列补齐

## 十三、备份与恢复

### 13.1 备份

直接复制 `config.db` 文件即可。所有配置都在一张表。

### 13.2 跨设备迁移

把 `config.db` 复制到另一台电脑相同位置即可——但：

- `recentDirectories` 路径可能在新电脑不存在（手动清理）
- `oss.accessKeyId` 可复用
- `currentDirectory` 需手动调整

### 13.3 加密

**当前未加密**。SQLite 文件 = 明文 JSON。

未来可选：

- SQLCipher 加密
- 系统级 Keychain（macOS）/ DPAPI（Windows）存敏感字段

## 十四、调试图表

读 config.db：

```bash
# macOS
sqlite3 ~/Library/Application\ Support/StartTooler/config.db

# 查所有 key
sqlite> SELECT key, value FROM config;

# 查项目历史
sqlite> SELECT value FROM config WHERE key='project';

# 手动改
sqlite> UPDATE config SET value='{"currentDirectory":"/new/path"}' WHERE key='project';
```

通过 [DbInspectorService](../../StartTooler/Services/DbInspectorService.cs)，星助 UI 自带 DB Inspector 也可查。

## 十五、API 客户端读取

API 客户端**不能直接读 config.db**——属于私有存储。客户端通过：

- HTTP API `/api/v1/health` 拿 Token（不读配置）
- HTTP API `/api/v1/projects` 拿项目列表（由 StartTooler 把 config 翻译成 API 响应）

详见 [API-01-http-routes.md](API-01-http-routes.md)。
