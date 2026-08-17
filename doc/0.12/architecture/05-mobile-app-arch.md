# 05 · App 端架构

本文档描绘 StarTooler 移动端 App 的整体架构、模块边界、数据流、状态机。

## 一、目标与约束

### 1.1 目标

- 让用户把手机里的照片一键传到 PC 端
- 零配置 / 零等待 / 零失败 / 零权限 / 零后台
- 跨平台一致（iOS / Android）

### 1.2 约束

- 不能依赖 PC 端 → 不能用单机数据
- 不能"通用" → 只解决"传照片到 PC"这一个场景
- 不能常驻 → App 关闭不占资源
- 不能引入账号 / 云同步 / 跨设备

## 二、整体架构

```
┌─────────────────────────────────────────────────────────┐
│                    StarTooler App                        │
│                                                          │
│  ┌────────────┐  ┌────────────┐  ┌────────────┐         │
│  │ Discovery  │  │ Connection │  │  Upload    │         │
│  │  Module    │  │  Module    │  │  Module    │         │
│  └────────────┘  └────────────┘  └────────────┘         │
│       │               │              │                  │
│  ┌────▼───────────────▼──────────────▼─────┐            │
│  │            Core (Network/Persistence)   │            │
│  └────┬───────────────────┬─────────────────┘            │
│       │                   │                              │
│  ┌────▼────┐         ┌────▼────┐                        │
│  │ UDP     │         │ HTTP    │                        │
│  │ Listener│         │ Client  │                        │
│  └─────────┘         └─────────┘                        │
└─────────────────────────────────────────────────────────┘
       │                   │
       │ UDP               │ HTTP
       ▼                   ▼
┌─────────────────────────────────────────────────────────┐
│                   PC 端 (StarTooler)                     │
│  UploadServerService (HTTP + UDP 广播)                   │
└─────────────────────────────────────────────────────────┘
```

## 三、模块边界

### 3.1 App 端五大模块

| 模块 | 职责 | 不该做什么 |
|---|---|---|
| Discovery | UDP 监听 + 解析 PC 广播 | 不连 HTTP |
| Connection | Token 验证 + 持久化 | 不知道 photo |
| Project | 拉取项目列表 | 不上传 |
| Upload | 上传照片 | 不知道 PC 发现 |
| Persistence | 存 IP / Token / 列表 | 不知道业务 |

### 3.2 Core 横切

| 模块 | 职责 |
|---|---|
| Network | HTTP 客户端 + 错误映射 |
| Storage | Keychain / EncryptedSP 抽象 |
| Models | DTO / Domain Model |

## 四、数据流

### 4.1 首次连接流程

```
App 启动
  ↓
Persistence.load() → 读 IP/Token
  ├─ 失败 → 跳 Connection 模块
  │         ↓
  │       Discovery.start() 扫 5 秒
  │         ↓
  │       Connection.connect(ip, token)
  │         ↓
  │       Persistence.save()
  │         ↓
  │       跳 Project 模块
  │
  └─ 成功 → Connection.tryReconnect()
            ↓
            ├─ 成功 → 跳 Project
            └─ 401 → 清 Token → 跳 Token 验证页
            └─ 网络错 → 跳 Connection 模块
```

### 4.2 上传流程

```
Project 模块
  ↓
User 选项目
  ↓
Upload 模块
  ↓
User 选照片（PHPicker / PhotoPicker）
  ↓
Upload.upload(projectName, files, onProgress)
  ↓
multipart 构造 → POST /api/v1/projects/{name}/upload
  ↓
显示进度
  ↓
成功 → 跳完成页
失败 → 失败详情
```

## 五、状态机

### 5.1 App 总状态

```
      ┌───────────┐
      │ 启动     │
      └─────┬─────┘
            │
            ▼
   ┌─────────────────┐
   │ LoadPreferences │ ← Persistence.read()
   └────────┬────────┘
            │
   ┌────────┴────────┐
   │                 │
   ▼                 ▼
┌──────┐        ┌──────────┐
│首次  │        │ 已连接    │
│启动  │        │          │
└──┬───┘        └────┬─────┘
   │                 │
   │ Discovery       │ 收到 401
   ▼                 ▼
┌──────────┐    ┌──────────┐
│ 扫描中    │    │ Token 错  │
└──┬────┬──┘    └─────┬────┘
   │    │            │
   │    │ found    用户重新输入
   │    ▼            │
   │ ┌──────┐        │
   │ │已连接│────────┘
   │ └──────┘
   │ no PC
   ▼
┌──────────┐
│ 失败     │ → 手动输入 IP
└──────────┘
```

### 5.2 Upload 子状态

```
       ┌───────────┐
       │ 准备      │
       └─────┬─────┘
             │ 用户点上传
             ▼
       ┌───────────┐
       │ 上传中    │
       └─────┬─────┘
             │
   ┌─────────┼─────────┐
   │         │         │
   ▼         ▼         ▼
┌──────┐ ┌──────┐ ┌──────────┐
│成功  │ │失败  │ │取消      │
└──────┘ └──────┘ └──────────┘
```

## 六、UI 架构

### 6.1 iOS

```
MainView (SwiftUI)
├── DiscoveryView (发现)
├── TokenInputView (Token 输入)
├── ConnectionLoadingView (加载)
├── ProjectListView (项目列表)
├── UploadView (上传)
│   ├── PhotoPicker (PHPicker)
│   ├── UploadProgressView (进度)
│   └── UploadResultView (结果)
└── SettingsView (设置)
```

### 6.2 Android

```
MainActivity (Compose)
├── NavGraph
│   ├── DiscoveryScreen
│   ├── TokenInputScreen
│   ├── ConnectionLoadingScreen
│   ├── ProjectListScreen
│   ├── UploadScreen
│   │   ├── PhotoPicker (PhotoPicker)
│   │   ├── UploadProgressBar
│   │   └── UploadResultDialog
│   └── SettingsScreen
```

## 七、关键数据模型

### 7.1 PC

```swift
struct PC {
    let ip: String
    let port: Int
    let name: String
    let token: String
    let currentProject: String?
    let lastSeenAt: Date
}
```

### 7.2 Project

```swift
struct Project {
    let name: String          // basename
    let path: String
    let projectName: String?  // 用户起的名字
    let fileCount: Int64
    let sizeMb: Int64
    let isCurrent: Bool
}
```

### 7.3 UploadResult

```swift
struct UploadResult {
    let success: Bool
    let files: [File]
    let failed: [FailedFile]
}
```

## 八、线程模型

### 8.1 iOS

| 层级 | 线程 |
|---|---|
| UI | MainActor |
| ViewModel | MainActor |
| Services | 自有 actor / class |
| Network | URLSession 自有线程 |
| Disk I/O | 自有 actor |

### 8.2 Android

| 层级 | 线程 |
|---|---|
| UI | Main |
| ViewModel | viewModelScope |
| Services | Dispatchers.IO |
| Network | OkHttp Dispatcher |
| Disk I/O | Dispatchers.IO |

## 九、错误处理架构

```
Error 发生
  ↓
业务层捕获并转换为 AppError
  ↓
AppError → ErrorMapper → 用户 i18n 文案
  ↓
UI 层显示（Toast / Alert / 全屏）
```

### 9.1 错误分类

| 类别 | 例子 | 展示 |
|---|---|---|
| NetworkError | TCP 失败 / DNS 失败 | Retry 按钮 |
| HTTPCodeError | 401 / 404 / 500 | Toast + 跳页 |
| BusinessError | failed[] | 跳过 / 详情 |
| ClientError | 0 张照片 / 超 500MB | 禁用按钮 |

## 十、安全架构

### 10.1 信任边界

```
App 端 = 不信任自己（Token 可能在 log 泄漏）
PC 端 = 单 PC 信任边界
LAN = 嗅探可能
```

### 10.2 防护

| 风险 | 防护 |
|---|---|
| LAN 嗅探 Token | 6 位数字是临时，重置失效 |
| App 端 Token 泄漏 | 不进日志 / Keychain 加密 |
| MITM | 无 TLS（家用场景） |
| Token 重复用 | 主动重置 |
| 异常 IP 连接 | 401 直跳验证页 |

### 10.3 安全原则

- **最小权限原则**：相册走系统选择器，不申请相册权限
- **最小存储**：只存必要数据（IP / Token / 上次 PC 列表）
- **最小曝光**：Token 不进日志、不进屏幕
- **最小生命周期**：30 天未连接自动清

## 十一、性能架构

### 11.1 预期性能

| 指标 | 目标 |
|---|---|
| App 启动到扫描 | < 2 秒 |
| 扫描到连接 | < 5 秒 |
| 选 50 张到上传完 | < 30 秒（同 WiFi） |
| 失败后重试 | < 1 秒 |
| 内存峰值 | < 200MB |

### 11.2 性能策略

- 视频不预加载
- 缩略图走系统 API（PHCachingImageManager / Coil）
- 列表用 LazyColumn / LazyVStack
- 上传用流式 multipart（不预读全部到内存）

## 十二、依赖与扩展

### 12.1 依赖

| 依赖 | 必要性 |
|---|---|
| PC 端 v0.12+ | 必须 |
| 用户手动配置 | 0 |

### 12.2 扩展点

| 扩展 | 复杂度 |
|---|---|
| 缩略图预览 | 中 |
| 离线缓存 | 中 |
| 多 PC 持久化 | 中 |
| 端到端加密 | 高 |
| 公网桥接 | 高 |
| 拍照直传 | 中 |

## 十三、跨平台一致性

| 维度 | iOS | Android |
|---|---|---|
| 状态机 | 同 | 同 |
| 错误映射 | 同 | 同 |
| HTTP 客户端 | URLSession | OkHttp |
| 持久化 | Keychain | EncryptedSP |
| UI | SwiftUI | Compose |
| UDP | NWConnection | DatagramSocket |
| 选照片 | PHPicker | PhotoPicker |

业务逻辑一致，技术栈不同。

## 十四、参考实现

```
iOS 参考：
- AirDrop 协议（简化版）
- PhotosPicker 官方示例
- Keychain Services 文档

Android 参考：
- PhotoPicker 官方示例
- EncryptedSharedPreferences 文档
- OkHttp 文档
```

## 十五、画图

### 15.1 完整上下文

```
┌─────────────────────────────────────────────────┐
│                StarTooler App                   │
│  ┌─────────────────────────────────────────┐   │
│  │  UI Layer (SwiftUI / Compose)            │   │
│  └──┬──────────────────────────────────┬───┘   │
│     │                                  │       │
│  ┌──▼─────────┐              ┌────────▼───┐   │
│  │  VMs       │              │ Core       │   │
│  │  (MainActor)│              │ (Network/  │   │
│  └──┬─────────┘              │  Storage)  │   │
│     │                        └────────┬───┘   │
│  ┌──▼──────────────┐                  │       │
│  │ Services        │──────────────────┘       │
│  │ (Discovery/     │                          │
│  │  Connection/    │                          │
│  │  Project/       │                          │
│  │  Upload/        │                          │
│  │  Persistence)   │                          │
│  └─────────────────┘                          │
└────────────────┬────────────────────────────────┘
                 │ UDP / HTTP
                 ▼
┌─────────────────────────────────────────────────┐
│           PC 端 (StarTooler)                    │
│  UploadServerService (HTTP + UDP)               │
└─────────────────────────────────────────────────┘
```

### 15.2 部署视图

```
iOS App         Android App
  │                │
  │ HTTPS?         │ HTTPS?
  │ 不，本地 HTTP   │ 不，本地 HTTP
  │                │
  ▼                ▼
┌─────────────────────┐
│   PC 端            │
│  UploadServer       │
│  端口 8765 (HTTP)   │
│  端口 9876 (UDP)    │
└─────────────────────┘
```

无云端，无转发，无中心服务。

## 十六、与 PC 端架构对比

| 维度 | PC 端 | App 端 |
|---|---|---|
| 技术栈 | .NET 9 + Avalonia | Swift / Kotlin |
| 用户数量 | 单用户 | 单用户 |
| 网络模型 | Server | Client |
| 持久化 | SQLite | Keychain / EncryptedSP |
| 共享协议 | HTTP + UDP | HTTP + UDP |
| 错误处理 | Debug.WriteLine | i18n + Toast |
| 升级 | 用户手动更新 | App Store / Play |

## 十七、关键决策汇总

| 决策 | 选项 |
|---|---|
| 仓库 | 独立仓库 |
| 语言 | Swift / Kotlin |
| UI 框架 | SwiftUI / Compose |
| HTTP 客户端 | URLSession / OkHttp |
| 持久化 | Keychain / EncryptedSP |
| 跨平台代码 | 无 |

## 十八、Open Questions

| # | 问题 | 决策 |
|---|---|---|
| 1 | iOS / Android 最低版本 | iOS 16+ / Android 10+ |
| 2 | App 端是否允许新建项目 | ❌ 不允许 |
| 3 | 手动输入 IP 位置 | 连接页底部 |
| 4 | 多 PC 选择持久化 | 暂只存最近一台 |
| 5 | 上传历史保留 | 30 天 |
| 6 | 离线缓存 | 暂不实现 |
| 7 | 端到端加密 | 暂不实现 |
| 8 | 上传失败重试 | 手动 |
| 9 | 缩略图 | 暂不实现 |
| 10 | 拍照直传 | 暂不实现 |

详见 [demand/05-mobile-app.md §11](../demand/05-mobile-app.md#)。
