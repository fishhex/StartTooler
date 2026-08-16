# API-03 · UDP 广播协议

PC 端服务运行时，每 2 秒向局域网广播一次 UDP 包，告诉 LAN 内的客户端"我在这里"。本文档定义广播协议。

## 一、协议基本信息

| 项 | 值 |
|---|---|
| 协议 | UDP（IPv4） |
| 端口 | 9876 |
| 目标地址 | `255.255.255.255`（IPv4 广播） |
| 广播间隔 | 2000 毫秒 |
| 启动时机 | PC 端 HTTP 服务启动时 |
| 停止时机 | PC 端 HTTP 服务停止时 |
| 编码 | UTF-8 |
| 内容类型 | JSON |

代码：[UploadServerService.StartUdpBroadcastAsync](../../StartTooler/Services/UploadServerService.cs)。

## 二、为什么用 UDP 广播而不是 mDNS / Bonjour

| 方案 | 优点 | 缺点 |
|---|---|---|
| **UDP 广播**（当前） | 0 配置 / 跨平台 / 1ms 响应 | 不跨路由器 / 不跨 VLAN |
| mDNS / Bonjour | 跨子网（router 支持） | 需要 multicast 配置 / 复杂 |
| 自定义 TCP 发现 | 可靠 | 跨平台实现繁琐 |

家用 LAN 场景（同一 WiFi）下 UDP 广播**足够**。路由器隔离 / 跨子网场景由公网 relay 通道兜底（[06-cross-device-sync.md](06-cross-device-sync.md)）。

## 三、广播 Payload

### 3.1 字段

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

| 字段 | 类型 | 含义 |
|---|---|---|
| `service` | string | 固定 `"starttooler"`（用于区分其他广播） |
| `version` | string | 服务版本（v0.12） |
| `name` | string | 本机机器名（Environment.MachineName） |
| `port` | int | HTTP 监听端口 |
| `token` | string | 6 位数字鉴权 Token |
| `currentProject` | string | 当前激活项目 basename（空 = 无） |

### 3.2 字段命名

**camelCase**（与 HTTP API 一致）。

### 3.3 字段语义

- `name` 用于客户端展示（"鱼鱼的 MacBook"）
- `port` 用于客户端连接 HTTP 服务
- `token` 用于 HTTP API 鉴权
- `currentProject` 用于客户端默认选中

## 四、客户端实现

### 4.1 监听

```python
import socket
import json

sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
sock.bind(("", 9876))

while True:
    data, addr = sock.recvfrom(4096)
    payload = json.loads(data)
    if payload.get("service") != "starttooler":
        continue
    print(f"发现 PC: {payload['name']} @ {addr[0]}:{payload['port']}")
```

### 4.2 客户端要求

| 平台 | 限制 |
|---|---|
| iOS | 需要 `NSLocalNetworkUsageDescription`（Info.plist） |
| Android | 需要 `INTERNET` 权限 + Android 13+ `POST_NOTIFICATIONS` |
| macOS | 首次需"允许"网络发现通知 |
| Windows | 防火墙可能拦截，需要用户手动允许 |
| Linux | 无特殊限制 |

### 4.3 客户端发现策略

每 2 秒收到一次广播 = PC 在线。**超时窗口** = 6 秒（3 次未广播）= 标记 PC 离线。

## 五、连接协议

客户端拿到广播后，应该做以下步骤：

```
1. UDP 监听收到广播
   ↓ 拿到 ip / port / token
2. GET /api/v1/health 验证 PC 真实可达 + Token 有效
   ↓ 拿到 health 响应（确认 version/port 匹配）
3. GET /api/v1/projects?token=xxx 拿项目列表
   ↓ 展示给用户
4. 用户选文件 → POST /api/v1/projects/{name}/upload?token=xxx
```

详见 [API-01-http-routes.md](API-01-http-routes.md)。

### 为什么不直接用 UDP 传 token

UDP **不可靠**：

- Token 走明文 → LAN 嗅探风险
- 大文件不能走 UDP
- 需要 100% 可靠 → 走 TCP（HTTP）

UDP 只做"发现" + "传 Token 给客户端"（一次性）。后续 HTTP 走 TCP。

## 六、广播过滤

### 6.1 多 PC 场景

同一 LAN 内可能多台 PC 跑 StartTooler。客户端会同时收到多个广播。

```
┌─ 客户端 ─────────────────────┐
│ 发现的 PC：                  │
│  ◉ 鱼鱼的 MacBook            │
│  ◯ 工作室 Win11               │
│  ◉ 阳台 NUC                  │
│ 选中：鱼鱼的 MacBook         │
│ Token: [123456     ]         │
│ [连接]                       │
└─────────────────────────────┘
```

客户端应该按 `name` + `port` 区分 PC；同一 `name` 多次出现（断电换 IP）应**保留最新 IP**。

### 6.2 持久化

客户端可持久化"上次连接的 PC"：

| 平台 | 存储 |
|---|---|
| iOS | Keychain |
| Android | EncryptedSharedPreferences |
| Web | localStorage（不推荐，泄漏敏感） |

App 启动时：

```
1. 读持久化 IP+Token
2. UDP 扫描 5 秒
3. 找到匹配 IP → 试探 GET /api/v1/health
   ↓ 成功 → 直接进主页
   ↓ 失败 → 跳到连接页
```

## 七、断连 / 重连

### 7.1 客户端断连

| 场景 | 客户端反应 |
|---|---|
| PC 关机 / 换 WiFi | 广播停止 → 客户端 6 秒后标离线 |
| PC 端 HTTP 服务异常 | 广播继续发 → 客户端 `health` 探活失败 |
| App 后台 / 屏幕关 | 操作系统暂停 UDP 监听 → 回到前台后立即静默重连 |

### 7.2 客户端主动断连

- 切换 PC → 调 `health` 试探新 PC，token 留待用户换
- 退出 App → 停止监听，token 持久化保留

### 7.3 失败回退

UDP 扫描 5 秒**没有任何响应** → 引导用户"手动输入 IP"：

```
┌─ 手动输入 ──────────────────┐
│ IP / 域名: [                 ] │
│ 端口:      [8765]            │
│ Token:    [                   ] │
│ [连接]                       │
└─────────────────────────────┘
```

这是兜底方案（路由器隔离 / 跨子网 / 防火墙拦截时）。

## 八、安全边界

### 8.1 Token 暴露风险

- **明文广播**：Token 在 UDP 包里以明文传播
- **同 LAN 嗅探**：同路由器下的其他客户端可读取 Token
- **缓解**：6 位数字 token 是临时凭据，重置就失效
- **未来**：可改用短时 ECDH 协商，但当前规模没必要

### 8.2 不防的场景

- ❌ 不防 LAN 内的中间人攻击（无 TLS）
- ❌ 不防跨网段访问（UDP 不跨路由）
- ❌ 不防广播包伪造（无消息签名）

星助定位是**单人本地工具**，防这些成本太高。

### 8.3 适合的场景

- ✅ 家庭 WiFi 临时传文件
- ✅ 工作室多机协作
- ✅ 拍摄现场手机直传

## 九、客户端实现注意事项

### 9.1 防火墙

Windows 默认家庭组网放行 UDP 广播。**Windows 域网络 / 公共网络**默认拦截——需要用户手动放行：

```powershell
# Windows 防火墙放行 9876
New-NetFirewallRule -DisplayName "StartTooler UDP Discovery" `
  -Direction Inbound -Protocol UDP -LocalPort 9876 -Action Allow
```

macOS 默认放行。Linux 通常无防火墙。

### 9.2 多网卡

复杂网络下（多网卡、有线 + 无线）UDP 广播可能走错网卡——客户端需在所有网卡上监听。

### 9.3 移动端权限

iOS 14+ 引入"本地网络"权限。首次扫描会被弹窗拦：

- iOS 应用需 `NSLocalNetworkUsageDescription`（Info.plist）
- Android 13+ 需 `POST_NOTIFICATIONS` + 适配后台

## 十、协议扩展

未来可能的扩展：

### 10.1 加密 Token

用公钥加密 Token，客户端用 PC 公钥解密。增加嗅探成本。

### 10.2 mDNS 兼容

并行发 mDNS 包，支持 macOS / iOS 原生发现。

### 10.3 服务元信息

```json
{
  ...现有字段,
  "capabilities": ["upload", "project-list", "ai-tagging"],
  "region": "zh-CN",
  "model": "Orion"
}
```

标识 PC 端能力 + 区域 + 设备型号。

### 10.4 心跳保活

- 当前：每 2s 广播一次，断电就停
- 未来：广播 + 心跳分离（更细粒度）

## 十一、版本兼容性

- 字段新增：客户端应忽略未知字段，向后兼容
- 字段删除：破坏性变更，需 major version bump
- `service` 字段名变化：破坏性变更（不同应用冲突）

## 十二、调试工具

抓包 UDP 广播（macOS）：

```bash
sudo tcpdump -i en0 -n udp port 9876 -A
```

模拟客户端（Python 一行）：

```bash
python3 -c "import socket; s=socket.socket(socket.AF_INET, socket.SOCK_DGRAM); s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1); s.bind(('', 9876)); print(s.recvfrom(4096))"
```

发送测试广播（验证 PC 端广播）：

```bash
# 注：仅用于本地测试；不能模拟真实 PC 广播
echo '{"service":"starttooler","version":"0.12","name":"test","port":8765,"token":"123456","currentProject":"test"}' | nc -u -b 255.255.255.255 9876
```

## 十三、典型客户端代码骨架

```kotlin
// Android Kotlin 示例
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

至此 UDP 广播协议 + HTTP API 完整闭环。客户端只需 5 个 API（H5 端点）+ 1 个 UDP 广播 = 完整对接。
