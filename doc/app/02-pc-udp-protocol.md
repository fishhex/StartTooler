# PC UDP 发现协议 v0.12

> 本文档**严格对齐 PC 当前实现** ([UploadServerService.cs](../../StartTooler/Services/UploadServerService.cs)、KB [`API-03`](file:///Users/hex/code/StartTooler/doc/knowledge-base/API-03-udp-broadcast.md))。
>
> 协议采用 **单向 UDP 广播 + HTTP 短连接** 两段式：广播用于发现，HTTP 用于业务/鉴权。**没有加密、没有签名、没有 nonce**——文档同时标注风险与改进方向。

## 一、协议基本信息

| 项 | 值 | 代码位置 |
|---|---|---|
| 协议 | UDP（IPv4） | `UdpClient` |
| 目标地址 | `255.255.255.255`（IPv4 广播） | `IPAddress.Broadcast` |
| 端口 | **9876** | `UdpBroadcastPort = 9876` |
| 广播间隔 | **2000 ms** | `UdpBroadcastIntervalMs = 2000` |
| 启动时机 | PC 端 HTTP 服务 `StartAsync()` | `_udpTask = StartUdpBroadcastAsync(...)` |
| 停止时机 | PC 端 `Stop()` / 服务关闭 | `_udpCts?.Cancel()` + `_udpClient?.Close()` |
| 编码 | UTF-8 | `JsonSerializer.SerializeToUtf8Bytes` |
| 命名 | camelCase | `JsonOpts.PropertyNamingPolicy = CamelCase` |
| 包格式 | JSON | 单包 ≤ ~150B |
| 鉴权方式 | 6 位数字 Token（仅 HTTP 路径） | `ValidateToken` / `RegenerateToken` |

## 二、广播 Payload

### 2.1 字段

```json
{
  "service": "starttooler",
  "version": "0.12",
  "name": "鱼鱼的 MacBook",
  "port": 8765,
  "token": "123456",
  "currentProject": "m42-2025-12-13"
}
```

字段定义（与 [`UdpBroadcastPayload`](file:///Users/hex/code/StartTooler/StartTooler/Services/UploadServerService.cs#L897-L905) DTO 严格一致）：

| 字段 | 类型 | 含义 | 取值 |
|------|------|------|------|
| `service` | string | 应用标识 | 固定 `"starttooler"`，客户端需据此过滤其他广播 |
| `version` | string | 服务版本号 | 当前 `"0.12"` |
| `name` | string | PC 显示名 | `Environment.MachineName` |
| `port` | int | HTTP 监听端口 | `StartAsync` 入参（默认 8765） |
| `token` | string | 6 位数字鉴权 Token | `Random.Shared.Next(0, 1_000_000).ToString("D6")` |
| `currentProject` | string | 当前激活项目 basename | `Path.GetFileName(cfg.CurrentDirectory)`；无项目时为空串 `""` |

### 2.2 不可变规则

- **Token 会变更**：进程启动（构造函数 + `StartAsync`）以及用户手动 `RegenerateToken()` 时变更；因广播每 2 s 周期重读 `_currentToken`，**新 token 最迟 2 s 内出现在下一次广播中**。客户端不要硬编码。
- **`name` 是 `MachineName`**：可能重复；同一 LAN 多台 PC 用 `name + port + ip` 三元组去重。
- **`currentProject` 可能为空**：未配置项目时为 `""`，客户端需按空串处理（不是 null）。
- **字段命名 camelCase**：与 HTTP API 保持一致。

### 2.3 Token 来源与权威性（与 CheckList §2.3 配对阅读）

> 上游事实（[`UploadServerService.cs`](file:///Users/hex/code/StartTooler/StartTooler/Services/UploadServerService.cs)）：PC 端**只有一个** token 实例字段 `_currentToken`，UDP 广播和 `/api/v1/health` 响应**都从同一字段读取**，不存在"两个 token"。

约束清单：

- **权威源**：UDP 广播里的 `token` 字段是 App 端**唯一权威**的 token 取值。
- **`/api/v1/health` 响应里的 `token`**：仅作"与权威源回包校验"用途（健康检查本身无需 token，回带仅为客户端对比方便），不应作为主动取舍的依据。
- **覆盖式更新**：App 端每次收到新广播都**无条件覆盖**内存中的 token（不要做合并、不要做 diff）。
- **不要做主动轮询**：PC 端未提供"取 token"接口，App 端不应自造轮询路径——只需被动等待广播。
- **同名但 IP 变化**：IP 是浮动的；通过 `name + port` 识别同一 PC，仍用广播 token 覆盖。
- **完整状态机 / 持久化 / 401 重试**：见 [`03-mobile-checklist.md` §2.3](file:///Users/hex/code/StartTooler/doc/app/03-mobile-checklist.md#L41-L58)。

## 三、HTTP 业务接口（用广播拿到的 port + token 访问）

### 3.1 接口清单（来自 `HandleRequestAsync`）

| Method | Path | 鉴权 | 说明 |
|---|---|---|---|
| GET | `/upload` | ❌ | H5 上传页面（HTML，含 `{{STARTOOLER_BASE}}` 占位符被替换为 `http://{localIp}:{port}`） |
| GET | `/api/v1/health` | ❌ | PC 健康检查（无需 token），客户端**首连验证用** |
| GET | `/api/v1/projects` | ✅ token | 列出最近项目目录 |
| POST | `/api/v1/projects/{name}/upload` | ✅ token | 上传到指定项目（multipart/form-data） |
| POST | `/upload` | ❌（沿用 v0.10 行为） | H5 文件上传回退，按日期写入默认目录 |

### 3.2 鉴权约定（来自 `ValidateToken`）

- 受保护接口必须带 **Token**：`?token=123456` 或 HTTP Header `X-Token: 123456`（二者择一即可；同时带以前者为准——代码先读 queryString，再读 Headers）。
- 401 响应：`{ "error": "invalid token" }`。**App 端 401 处理策略**：等下一次广播（≤ 2 s）拿到新 token 后**重试当前请求一次**；仍 401 走用户兜底提示（提示"PC 已拒绝当前 Token"并引导重新发现）。详见 [§2.3 + CheckList §2.3](file:///Users/hex/code/StartTooler/doc/app/03-mobile-checklist.md#L41-L58)。
- 已知路径但方法不对：405 `{ "error": "method not allowed" }`。
- 路径不存在：404 `{ "error": "not found" }`。
- 系统异常：500 `{ "error": "..." }`。

### 3.3 主要响应 DTO

`/api/v1/health`（无鉴权，**移动端验证可达性**用）：

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

`/api/v1/projects`：

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

字段注意：

- `projectName` 为 nullable string（DTO `string?`，见 [UploadServerService.cs#L872-L880](../../StartTooler/Services/UploadServerService.cs#L872-L880)）：用户可能未在 PC 端填项目显示名。客户端需处理 `null` / 缺失情况，不要按空串渲染。
- `sizeMb` 为 `long`，单位 MB，截断精度（不展示原始字节）。
- `fileCount` 为 `long`，可能为 0（项目目录尚未被扫描入库时）。

`/api/v1/projects/{name}/upload`（POST，multipart）：

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

### 3.4 文件上传约束（来自代码）

- 单文件大小限制：**500MB**（`UploadServerService.cs#L510-L514`，`sizeHint > 500L * 1024 * 1024`）。
- 支持扩展名白名单：`.jpg .jpeg .png .raw .avi .mp4 .mov .mkv .webm .m4v .mpg .mpeg`。
- 文件落在 `{projectPath}/{yyyy-MM-dd}/` 下，重名自动追加 `_1`/`_2`...
- 上传成功后**异步**触发 `ScanDirectoryAsync` 写库（不阻塞响应）。

## 四、安全边界（必须告知移动端）

| 风险 | 现状 | 建议实现期补 |
|---|---|---|
| Token 明文广播 | 同一 LAN 任意客户端可读到 Token | 仅作 LAN 临时凭据；通过 PC 端"重置"按钮撤销 |
| Token 明文 HTTP | LAN 嗅探可见 query string / X-Token | 改 HTTPS / 应用层加密 |
| `/api/v1/health` 响应明文回带 token | 与广播 / HTTP 受保护接口同源 `_currentToken`，LAN 嗅探同样可见 | 应用层加 AEAD 或改 HTTPS；最低限度不在 3rd-party 日志回放 |
| 广播伪造 | 同 LAN 客户端可伪装成 PC | 加 ECDH / 应用层签名 |
| 广播重放 | 没有 nonce / 时间戳 | 加时间戳窗口或 nonce |
| 大文件滥用 | 服务端 500MB 限制 | 已生效；客户端需做 UI 提示 |
| 跨公网 | UDP 广播不跨路由器 | 必须走 [cross-device-sync relay](file:///Users/hex/code/StartTooler/doc/knowledge-base/06-cross-device-sync.md) |

⚠️ **请在移动端明确**：本协议定位为**单人本地工具**，KB `API-03 §八.2` 已写明：

> ❌ 不防 LAN 内的中间人攻击（无 TLS）
> ❌ 不防跨网段访问（UDP 不跨路由）
> ❌ 不防广播包伪造（无消息签名）

## 五、典型移动端实现

### 5.1 发现（UDP 监听）

```python
import socket, json

sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
sock.bind(("", 9876))

while True:
    data, addr = sock.recvfrom(4096)
    payload = json.loads(data)
    if payload.get("service") != "starttooler":
        continue
    # payload["name"], payload["port"], payload["token"], payload["currentProject"]
    # addr[0] 即广播源 IP
    print(f"发现 PC: {payload['name']} @ {addr[0]}:{payload['port']}")
```

### 5.2 验证 + 拉项目列表 + 上传（HTTP）

```python
import requests

BASE = "http://192.168.1.10:8765"

# 1) 健康检查（无 token）。仅作"真实可达"验证，
#    不从这里取 token —— token 的唯一权威来源是 §2.1 的 UDP 广播 payload。
r = requests.get(f"{BASE}/api/v1/health", timeout=3).json()
assert r["service"] == "starttooler" and r["port"] == 8765

# 2) token 必须从 UDP 广播 payload["token"] 取（§2.3 权威来源），
#    这里写死仅用于 demo；真实 App 应从广播回调里注入。
TOKEN = "123456"

# 3) 项目列表
r = requests.get(f"{BASE}/api/v1/projects", params={"token": TOKEN}, timeout=5).json()
projects = r["items"]

# 4) 上传文件
with open("IMG_0001.NEF", "rb") as f:
    r = requests.post(
        f"{BASE}/api/v1/projects/{projects[0]['name']}/upload",
        params={"token": TOKEN},
        files={"file": ("IMG_0001.NEF", f, "image/x-raw")},
        timeout=60,
    ).json()
print(r["count"], "files uploaded")
```

### 5.3 Android Kotlin 片段（来自 KB `API-03 §十三`）

```kotlin
class DiscoveryService {
    private val port = 9876
    private val timeout = 6000L  // 6 秒无广播 = 离线

    fun discover(): Flow<List<PC>> = callbackFlow {
        val socket = DatagramSocket(port)
        socket.broadcast = true
        val buffer = ByteArray(4096)

        while (isActive) {
            val packet = DatagramPacket(buffer, buffer.size)
            socket.receive(packet)
            val payload = Json.parseToJsonElement(String(packet.data, packet.offset, packet.length))
            val pc = PC(
                ip = packet.address.hostAddress,
                name = payload["name"]?.jsonPrimitive?.content ?: "",
                port = payload["port"]?.jsonPrimitive?.int ?: 8765,
                token = payload["token"]?.jsonPrimitive?.content ?: "",
                project = payload["currentProject"]?.jsonPrimitive?.content ?: ""
            )
            trySend(pc)
        }
    }
}
```

## 六、超时 / 离线判定

- 收到 1 次广播 → 视为上线
- **6 秒（3 次）未收到广播 → 标记 PC 离线**（KB §4.3 / §7）
- App 切前台后应保持 UDP 监听；操作系统暂停监听时回到前台需重新建立

## 七、调试

抓包（macOS）：

```bash
sudo tcpdump -i en0 -n udp port 9876 -A
```

模拟客户端（Python 一行）：

```bash
python3 -c "import socket; s=socket.socket(socket.AF_INET, socket.SOCK_DGRAM); s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1); s.bind(('', 9876)); print(s.recvfrom(4096))"
```

发送测试广播（仅本地测试用）：

```bash
echo '{"service":"starttooler","version":"0.12","name":"test","port":8765,"token":"123456","currentProject":"test"}' | nc -u -b 255.255.255.255 9876
```

## 八、版本兼容（KB §十一）

- 字段新增：客户端应忽略未知字段，向后兼容
- 字段删除 / `service` 改名：**破坏性变更**，需 major version bump

## 九、移动端对接指引

参见 [`03-mobile-checklist.md`](file:///Users/hex/code/StartTooler/doc/app/03-mobile-checklist.md)（已重写为对齐本协议）。
