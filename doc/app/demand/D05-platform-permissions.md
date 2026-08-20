# D05 · 移动端平台权限与适配

> **状态**：需求稿（与 PC v0.12 实现对齐）
> **关联**：[`../03-mobile-checklist.md` §四、五](../03-mobile-checklist.md#四ios--macos-关键注意点kb-api-03-四2--九3)、[`../../knowledge-base/API-03-udp-broadcast.md` §四.2 / §九.3](../../knowledge-base/API-03-udp-broadcast.md)

---

## 0. 元信息

| 项 | 值 |
|---|---|
| 文档版本 | **v0.1（需求稿）** |
| 目标用户 | iOS / Android / macOS App 端开发者 |
| 文档状态 | **需求 — 待评审** |
| PC 端能力状态 | ✅ UDP 广播 + 明文 HTTP 已实现 |
| App 端目标 | 解决 iOS / macOS Local Network 权限、Android Cleartext、明文 HTTP 等平台差异 |

---

## 1. 需求总览

PC v0.12 协议采用：

- **明文 UDP 广播**（255.255.255.255:9876）
- **明文 HTTP**（端口可配置，默认 8765）

→ 各移动平台默认都会拦截，App 端**必须**正确配置权限与例外，否则功能完全不可用。

### 1.1 三类平台问题

| 类别 | iOS / macOS | Android |
|---|---|---|
| 本地网络发现 | Local Network 权限（Info.plist 字符串） | 默认放行 |
| 明文 HTTP | ATS 例外 | `network_security_config` |
| UDP 广播 | 多网卡需遍历 | 多网卡需遍历 |
| 后台监听 | 系统限制 | Doze / 厂商限制 |

### 1.2 一句话概括

**移动端 App 必须按平台补齐：iOS/macOS 的 `NSLocalNetworkUsageDescription` + ATS 例外；Android 的 `network_security_config` + INTERNET 权限；并在多网卡场景遍历活跃接口。**

---

## 2. 用户场景

### 场景一：iOS 首次启动

> 1. 用户首次打开 App
> 2. 系统弹"是否允许 App 访问本地网络？" → 用户允许
> 3. UDP 监听开始工作
> 4. 用户拒绝 → 引导到「设置 → 隐私 → 本地网络」开启

### 场景二：macOS 13+ 首次启动

> 1. 用户首次启动未沙盒编译的二进制
> 2. 系统静默拦截 UDP（**无错误提示**）
> 3. App 收不到任何广播
> 4. 需在 `Info.plist` 加 `NSLocalNetworkUsageDescription`，重新编译

### 场景三：Android 9+ 默认禁 HTTP

> 1. App 直接调 `http://192.168.1.10:8765/...`
> 2. 抛 `CleartextNotPermittedException`
> 3. 必须配置 `network_security_config.xml` 放行私有 IP 段

### 场景四：Mac 同时连有线 + Wi-Fi

> 1. Mac 上 UDP 广播可能从错误的网卡出去
> 2. App 必须遍历所有活跃接口
> 3. 否则可能"PC 端能收到但 App 收不到"

---

## 3. 功能需求

### 3.1 iOS 关键权限

#### 3.1.1 Info.plist 必须包含

```xml
<key>NSLocalNetworkUsageDescription</key>
<string>用于在家庭网络中自动发现 PC 端的星助</string>
```

> ⚠️ **必填**——不填会被系统直接拒绝本地网络访问，无任何提示。

#### 3.1.2 ATS（App Transport Security）

PC 端是明文 HTTP，必须加 ATS 例外：

```xml
<key>NSAppTransportSecurity</key>
<dict>
  <key>NSAllowsArbitraryLoads</key>
  <true/>
</dict>
```

或更精细（推荐生产用）：

```xml
<key>NSAppTransportSecurity</key>
<dict>
  <key>NSExceptionDomains</key>
  <dict>
    <key>192.168.0.0</key>
    <key>NSExceptionAllowsInsecureHTTPLoads</key>
    <true/>
    <key>NSIncludesSubdomains</key>
    <true/>
  </dict>
</dict>
```

#### 3.1.3 真机调试

- iOS 模拟器下 UDP 广播**不通**——必须真机调试
- iOS 17+ 「私有 Wi-Fi 地址」可能让广播源 IP 不稳定——用稳定 PC 标识以 `name+port` 为主，IP 仅做直连用

#### 3.1.4 后台约束

- iOS 后台会暂停 UDP 监听
- 回到前台需立即重置离线判定计时
- 不能依赖后台监听做"实时发现"

### 3.2 macOS 关键权限

> **macOS 13+（Ventura 起）对未沙盒编译型二进制同样有 Local Network 权限弹窗**——UDP 监听需在 `.app/Contents/Info.plist` 加 `NSLocalNetworkUsageDescription` 字符串（**不是 `NSPrivacyAccessedAPITypeLocalNetwork`**）。

#### 3.2.1 Info.plist 必须包含

```xml
<key>NSLocalNetworkUsageDescription</key>
<string>用于在家庭网络中自动发现 PC 端的星助</string>
```

#### 3.2.2 首次触发行为

- 首次触发本地网络访问时系统弹"允许/不允许"
- 用户拒绝 → 引导到「系统设置 → 隐私与安全性 → 本地网络」勾选本 App
- **不填 Info.plist 字符串**：UDP 监听第一次启动会被系统**静默拦截**且无报错；需查后端 `[com.apple.network]` 日志

#### 3.2.3 调试便利

- macOS 上**无 iOS 那种"模拟器不可用"问题**，可直接 Mac 上调试
- 多网卡：Mac 同时连有线 + Wi-Fi 时 UDP 广播可能走错接口，App 监听需遍历活跃网络接口

### 3.3 Android 关键权限

#### 3.3.1 AndroidManifest.xml 必须包含

```xml
<uses-permission android:name="android.permission.INTERNET" />
<uses-permission android:name="android.permission.ACCESS_NETWORK_STATE" />
<uses-permission android:name="android.permission.ACCESS_WIFI_STATE" />
```

> KB 提到 `POST_NOTIFICATIONS`，但 v0.12 PC 端推送未落地，**当前对接用不上**。

#### 3.3.2 明文 HTTP 必须配 network_security_config

`AndroidManifest.xml <application>` 标签：

```xml
<application
    android:networkSecurityConfig="@xml/network_security_config">
```

`res/xml/network_security_config.xml`：

```xml
<network-security-config>
    <base-config cleartextTrafficPermitted="true">
        <trust-anchors>
            <certificates src="system" />
            <certificates src="user" />
        </trust-anchors>
    </base-config>
</network-security-config>
```

#### 3.3.3 DatagramSocket 默认即可

- `DatagramSocket` 默认即可接收广播，**不需要** `MulticastLock`（这是组播的需求，UDP 广播不需要）
- 这是常见的误解，**不要**加

#### 3.3.4 后台 / Doze

- `DatagramSocket.receive()` 在主进程被 Doze 挂起
- 如需后台监听走 **ForegroundService**（v0.12 范围内不实现，留作前瞻）
- 厂商后台限制（华为/小米/OPPO）：必须提示用户将 App 加入"自启动"和"电池优化白名单"

#### 3.3.5 多网卡

- UDP 广播可能从非业务网卡进
- 需在所有活跃网卡监听

### 3.4 跨平台：照片选择

| 平台 | API |
|---|---|
| iOS | `PHPickerViewController`（无需相册权限，只返回选中的） |
| Android | `ActivityResultContracts.PickMultipleVisualMedia`（Photo Picker，API 33+） |
| macOS | `NSOpenPanel` 或 `PHPickerViewController` |

> 推荐 PHPicker / Photo Picker：用户授权粒度到单张照片（iOS 14+ / Android 13+），不需要"完全相册访问"权限。

### 3.5 跨平台：网络事件

| 平台 | API |
|---|---|
| iOS | `NWPathMonitor` |
| Android | `ConnectivityManager.NetworkCallback` |
| macOS | `NWPathMonitor` |

监听网络变化 → 触发重新扫描 / 重置离线判定。

---

## 4. 非功能需求

| 维度 | 要求 |
|---|---|
| Info.plist 字符串 | 必须有英文（App Store 审核要求）+ 本地化 |
| 权限拒绝引导 | 提供跳转路径；不要靠用户主动发现 |
| 多网卡遍历 | 不依赖主网卡；遍历所有活跃 IPv4 接口 |
| 后台限制 | 在 README 显式说明"App 不在后台持续监听" |

---

## 5. 边界情况

| 场景 | 处理 |
|---|---|
| iOS 用户拒绝本地网络权限 | 引导到设置；启用手动输入兜底 |
| macOS 13+ 静默拦截 | 后端日志确认；用户必须手动到系统设置开启 |
| Android 9+ 默认禁 HTTP | 必须配 `network_security_config`，否则 App 完全不可用 |
| 厂商后台限制（华为/小米/OPPO）| 弹窗引导用户加白名单 |
| 飞行模式 / 全部断网 | 网络事件触发；标记所有 PC 离线 |
| Mac 同时连有线 + Wi-Fi | 遍历所有活跃接口 |

---

## 6. 不做清单

| 内容 | 理由 |
|---|---|
| 后台持续监听 | 系统限制；v0.12 范围内不实现 |
| Bonjour / mDNS | PC 端未支持 |
| HTTPS / 自签证书 | 局域网风险可控 |
| 完全相册访问权限 | PHPicker / Photo Picker 粒度足够 |
| `POST_NOTIFICATIONS` 权限 | PC 端推送未落地 |

---

## 7. 验收标准

### 7.1 iOS

- [ ] Info.plist 含 `NSLocalNetworkUsageDescription` 字符串（英文 + 中文）
- [ ] Info.plist 含 ATS 例外
- [ ] 真机首次启动触发本地网络权限弹窗
- [ ] 用户拒绝后引导到「设置 → 隐私 → 本地网络」
- [ ] 真机调试可抓 UDP 9876 + HTTP 流量
- [ ] 模拟器不要求 UDP 工作（已知限制）

### 7.2 macOS

- [ ] `.app/Contents/Info.plist` 含 `NSLocalNetworkUsageDescription`
- [ ] 首次启动触发系统弹窗（非静默）
- [ ] 用户拒绝后引导到「系统设置 → 隐私与安全性 → 本地网络」
- [ ] 多网卡场景遍历活跃接口
- [ ] 直接 Mac 调试可用（不需真机）

### 7.3 Android

- [ ] AndroidManifest.xml 含 INTERNET / ACCESS_NETWORK_STATE / ACCESS_WIFI_STATE
- [ ] 不含 MulticastLock 相关代码
- [ ] `network_security_config.xml` 放行私有 IP 段
- [ ] Photo Picker 优先（API 33+）
- [ ] 真机可 PCAP 抓包

### 7.4 跨平台

- [ ] 网络事件监听（NWPathMonitor / NetworkCallback）
- [ ] 跨 Wi-Fi / 飞行模式能自动恢复发现
- [ ] README 显式说明"App 不在后台持续监听"

---

## 8. 关联文档

- 详细自测：[`../03-mobile-checklist.md` §四、五](../03-mobile-checklist.md#四ios--macos-关键注意点kb-api-03-四2--九3)
- KB 权威：[`../../knowledge-base/API-03-udp-broadcast.md` §四.2 / §九.3](../../knowledge-base/API-03-udp-broadcast.md)
- UDP 发现：[D01](D01-udp-discovery.md)
- HTTP 上传：[D02](D02-http-upload.md)
- 离线重连：[D04](D04-offline-reconnect.md)