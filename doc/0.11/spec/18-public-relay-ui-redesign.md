# 0.11 — PublicRelayView UI 重设计（公网代理设置）

> 基于新版设计稿，将 `UploadServerView.axaml` 中内嵌的「公网代理设置」Expander 重构为独立的两栏表单布局。所有颜色、间距、圆角必须复用现有设计 tokens，禁止引入新的硬编码色值。
>
> 对应需求：`doc/0.11/demand/04-upload-improve.md`。本次 spec 只做视觉层重构，不删除已有行为，不改动 ViewModel 逻辑。

---

## 1. 总体布局

```
ScrollViewer
└── StackPanel (Margin="40,24" Spacing="24")
    ├── Grid (ColumnDefinitions="* *" 间距 24)
    │   ├── Border — LAN 上传卡片
    │   └── Border — 扫码上传卡片
    └── Expander — 公网代理设置（本次重构目标）
```

- **位置**：继续放在双卡片下方，仍是 `Expander`，默认折叠。
- **背景**：`Bg.Surface`（同卡片背景，保持与设计稿深色容器一致）。
- **展开内容背景**：`Bg.SurfaceElevated`。
- **内部布局**：将现有的「第一步 / 第二步 / 日志」垂直堆叠，重构为**左右两栏**。
  - 左栏：第一步 VPS 连接 + 保存按钮。
  - 右栏：第二步服务配置 + 操作按钮 + 状态 + 日志。
- 顶部增加标题行：左侧地球/隧道图标 + 标题 + 副标题 + 右上角状态徽章「已保存 ✓」（同设计稿绿色胶囊）。

---

## 2. 视觉规范（全部走 tokens）

| 属性 | Token | 说明 |
|------|-------|------|
| 卡片/Expander 背景 | `Bg.Surface` | 容器背景 |
| 展开内容背景 | `Bg.SurfaceElevated` | 两栏表单区 |
| 字段输入框背景 | `Bg.SurfaceElevated` | 深色输入框 |
| 标题 | `Text.Primary` `16 SemiBold` | "公网代理设置" |
| 副标题 | `Text.Secondary` `12` | "Public Tunnel · 通过 VPS 中转访问本机" |
| 说明文案 | `Text.Secondary` `12` | 顶部提示行 |
| 字段标签 | `Text.Primary` `13` | 左对齐字段名 |
| 字段值/占位符 | `Text.Primary` / `Text.Tertiary` | 已填 / 未填 |
| 步骤标题 | `Text.Primary` `14 SemiBold` | "第一步：VPS 连接" |
| 状态徽章 | `State.Success` | "已保存 ✓" 绿色胶囊 |
| 主按钮 | `primary-button` | 启动服务（渐变/主色） |
| 次要按钮 | `secondary-button` | 保存、部署 |
| 步骤序号圆点 | `Accent.Stellar` / `Text.Secondary` | 当前步骤亮，未完成灰 |
| 输入框圆角 | `Radius.Medium` | `8` |
| 卡片内边距 | — | `24` |

**禁止**：
- 输入框使用白色/浅色背景。
- 使用硬编码渐变、紫色氛围光。
- 新增任何不在现有 `Styles.axaml` tokens 中的颜色。

---

## 3. 顶部标题行

```
[图标]  公网代理设置        [已保存 ✓]
        Public Tunnel · 通过 VPS 中转访问本机
```

- 左侧图标：32×32 圆角容器，背景 `Bg.SurfaceElevated`，内嵌 `Icon.Globe`（地球）或复用现有 `Icon.Upload`，颜色 `Accent.Stellar`。
- 主标题：「公网代理设置」`Text.Primary` `16` `SemiBold`。
- 副标题：「Public Tunnel · 通过 VPS 中转访问本机」`Text.Secondary` `12`。
- 右上角徽章：同设计稿绿色胶囊，文字「已保存 ✓」（`State.Success`），仅当 `PublicRelayViewModel.Step1Done` 为 true 时显示。

---

## 4. 顶部说明文案

保留现有提示：
> 通过 SSH 在 VPS 上部署代理，手机扫码通过公网中转到本机 StartTooler。

- 颜色 `Text.Secondary`，字号 `12`，`TextWrapping="Wrap"`。
- 项目目录未设置时的警告文案保留，使用 `State.Danger`。

---

## 5. 两栏表单布局

### 5.1 左栏：第一步 VPS 连接

- 步骤标题行：
  - 左侧圆点数字「1」，当前激活态用 `Accent.Stellar` 实心圆；完成态用 `State.Success` 圆点 + 对勾（或直接用文字「1」）。
  - 文字「第一步：VPS 连接」`Text.Primary` `14` `SemiBold`。
- 字段列表（标签在上、输入框在下，垂直排列）：
  - 认证方式：`ComboBox`（密码 / SSH Key）。
  - SSH Host：`TextBox`，水印「例如 47.111.138.46」。
  - SSH Port：`NumericUpDown`。
  - User：`TextBox`。
  - 凭据：密码 `TextBox PasswordChar="•"` 或 Key 路径 + 浏览按钮。
  - Remote Path：`TextBox`。
  - VPS 架构：`ComboBox`（自动检测 / amd64 / arm64）。
- 底部按钮：「保存 VPS 连接」`primary-button`（设计稿中为绿色/主色按钮，带对勾）。

### 5.2 右栏：第二步服务配置

- 步骤标题行：
  - 左侧圆点数字「2」，未激活时 `Text.Secondary` 灰色空心/实心圆；激活/完成态 `Accent.Stellar`。
  - 文字「第二步：服务配置」`Text.Primary` `14` `SemiBold`。
- 字段列表：
  - HTTP Port：`NumericUpDown`。
  - TCP Port：`NumericUpDown`。
  - 公网 Host：`TextBox`，水印「留空用 SSH Host」。
- 操作按钮组（右对齐）：
  - 「部署」`secondary-button`。
  - 「启动服务」`primary-button`（设计稿右侧紫色/主色按钮，带播放图标）。
  - 「停止」按钮在运行态显示。
- 状态行：
  - 左侧「状态」标签，右侧彩色圆点 + `RelayStateText`。
- 日志区：
  - 标题「运行日志」+ 右上角「清空日志」`secondary-button`。
  - 只读多行 `TextBox`，背景 `Bg.SurfaceElevated`，字号 `11`，等宽字体。

---

## 6. 状态徽章与按钮可见性

| 条件 | UI 表现 |
|------|---------|
| `Step1Done == true` | 顶部显示「已保存 ✓」绿色徽章；右栏可见。 |
| `Step1Done == false` | 右栏隐藏，只显示左栏保存按钮。 |
| `Step2Done == true` | 右栏标题旁显示「已部署 ✓」。 |
| `IsPublicRelayRunning == true` | 状态圆点高亮，显示「停止」按钮。 |
| `IsProjectPathSet == false` | 顶部显示 `State.Danger` 警告：「请先在「设置」里选择项目目录」。 |

---

## 7. ViewModel 复用说明

所有绑定继续沿用 `PublicRelayViewModel` 现有属性，**不新增字段**：

- `AuthMethodIndex`
- `SshHost`, `SshPort`, `SshUser`, `SshPassword`, `SshKeyPath`, `SshRemotePath`
- `RemoteArchIndex`
- `HttpPort`, `TcpPort`, `PublicHost`
- `IsDirty`, `IsBusy`, `Step1Done`, `Step2Done`, `CanDeploy`, `CanStart`
- `RelayStateText`, `RelayStateColor`, `PendingCount`, `LastError`, `LogText`
- `SaveCommand`, `DeployCommand`, `StartCommand`, `StopCommand`, `BrowseKeyCommand`, `ClearLogCommand`

---

## 8. 验收要点

- [ ] 公网代理设置区域展开后为左右两栏。
- [ ] 所有颜色使用 `Bg.*`、`Text.*`、`State.*`、`Accent.*` tokens。
- [ ] 输入框保持深色背景，无浅色/白色输入框。
- [ ] 顶部显示标题、副标题、地球图标、已保存徽章。
- [ ] 保存后右栏出现，顶部徽章显示「已保存 ✓」。
- [ ] 部署/启动/停止行为与 v0.10 一致。
- [ ] 日志区保留清空按钮与自动滚动。
