# 0001 · 移动端 App 仓库选型

**状态**：已采纳
**日期**：2026-08-15
**决策者**：StartTooler 主项目组
**关联**：[doc/0.12/demand/04-mobile-lan-sync.md](../demand/04-mobile-lan-sync.md)、[doc/0.12/spec/05-mobile-app.md](../spec/05-mobile-app.md)

## 背景

[D04 移动端 LAN 同步](../demand/04-mobile-lan-sync.md) 需求决定 PC 端作为 HTTP 服务，移动端 App 通过 LAN 上传文件。App 端需要分发型：
- iOS（Swift / SwiftUI）
- Android（Kotlin / Jetpack Compose）

PC 端是 .NET 9 单文件 Avalonia 应用。App 端是原生双端。两者**完全不同的技术栈、构建工具链、发布渠道**。

## 决策

**采用独立仓库方案**。

具体：
- 仓库名：`fishhex/starttooler-app`（暂定）
- 仓库归属：fishhex 组织下，与 `fishhex/StartTooler`（PC 端）并列
- 协议：HTTP/UDP 协议字段同步通过文档版本号（`version` 字段）

## 备选方案

### 方案 A：独立仓库（已采纳）

**优点**：
- 独立 CI / CD（iOS macOS runner / Android Linux runner）
- 独立团队（移动端开发者不需 clone PC 端 .NET 仓库）
- 独立版本号（App 端可 1.0.0 起步，PC 端 v0.12 继续）
- 独立发布渠道（App Store / Google Play 与 sideload 互不干扰）
- 仓库体量小（PC 端 repo 30+ MB，移动端 5 MB 内）

**缺点**：
- 跨仓库同步协议变更需手动（通过 `version` 字段协调）
- 三方协作（App + PC + Go relay）issue 跨仓库管理

**缓解**：
- 协议字段变更走 ADR（架构决策记录）+ 跨仓库 issue
- App 端有 `version` 兼容性检查，PC 端连续版本可被识别

### 方案 B：git submodule

**优点**：主仓库可见，协议代码一处看

**缺点**：
- submodule 体验差（必须 init / update）
- CI 复杂（clone 时 submodule 递归）
- 主仓库 PR 要改 submodule 指向，难合并
- 移动端开发者主仓库完全不需要 PC 端代码

### 方案 C：monorepo

**优点**：单仓管理，原子化 PR

**缺点**：
- **致命**：PC 端是 `WinExe`（`net9.0` + `Avalonia`），无法装 iOS/Android SDK 工具链
- macOS 用户必须装 .NET 9 SDK，但 iOS 编译必须有 Xcode
- 文件结构混乱（`mobile/ios/`、`mobile/android/`、`pc/` 三套互相隔离）
- 移动端开发者被迫 clone .NET 全部代码

## 跨仓库协议同步

### 现状

PC 端 HTTP API 在 [doc/knowledge-base/API-01-http-routes.md](../../knowledge-base/API-01-http-routes.md) 定义，UDP 广播在 [API-03-udp-broadcast.md](../../knowledge-base/API-03-udp-broadcast.md)。

这些文档**两边仓库共享**——用 git subtree 或单独 tools 同步。

**采用**：直接复制（避免 subtree 复杂性）。PC 端 KB 文档是**事实源**（canonical source），App 端仓库里有一份镜像。

### 变更流程

```
PC 端代码改动
  ↓
更新 pc-side KB（API-01 / API-03）
  ↓
手动同步 KB 到 App 端仓库
  ↓
App 端 PR 适配
```

**约定**：
- PC 端做破坏性变更前必须先在 `decisions/00XX-*.md` 写 ADR
- 协议变更必须 major version bump（`v0.12` → `v0.13`），App 端能识别
- App 端见到不识别的 `version` 字段 → 提示用户升级 PC 端

### 版本兼容矩阵

| PC 端版本 | App 端最低版本 | 兼容策略 |
|---|---|---|
| 0.12 | 1.0.0 | 当前基线 |
| 0.13 | 1.0.0 | 新字段可选（App 端忽略） |
| 0.14 | 1.1.0 | 弃用端点（App 端有兼容期） |
| 1.0.0 | 2.0.0 | 协议稳定，App 端必须升级 |

## 仓库结构（App 端）

```
fishhex/starttooler-app/
├── README.md
├── LICENSE
├── .github/
│   └── workflows/
│       ├── ios-build.yml
│       └── android-build.yml
├── ios/
│   ├── StartTooler.xcworkspace
│   ├── StartTooler/
│   │   ├── App/
│   │   ├── Features/
│   │   │   ├── Discovery/
│   │   │   ├── Connection/
│   │   │   ├── Upload/
│   │   │   └── Persistence/
│   │   ├── Core/
│   │   │   ├── Network/
│   │   │   ├── Storage/
│   │   │   └── Models/
│   │   └── Resources/
│   └── StartToolerTests/
├── android/
│   ├── app/
│   │   ├── src/main/kotlin/
│   │   │   ├── com.starttooler.app/
│   │   │   ├── features/
│   │   │   ├── core/
│   │   │   └── data/
│   │   └── build.gradle.kts
│   └── app/build/
├── docs/
│   ├── demand/
│   │   └── 01-mobile-app.md       # 镜像 PC 端 demand/05
│   ├── spec/
│   │   └── 01-mobile-app.md       # 镜像 PC 端 spec/05
│   ├── architecture/
│   │   └── 01-mobile-app-arch.md  # 镜像 PC 端 architecture/05
│   └── api/
│       ├── API-01-http-routes.md  # 镜像 PC 端 KB
│       ├── API-03-udp-broadcast.md
│       ├── API-05-app-persistence.md
│       └── API-06-error-i18n.md
├── tools/
│   └── sync-kb.sh                 # 从 PC 端 repo 拉 KB
└── CHANGELOG.md
```

**iOS / Android 共享 docs/**（文档不分家），便于跨端开发者读。

### 为什么不拆 iOS / Android 两个仓库

- 共享业务文档（API / 协议 / UX）
- 共享测试场景（端到端真机走查）
- 跨端 bug 同步（同一 Protocol 改动）
- 总代码量小（iOS 5MB + Android 3MB）

未来若团队扩大（iOS 团队独立 / Android 团队独立），可拆。

## 决策影响

### 必须做的事

1. **创建仓库**：在 fishhex 组织下创建 `starttooler-app` 仓库
2. **写 README**：跨仓库说明、协议文档链接
3. **设置 CI**：
   - iOS: GitHub Actions + `macos-latest` + `xcodebuild`
   - Android: GitHub Actions + `ubuntu-latest` + `gradle`
4. **建立 issue 模板**：跨仓库影响模板
5. **写 sync-kb.sh**：从 PC 端拉 KB 文档

### 不要做的事

- ❌ 不在 PC 端仓库里放 iOS / Android 项目文件
- ❌ 不让 PC 端 PR 改 App 端代码
- ❌ 不共享 .git 内部文件 / 配置

## 后悔成本

| 时点 | 成本 |
|---|---|
| 立即后悔 | 低（仓库还没建） |
| 1 个月后 | 中（CI 同步配置要改） |
| 6 个月后 | 高（团队 / CI / issue 都重做） |

**建议**：仓库位置在 1 周内不重启，否则后悔成本上行。

## 关键日期

- 决策日期：2026-08-15
- 期望建仓日期：2026-08-22
- 期望 App 端 MVP：2026-09-30
