# 移动端实现 CheckList（v0.12 PC 对接）

> 本文档**严格对齐 PC 当前实现** ([UploadServerService.cs](../../StartTooler/Services/UploadServerService.cs))。CheckList 反映的是"真实协议"——无加密无签名——而不是理想协议。

## 一、对接前置

- [ ] 已通读 [`02-pc-udp-protocol.md`](file:///Users/hex/code/StartTooler/doc/app/02-pc-udp-protocol.md)，明确 PC 端协议现状与安全边界
- [ ] 设备与 PC 处于**同一 LAN 子网**（UDP 广播不跨路由器）
- [ ] PC 端 **HTTP 服务已启动**（UDP 广播才会启动；如未启动，App 找不到设备）
- [ ] 与 PC 约定好 HTTP 端口（PC 端默认 8765，运行时可调整）
- [ ] 准备好 6 位数字 **Token**（从广播 JSON 中取，或在 PC 端 Upload 设置页查看）

## 二、协议实现要点

### 2.1 UDP 发现

- [ ] **监听端口 `9876`**（KB `API-03 §四.1`、代码 `UdpBroadcastPort = 9876`）
- [ ] `setsockopt(SOL_SOCKET, SO_REUSEADDR, 1)` 后再 `bind`
- [ ] 收到 JSON 后**先校验 `service == "starttooler"`**，否则丢弃（避免其他广播干扰）
- [ ] 解析 `name` / `port` / `token` / `currentProject`，源 IP 用 socket 收到的 `addr[0]`
- [ ] `currentProject` 可能为空串 `""`——客户端按空串处理，不要按 null
- [ ] **去重**：以 `name + port + ip` 三元组为 key 合并（同 LAN 多台 PC 可能同名）

### 2.2 HTTP 验证（首连）

- [ ] 用广播拿到的 `http://{addr}:{port}` 调 `GET /api/v1/health`（**无鉴权**）
- [ ] 校验响应：`service == "starttooler"`、`port` 与广播一致、`version >= "0.12"`
- [ ] **请求级超时 3s**（仅本次 HTTP 请求的最大等待）；非 200 / 超时视为不可达，灰显该 PC

> 各处时间窗总结（在 §2 / §6 / §7 里都用得到）：
>
> | 时间窗 | 含义 | 出处 |
> |---|---|---|
> | **3s** | 单次 `GET /api/v1/health` 请求级超时 | §2.2 |
> | **5s** | 单次 `GET /api/v1/projects` 请求级超时 | §2.4 |
> | **60s** | 单次 multipart 上传请求级超时（500MB 走 LAN 通常 < 60s；实在不够再延） | §2.4 |
> | **2s** | PC 端 UDP 广播周期；新 Token 最迟 2s 反映到下一次广播 | §2.3 |
> | **6s（3 个广播周期）** | "未收到广播 → 离线" 判定 | §2.5 |
> | **5s** | 用户感知级"看不到任何 PC"阈值（仅 UX 节奏用，不与上面技术超时重叠） | §6 |

### 2.3 Token 更新策略（以 PC 实现为准）

> 上游事实（[`UploadServerService.cs`](file:///Users/hex/code/StartTooler/StartTooler/Services/UploadServerService.cs)）：PC 端只有 **一个** token 实例字段 `_currentToken`，广播包和 `/api/v1/health` 响应都从同一字段读取，**不存在"两个 token"**。Token 仅在 `GenerateToken()` / `RegenerateToken()` 时变化，并通过 `OnTokenChanged` 事件同步 UI；广播每 2 s 周期重读，因此**新 token 最迟 2 s 内会出现在下一次广播中**。

App 端实现要求：

- [ ] **权威来源**：UDP 广播中的 `token` 字段是 App 端唯一权威的 token 值；`/api/v1/health` 响应里的 `token` 仅用于校验（健康检查本身无需 token，但响应带回便于对比）。
- [ ] **每次收到新广播都无条件更新内存中该 PC 的 token**（覆盖式，不是合并）。
- [ ] **持久化策略**：
  - App 进程被回收后再次启动，**先用持久化 token 试探** `GET /api/v1/health`（PC 端该接口无需鉴权，本步只是探活）；
  - 收到任意一次新广播后，**立即覆盖**持久化值。
- [ ] **401 重试**：使用 `token` 调受保护接口时若返回 401，App 应：
  1. 等下一次广播（最迟 2 s）拿到新 token；
  2. 用新 token **重试一次**当前请求；
  3. 仍 401 → 提示用户「PC 已拒绝当前 Token，请确认 PC 端 token 与 App 一致」，引导重新发现。
- [ ] **不做"token 主动拉取"**：PC 端无 token 查询接口，App 也**不应**自己造轮询。
- [ ] **不要混淆两个 token 源**：广播 token 与 health token 同源；若出现不一致（理论上不可能），以**广播 token 为准**。

### 2.4 HTTP 业务调用

- [ ] 项目列表：`GET /api/v1/projects?token={token}`，请求级超时 **5s**（见 §2.2 时间窗表）
- [ ] 上传：`POST /api/v1/projects/{name}/upload?token={token}`，`multipart/form-data`，单文件最大 500MB；请求级超时 **60s**（见 §2.2 时间窗表，60s 不够再延）
  - [ ] 单文件 ≤ **500MB**（服务端硬限制，超限会被拒）
  - [ ] 扩展名必须是 `.jpg .jpeg .png .raw .avi .mp4 .mov .mkv .webm .m4v .mpg .mpeg`（其他扩展名会被写入 `failed[]`）
- [ ] Token 也可通过 HTTP Header `X-Token: ...` 携带（PC 端先读 queryString 再读 header）
- [ ] 处理错误码：200/400/401/404/405/500（见协议文档 §3.2；401 见 §2.3 处理）

### 2.5 在线判定

- [ ] **6 秒（3 个广播周期）内未收到任意一次广播 → 标记 PC 离线**（见 §2.2 时间窗表）
- [ ] 切后台后系统会暂停 UDP 监听，回前台立即重置计时；勿让用户看到错误状态

## 三、安全 / 用户提示

- [ ] **明确告知用户**：本工具定位为「单人本地工具」，Token 在 LAN 上是明文广播
- [ ] **不要把 token 写到日志 / crash 上报**
- [ ] LAN 内 500MB 大文件传输：客户端需提前展示耗时/进度，PC 端限制是硬的
- [ ] Token 的获取 / 更新 / 持久化 / 401 重试策略**一律遵循 §2.3**，本节不重复

## 四、iOS / macOS 关键注意点（KB `API-03 §四.2 / §九.3`）

> 本节 v0.12 PC 端协议只走 UDP 广播 + HTTP，**iOS 与 macOS 客户端实现注意点一致**：都受 Local Network 权限与 ATS 限制。下文行内简称"iOS"，macOS 客户端按相同要点处理（仅替代步骤略有差异）。

### 4.1 iOS 关键注意点

- [ ] `Info.plist` 添加 `NSLocalNetworkUsageDescription`（必填，否则系统直接拒绝本地网络访问）
- [ ] 首次扫描会被弹窗拦；用户拒绝后引导到「设置 → 隐私 → 本地网络」开启
- [ ] **真机调试**（模拟器下 UDP 广播不通）
- [ ] iOS 17+ 「私有 Wi-Fi 地址」可能让广播源 IP 不稳定，**用稳定 PC 标识以 `name+port` 为主，IP 仅做直连用**
- [ ] 后台运行会暂停 UDP 监听；回到前台需立即重置离线判定计时

### 4.2 macOS 关键注意点（C# / Avalonia / Swift / Flutter 等桌面 App 都适用）

- [ ] **macOS 13+（Ventura 起）对未沙盒编译型二进制同样有 Local Network 权限弹窗**：UDP 监听需在 `.app/Contents/Info.plist` 加 `NSLocalNetworkUsageDescription` 字符串（不是 `NSPrivacyAccessedAPITypeLocalNetwork`，是 `NSLocalNetworkUsageDescription`）。若不填，UDP 监听第一次启动会被系统静默拦截且无报错；后端走系统日志查看 `[com.apple.network]`
- [ ] 首次触发本地网络访问时系统弹"允许/不允许"——若用户拒绝，需引导到「系统设置 → 隐私与安全性 → 本地网络」勾选本 App
- [ ] 同样受 ATS 限制：明文 HTTP 同样需 `Info.plist` 加 ATS 例外（与 §4.3 一致）
- [ ] macOS 上**无 iOS 那种"模拟器不可用"问题**，可以直接 Mac 上调试
- [ ] 多网卡：Mac 同时连有线 + Wi-Fi 时 UDP 广播可能走错接口，App 监听需遍历活跃网络接口

### 4.3 通用 ATS 例外（iOS + macOS 都需）

- [ ] `URLSession`/`NWConnection` 都可，**ATS 默认要求 HTTPS**：明文 HTTP 需配 `Info.plist` ATS 例外（KB 没提到这点，v0.12 PC 是明文，需 App 端补）

```xml
<key>NSAppTransportSecurity</key>
<dict>
  <key>NSAllowsArbitraryLoads</key>
  <true/>
</dict>
```

## 五、Android 关键注意点（KB `API-03 §四.2 / §九.3` + 实现现实）

- [ ] `DatagramSocket` 默认即可接收广播，无需 `MulticastLock`（这是组播的需求，UDP 广播不需要）
- [ ] 权限最小集：

  ```xml
  <uses-permission android:name="android.permission.INTERNET" />
  <uses-permission android:name="android.permission.ACCESS_NETWORK_STATE" />
  <uses-permission android:name="android.permission.ACCESS_WIFI_STATE" />
  ```

  > KB 提到 `POST_NOTIFICATIONS`，但 v0.12 PC 端推送未落地，**当前对接用不上**

- [ ] Android 9+ 默认禁用明文 HTTP，**必须配 `network-security-config`**：

  ```xml
  <!-- res/xml/network_security_config.xml -->
  <network-security-config>
    <base-config cleartextTrafficPermitted="true">
      <trust-anchors>
        <certificates src="system" />
        <certificates src="user" />
      </trust-anchors>
    </base-config>
  </network-security-config>
  ```

  ```xml
  <!-- AndroidManifest.xml <application> -->
  android:networkSecurityConfig="@xml/network_security_config"
  ```

- [ ] Doze / 后台：`DatagramSocket.receive()` 在主进程被 Doze 挂起；如需后台监听走 **ForegroundService**
- [ ] 多网卡：UDP 广播可能从非业务网卡进，需在所有活跃网卡监听
- [ ] 厂商后台限制（华为/小米/OPPO）：必须提示用户将 App 加入"自启动"和"电池优化白名单"，否则后台秒收不到
- [ ] AP 隔离路由器（部分酒店 Wi-Fi）：UDP 广播被屏蔽，必须走手动输入 IP 兜底

## 六、UX 与稳定性

- [ ] 首屏：「正在搜索 PC…」动画 + 6 秒内无设备提示"检查 Wi-Fi / 切换到同一局域网"
- [ ] 显示 PC `name`（MachineName）时，标明"多台同名 PC 时用 IP 区分"
- [ ] **Token 输入**：手动模式（兜底）需要用户输入；安全输入框（iOS `isSecureTextEntry` / Android `password` InputType）
- [ ] 当前项目：列表展示 `currentProject`，未配置项目时显示"PC 未选择项目"，不要展示空项目名
- [ ] 上传结果：成功 / 失败 / 单文件超大 / 扩展名不允许——分别清晰提示
- [ ] 失败兜底：用户感知级 **5s 看不到任何 PC**（区别于 §2.5 的 6s 离线判定——5s 是 UX 节奏，不是网络技术超时）→ 自动引导「手动输入 IP / 端口 / Token」

## 七、自测清单

- [ ] PC 启动 HTTP 服务 → App 1~2s 内出现设备
- [ ] PC 关闭 HTTP 服务 → App 在 6s 内标灰离线
- [ ] PC 手动 `RegenerateToken()` → App 最迟 2s 内被动收到含新 token 的广播，自动覆盖内存值（不应需用户重输）
- [ ] PC 重启（Token 变更）→ App 重新收到广播后自动用新 Token 继续可用
- [ ] **PC 重启同时换了 IP** → App 用 `name+port` 仍能识别同一 PC（IP 视为浮动）
- [ ] **缓存 token 与最新广播 token 不一致** → App 立刻覆盖缓存与内存值；期间若触发 401，按 §2.3 重试一次仍 401 才走兜底提示
- [ ] **App 进程被回收后重启** → 用持久化 token 试探 `/api/v1/health`，无需等下次广播即可判断 PC 可达
- [ ] 多 PC 同 LAN：列表分别展示同名 PC 时用 IP 区分
- [ ] 切 Wi-Fi → 飞行 → 恢复 Wi-Fi，App 能自动重新发现
- [ ] 上传 1 张 .jpg → PC 端项目目录下 `{yyyy-MM-dd}/` 出现该文件
- [ ] 上传 1 张 .exe → App 收到 `failed[]` 含 `unsupported extension .exe`
- [ ] 上传 600MB 单文件 → 服务端写入失败，App 收到 `failed[]` 含 `exceeds 500MB limit`
- [ ] 上传 2 张同文件名 → PC 端一个落地原名，一个加 `_1` 后缀
- [ ] iOS / Android 真机分别能 `tcpdump`（Android 可 PCAP）抓到 9876 广播 + HTTP 流量
- [ ] iOS `Info.plist` 故意漏配 `NSLocalNetworkUsageDescription` → 必须看到清晰的权限弹窗/提示

## 八、交付物清单

- [ ] UDP 监听模块（含 `service` 过滤、6s 离线判定）
- [ ] HTTP 客户端（带 Token 持久化、错误码映射、500MB 限制校验）
- [ ] Token 缓存模块（Keychain / EncryptedSharedPreferences）
- [ ] 设备列表 UI（含"未配对/已配对"状态、当前项目展示）
- [ ] 手动输入 IP/Port/Token 页面（兜底）
- [ ] 上传结果页（成功/失败分桶展示）
- [ ] 单元测试：`service` 字段过滤、`currentProject` 空串处理、Token 状态机（覆盖 / 持久化 / 401 重试 / 同源校验，见 §2.3）
- [ ] 真机调试脚本：`tcpdump` 命令、模拟广播命令（见协议文档 §七）

## 九、未来改进（实现真加密之前先固化行为）

PC 端协议需要做的演进（不在 v0.12 范围内）：

- Token 用 PC 长期密钥加密传输（PC 公钥 + ECDH）
- Broadcast 加 nonce / 时间戳防重放
- HTTP 改 HTTPS 或应用层 AEAD
- 加 mDNS 双发现通道
- 跨子网走公网 relay（参见 [cross-device-sync.md](file:///Users/hex/code/StartTooler/doc/knowledge-base/06-cross-device-sync.md)）
