# 10 — 已知陷阱与决策记录

> **目的**：把踩过的坑 + 已决策的「为什么不那样做」集中沉淀。每条记录格式 = **情境 → 坑 → 解决 → 教训**。
>
> 每条都在对应模块 spec (`00-09`) 里被引用，避免重复决策。

---

## 总目录

| # | 主题 | 陷阱分类 | spec |
|---|---|---|---|
| 1 | Trace 写到 `Console.WriteLine` 不可见 | 启动 | 01 |
| 2 | `async void` 启动异常未捕获 | 启动 | 01 |
| 3 | WinExe cwd 不可写 | 启动 | 01 |
| 4 | `dotnet clean` 不清 relay-binaries | 构建 | 01 |
| 5 | `Config.db` 表名大小写历史 | 数据 | 02 |
| 6 | 路径规范化（`Path.GetFullPath + TrimEnd`） | 数据 | 02,05 |
| 7 | DB 写失败不撤销 UI | 数据 | 04 |
| 8 | rawvideo AVI input seek 无效 | 媒体 | 03 |
| 9 | FFMpegCore SDK 强制 PNG | 媒体 | 03 |
| 10 | SkiaSharp 解码失败静默 | 媒体 | 03 |
| 11 | Aliyun OSS Region 带 "oss-" 前缀 | OSS | 04 |
| 12 | OSS 签名 URL 时区错位 | OSS | 04 |
| 13 | OSS SDK 同步阻塞 | OSS | 04 |
| 14 | Multipart 取消不 Abort | OSS | 04 |
| 15 | `OnSelectedFilesChanged` 不处理 Reset | Gallery | 05 |
| 16 | `IsSelected` 反向越权 | Gallery | 05 |
| 17 | LIMIT 1000 截断 | Gallery | 05 |
| 18 | `_isInitialized` 未守门 | Settings | 06 |
| 19 | OSS Provider UI 占位 | Settings | 06 |
| 20 | `RecentDirectories` 字符串匹配重复 | Settings | 06 |
| 21 | multipart 字符串解析崩溃 | LAN | 07 |
| 22 | Windows netsh urlacl | LAN | 07 |
| 23 | QR Bitmap stream Dispose 顺序 | LAN | 07 |
| 24 | 公网 QR 切 LAN QR 状态 | LAN | 07 |
| 25 | 公网 TCP JSON 长度前缀 | 公网 | 08 |
| 26 | Go relay 32MB io.ReadAll | 公网 | 08 |
| 27 | Go relay HTTP 不响应 SIGTERM | 公网 | 08 |
| 28 | `ReplayPending` 满 buffer 丢消息 | 公网 | 08 |
| 29 | 退出兜底 5s timeout | 公网 | 08 |
| 30 | RelayBinaryExtractor `TryChmod755` Windows | 公网 | 08 |
| 31 | 公网接收：scp batch + ControlMaster + 60s Timeout + KeepAlive | 公网 | 02, 08 |
| 32 | `PathConverter` 用 `StreamGeometry` | UI | 09 |
| 33 | NotificationCard `IsHitTestVisible` | UI | 09 |
| 34 | `DynamicResource` vs `StaticResource` 主题 | UI | 09 |
| 35 | `System.Diagnostics.Trace` vs `Debug` | 跨模块 | — |
| 36 | `starttooler.db` 早期实验死文件 | 数据 | 02 |
| 37 | `Process.Dispose()` 后读 `ExitCode` 抛 "No process is associated with this object" | 公网 | 08 |
| 38 | `ConfigService.InitializeDatabase` 迁移顺序错：新装抛 `no such table: config` | 数据 | 02 |
| 39 | `GalleryView` 中心空态 StackPanel 叠加（`HasNoProject` / `IsEmpty` 不互斥） | UI | 05, 09 |

---

## 详细记录

### 1. Trace 写到 `Console.WriteLine` 不可见
- **情境**：开发时习惯 `Console.WriteLine(...)` 调试
- **坑**：项目 `<OutputType>WinExe</OutputType>`（`StartTooler.csproj:3`），没有 console 句柄，`Console.WriteLine` 静默失败
- **解决**：所有日志用 `System.Diagnostics.Trace.WriteLine(...)`；`Program.cs:14-25` 已注册文件监听器，输出到 `cwd/starttooler-debug.log`
- **教训**：diagnostic info / state change 必须用 Trace；UI 反馈用 `NotificationService`

### 2. `async void` 启动异常未捕获
- **情境**：`App.OnFrameworkInitializationCompleted` 用 `async void` （`App.axaml.cs:19`）
- **坑**：Avalonia 启动早期 `async void` 异常不会冒到外层；展示为「窗口没起」
- **解决**：内部 try/catch 已实现（`App.axaml.cs:32-54`）
- **教训**：`async void` 必须在方法体顶 try/catch

### 3. WinExe cwd 不可写
- **情境**：双击 `.app` / `.exe` 启动时 cwd 是 `/`（macOS）或安装目录（不可写）
- **坑**：`File.Create("starttooler-debug.log")` 抛 UnauthorizedAccessException
- **解决**：`Program.Program` 静默 try/catch（`Program.cs:23-25`）
- **教训**：捕获后输出日志用别的方案，比如 listener 还可以装吗？目前没找到简化办法

### 4. `dotnet clean` 不清 relay-binaries
- **情境**：`dotnet clean` 清 `bin/obj`，但 `StartTooler/Resources/relay-binaries/*.bin` 在源码目录里
- **坑**：mtime 检查基于源文件；如果删了源但保留旧二进制，build 不会重编
- **解决**：手动 `rm` 或 `mavis-trash`；或保留源不动；脚本仅在二进制 mtime 旧于源时重编（幂等）
- **教训**：CI 用 `git clean -fdx` 之前保证本地编译过

### 5. `Config.db` 表名大小写历史
- **情境**：早期 `ConfigService.cs:36-42` 用 `CREATE TABLE IF NOT EXISTS config`（小写），v0.1 改成 `Config`（大写 C）「迁就 ORM」
- **坑**：与 `media.db` 的 `media_files` / `upload_jobs` 全小写命名不一致；SQLite 大小写不敏感，但 ORM/迁移脚本分大小写，跨工具时仍然踩坑。**额外坑**：SQLite 3.51.0 的 `ALTER TABLE ... RENAME TO` 检查目标名跟现有表是否冲突时用 NOCASE 比较，`Config → config` 单步会报 "already another table or index with this name"
- **解决**：v0.2 改回全小写 `config`（`ConfigService.cs:40,68,89`），与项目内其他表对齐；启动时 `InitializeDatabase` 检测到旧 `Config` 表自动两步 RENAME（`ConfigService.cs:48-67`）—— 先 `Config → Config_temp` 再 `Config_temp → config`，绕开 NOCASE 冲突，PRIMARY KEY 的 sqlite_autoindex 自动跟着搬过去，老用户数据零损失
- **教训**：表名一开始定全小写；项目内一致性优先于「迁就 ORM」；**SQLite RENAME 不能直接改大小写**，要么两步中转，要么 INSERT INTO 新表 + DROP 旧表（更慢）

### 6. 路径规范化（`Path.GetFullPath + TrimEnd`）
- **情境**：项目路径存到 `media_files.project_path`
- **坑**：写时未 trim 尾 `/`，读时匹配 `~/shots` vs `~/shots/` 双条目
- **解决**：所有读写 `project_path` 前/后都 `Path.GetFullPath(...).TrimEnd(Path.DirectorySeparatorChar)`（见 `02-data-layer.md` §3.3）
- **教训**：路径入库必须规范化 + 读取也必须规范化；唯一约束不能取代规范化

### 7. DB 写失败不撤销 UI
- **情境**：`UploadManyAsync` 中上传成功，但 `ApplyUploadSuccessAsync` 写 DB 时 DB 抛异常
- **坑**：发现后撤销 `MediaFile.IsUploaded = true` 会让用户重新上传同一文件；不撤销就状态错位
- **解决**：保持 UI 是 Uploaded，下次重试会再写 DB；catch silent（`GalleryViewModel.cs:880-897`）
- **教训**：UI state is source of truth for the current session；DB 是 persistence layer，会自然重新 sync

### 8. rawvideo AVI input seek 无效
- **情境**：用户用 ASICAP 接望远镜拍 video 是 rawvideo AVI（1920x1080 每帧 6MB）
- **坑**：`-ss T -i input -vframes 1` 让 ffmpeg 抓 `T` 秒位置的帧 → ffmpeg 走 demuxer seek → rawvideo 没有关键帧表 → ffmpeg **强制忽略 -ss**，从头解码 → 抓出相机初始化阶段的暗帧
- **解决**：用 `-vf "thumbnail=N"` filter 扫前 N 帧找色彩变化最大的（`FfmpegSnapshotRunner.cs:10-31`）；覆盖足够长（76fps 130 秒，25fps 400 秒）
- **教训**：依赖 input seek 前先确认 codec 有关键帧表；rawvideo 不是 → 必须用别的方法

### 9. FFMpegCore SDK 强制 PNG
- **情境**：早期上传 `FFMpegCore` 5.1.0
- **坑**：`SnapshotAsync` 内部强制用 PNG codec 写输出并自动改扩展名 → 视频缩略图全是 PNG，违反设计
- **解决**：彻底移除该包，自己 `Process.Start` 调命令行（`Services/FFmpegConfigurator.cs:7-18`）
- **教训**：第三方 SDK 包装的「便利方法」隐藏细节；要可控 / 可调，必须 raw subprocess

### 10. SkiaSharp 解码失败静默
- **情境**：`SKBitmap.Decode(stream)` 返 null
- **坑**：如果吞了不写日志，下次问题排查会让人怀疑是 SkiaSharp 版本
- **解决**：thumbnail gen 内 catch + Trace 写日志（`ThumbnailService.cs:65-70`）
- **教训**：吞异常必写日志，「上次坑就坑在这」是该函数注释的原话

### 11. Aliyun OSS Region 带 "oss-" 前缀
- **情境**：用户 region 填 `oss-cn-hangzhou.aliyuncs.com`
- **坑**：构造 endpoint 时拼出 `https://oss-oss-cn-hangzhou.aliyuncs.com.aliyuncs.com`（双前缀）
- **解决**：`BuildEndpoint` 先判 `http(s)://` 起始则原样；否则默认补 `oss-` + `aliyuncs.com`（`AliyunOssStorage.cs:303-312`）；构造器尾打印 `endpoint=` + `region(in)=` 让用户一眼核对
- **教训**：构造器主动 echo 配置输入 → 错配时直接可读

### 12. OSS 签名 URL 时区错位
- **情境**：`GeneratePresignedUri(..., expiresAt)` 用绝对 DateTime
- **坑**：阿里云签名 URL 用本地时区校验；如果 `DateTime.UtcNow.Add(expiry)` → 14:00 本地应是 06:00 UTC → 落地用户看变成 06:00 本地，URL 提前过期
- **解决**：必须 `DateTime.Now`（本地时间）；`AliyunOssStorage.cs:111-115` 注释断言
- **教训**：跨时区调用，先写测试 case 验证边界

### 13. OSS SDK 同步阻塞
- **情境**：阿里云 SDK `PutObject` 是阻塞 IO
- **坑**：在 UI 线程直接 await 会卡住；上传队列时并发 1
- **解决**：包 `await Task.Run(() => _client.PutObject(...))`（`AliyunOssStorage.cs:69-75`）
- **教训**：第三方 SDK 默认同步阻塞；并行化用 Task.Run 让出线程

### 14. Multipart 取消不 Abort
- **情境**：用户中途 cancel 或 app 崩溃 → 进程退出 → 不再 Resume
- **坑**：OSS 端 multipart sessions 留底占用配额
- **解决**：当前不主动 Abort（v0.1 简化），依赖 OSS 默认 TTL（阿里云保留 1 天后清理）
- **教训**：资源回收需要 explicit，但 v0.1 加 `AbortMultipartAsync` 占位接口待用

### 15. `OnSelectedFilesChanged` 不处理 Reset
- **情境**：`GalleryViewModel.OnSelectedFilesChanged` 处理 `NewItems / OldItems`
- **坑**：`ObservableCollection.Clear()` 触发 `Reset`，**不**带 NewItems/OldItems → IsSelected 漏同步
- **解决**：当前用 `_isMultiSelectMode = false` 触发时显式 `SelectedFiles.Clear()`，`Reset` 不会发生在多选模式内 → 不影响
- **教训**：`Reset` + `Clear` 是序列化操作；改 ObservableCollection 时小心

### 16. `IsSelected` 反向越权
- **情境**：`mf.IsSelected = true` 反向更新 `SelectedFiles`
- **坑**：VM 还要反向同步，反人类且难维护
- **解决**：`mf.IsSelected` 单向由 `SelectedFiles.CollectionChanged` 同步（`GalleryViewModel.cs:88-115`）；反向不要维护
- **教训**：单向数据流是真理

### 17. LIMIT 1000 截断
- **情境**：`GetByDateAsync` 用 `LIMIT 1000`
- **坑**：单日超 1000 文件时静默丢失
- **解决**：v0.1 不解决；v0.2 加分页
- **教训**：用户感知不到 silent failure 是大坑

### 18. `_isInitialized` 未守门
- **情境**：`SettingsViewModel.OnFfmpegPathChanged(...)` 触发 `RecomputeDirty`
- **坑**：初始化时 `FfmpegPath = savedValue` 也会触发 → IsDirty 在 Initialize 一开始就 true
- **解决**：所有 `On*Changed` 都 `if (!_isInitialized) return;`
- **教训**：初始化期禁副作用；用 flag 守门

### 19. OSS Provider UI 占位
- **情境**：`OssProvider` ComboBox 渲染 "Aliyun" 项
- **坑**：用户期望选了别的 Provider 就用别的实现；但目前只有 Aliyun
- **解决**：`BuildOssConfigFromVm` 硬编码 "Aliyun"，OssProvider **不** 持久化（`SettingsViewModel.cs:121` 注释）
- **教训**：UI 留口子，逻辑不预承诺

### 20. `RecentDirectories` 字符串匹配重复
- **情境**：用 `Contains` 判断
- **坑**：`~/shots` vs `~/shots/` 重复
- **解决**：用 `Path.GetFullPath` 规范化后比较（待实现）
- **教训**：复用 `02` 的规范化原则

### 21. multipart 字符串解析崩溃
- **情境**：第一版 `ParseMultipartFiles` 用 `Encoding.UTF8.GetString(body).Split(boundary)`
- **坑**：边界附近二进制 → 解码出乱码 → split 错位 → `files.Clear()` 重新跑
- **解决**：改 `ParseMultipartFilesByBytes` 字节偏移解析（`UploadServerService.cs:304-349`）
- **教训**：二进制协议不要先 string 化再 split

### 22. Windows netsh urlacl
- **情境**：`http://+:<port>/` 前缀
- **坑**：Windows 要 ACL 才能绑定非 localhost
- **解决**：捕获 `HttpListenerException.ErrorCode == 5`，提示 `netsh http add urlacl`
- **教训**：跨平台 HTTP listener 写最常见坑

### 23. QR Bitmap stream Dispose 顺序
- **情境**：把 `Bitmap(ms)` 套在 `Dispatcher.UIThread.Post` 内层
- **坑**：外层 `using ms` 已 dispose → `new MemoryStream(pngBytes)` 不会触发 — 但 stream 已是已 dispose object → `Unable to load bitmap from provided data`
- **解决**：在 Post 外层拿到 bytes，再 Post 构造 Bitmap（`UploadServerViewModel.cs:149-170`）
- **教训**：跨线程 + IDisposable flow 必查 dispose 时机

### 24. 公网 QR 切 LAN QR 状态
- **情境**：用户启公网 relay，QR 是公网 URL；停 relay 时 QR 没立刻切回 LAN
- **坑**：用户扫了陈旧 QR，二维码 → 404
- **解决**：订阅 `RelayStateText / PublicHost / SshHost / HttpPort` 变化 → 立即刷 QR
- **教训**：UI 必须反映 state 变更；老状态危及安全时强制刷

### 25. 公网 TCP JSON 长度前缀
- **情境**：早期 PublicRelay C# 端 `ReadExactAsync` + 4 字节大端长度前缀
- **坑**：Go relay 端改用纯 JSON line 后 C# 端按 4 字节读 `{"type":...` → 前 4 字节是大端长度，**不**等于 `{`+`"ty`+`pe`… → parse 失败 → 循环断
- **解决**：C# 端改按行 `ReadLineAsync`，重新 build
- **教训**：协议变更两端必须同步；不论谁先改，commit 描述要写「协议版本」

### 26. Go relay 32MB io.ReadAll
- **情境**：上传大视频（几百 MB）
- **坑**：`io.ReadAll` 把整个 multipart 拉进 RAM
- **解决**：v0.1 限制 32MB threshold；v0.2 改 streaming `io.Copy(tmpfile)`
- **教训**：内存峰值风险

### 27. Go relay HTTP 不响应 SIGTERM
- **情境**：SSH kill <pid>
- **坑**：`http.ListenAndServe` 阻塞 → 进程被 SIGTERM 后无优雅退出，正在传的请求被截断
- **解决**：v0.1 接受；SSH 端知道会丢
- **教训**：Go `http.Server.Shutdown` 是 graceful shutdown；当前没用

### 28. `ReplayPending` 满 buffer 丢消息
- **情境**：TCP client 来不及消费，buffer (16) 满
- **坑**：`Broadcast / ReplayPending` 默认走 `default:` 丢该消息
- **解决**：v0.1 简化；client 端 ack 失败重试机制待补
- **教训**：丢消息必须能重传；v0.2 加 ack 重试 / ping/pong

### 29. 退出兜底 5s timeout
- **情境**：`AppDomain.CurrentDomain.ProcessExit` 杀 VPS relay 进程
- **坑**：5s 内 SSH 链不上 → 留着跑
- **解决**：fire-and-forget；.Wait(5s)；进程随时被 SIGKILL 不阻塞
- **教训**：退出兜底有上限

### 30. RelayBinaryExtractor `TryChmod755` Windows
- **情境**：`File.SetUnixFileMode` 在 Windows 抛 PlatformNotSupportedException
- **坑**：编译器 warning CA1416
- **解决**：`[SupportedOSPlatform]` 标注 + `#pragma warning disable CA1416`
- **教训**：API 标注跨平台，本机内容包裹 try/catch 兜底

### 31. 公网接收：scp 改为 batch + 本地 scp 命令 + SSH ControlMaster
- **情境**：v0.1 公网接收每张图走 `FetchOneAsync` = `new SshClient().Connect()` + `new ScpClient().Connect()` + 下载 + 再 `ssh.Connect()` + `RunCommand("rm -f ...")` —— **每张图 3 次 SSH 握手 + 1 次 HTTP POST**
- **坑**：100 张图触发 **300 次并发 SSH connect**，撑爆 VPS sshd 默认 `MaxStartups 10:100`（阿里云 ECS 镜像常见配置）→ sshd 主动 RST 部分连接 → 客户端看到 "Connection reset by peer"；同时 `ConnectionInfo.Timeout = 10s` 太短 → 慢链路上 KEX / scp socket read 超时 → SSH.NET 发 disconnect code 10 给服务端（sshd 日志 `[preauth]`）；加上 scp socket read 10s 超时报 "Socket read operation has timed out after 10000 milliseconds" —— **三件事叠加**
- **解决（v0.2）**：
  - 删 `FetchOneAsync`，`PublicRelayService.cs` 引入 `Channel<PendingVpsFile>` + `RunBatchWorkerAsync` + `DrainBatchAsync`
  - `RunClientLoopAsync` 收到 `file_pending` 不再 fire-and-forget 拉取，而是 `_pendingChannel.Writer.WriteAsync(...)` 入队
  - `RunBatchWorkerAsync` 凑齐 `BatchSize`（默认 5）或距首文件 `BatchIdleTimeout`（默认 30s）→ 触发 `DrainBatchAsync`
  - `DrainBatchAsync` 用 `Process.Start("scp", ...)` 一次传 N 个文件，带 SSH `ControlMaster=auto` + `ControlPath={Temp}/starttooler-ssh-{host}-{port}` + `ControlPersist=10m` —— 多次 batch 复用单 SSH 连接
  - 触发 flush 兜底：凑齐 / idle 超时 / `Stop` 按钮 / `ProcessExit` 兜底 / TCP 客户端断线，**任一满足即 flush channel 残留**
  - `BuildConnectionInfo` 改 `Timeout = 60s`（10s → 60s）+ 加 `KeepAlive = new SshKeepAlive(30s/60s)` 防 NAT 老化
  - 失败粒度：scp exit != 0 时解析 stderr `scp: .../tmp/{filename}: <reason>` 行拆失败文件 ID（v0.4+ VPS 用原文件名落盘），只对失败文件标 `status=Failed`
  - 100 张图 = **20 次 batch scp**（首次 ControlMaster 握手 + 后续复用 socket），触发 sshd MaxStartups 概率大幅降低
- **教训**（v0.2 → v0.3/v0.4 演进视角，部分已随设计变更而消除或被替代）：
  - **每文件 1 次 SSH connect 是反模式** —— 多文件场景必然撑爆 MaxStartups。**v0.1 → v0.2 已修复**（ControlMaster + batch scp）
  - **`ConnectionInfo.Timeout = 10s` 是 .NET SSH.NET 默认值，不是合理值** —— 慢链路上必踩；公网场景直接 60s+
  - **batch 化的成本是延迟（30s 静默）+ UX 复杂度（Stop flush）** —— ~~v0.2 push 模型必须做全 idle 超时 + 强制 flush~~。**v0.3 pull 模型已彻底消除**：Poller 直接查 `sync_for_vps_task` 拉 Pending 行，**没有 channel 也没有 idle timer**，Stop 只需 cancel Poller CTS；不再有 "凑齐/超时" 双触发条件
  - **进程崩溃会丢 channel 残留** —— ~~v0.2 接受丢失~~。**v0.3 修复**：TCP 通知立即入库（`sync_for_vps_task`），Poller 启动立即跑一次拉遗留 Pending。**v0.4 进一步简化**：scp 落盘直接用原文件名（`tmp/{filename}`），去掉 `.bin/.meta` 配套；崩溃恢复方案改成 **re-upload**（详见 `02-data-layer.md` §10.5 三方案对比，暂选 C）
  - **`StrictHostKeyChecking=accept-new` 是安全权衡** —— 首次自动 trust host key；可改 `ask` 弹框但 UX 差

### 32. `PathConverter` 用 `StreamGeometry`
- **情境**：用 `PathGeometry` 写图标
- **坑**：Bounds 延迟计算，combo/radio hover 后错位
- **解决**：全用 `StreamGeometry`
- **教训**：图标几何 shape 必须 inline

### 33. NotificationCard `IsHitTestVisible`
- **情境**：右下角通知卡片层挡着
- **坑**：默认 IsHitTestVisible=true → 用户点不到下层按钮
- **解决**：父 Panel 显式 `IsHitTestVisible="False"`（`MainWindow.axaml:183`）
- **教训**：浮层永远不挡点击

### 34. `DynamicResource` vs `StaticResource` 主题
- **情境**：XAML 用 `Background="{StaticResource Bg.Outer}"`
- **坑**：主题切换（夜间红）不生效，因为 Static 只在 load 时 resolve
- **解决**：用 `{DynamicResource ...}`
- **教训**：主题色必须 dynamic

### 35. `System.Diagnostics.Trace` vs `Debug`
- **情境**：写日志选哪个
- **坑**：`Debug.WriteLine` 在 Release 包被编译器 strip 掉
- **解决**：diagnostic → Trace；调试专用（如上传过程 detail）→ Debug
- **教训**：按可见性需求选

### 36. `starttooler.db` 早期实验死文件
- **情境**：v0 早期实验过 AI 功能 / 多云存储 / 文件指纹（MediaFiles、MediaFileRecords、AiSettings、CloudStorageSettings、RecentFolders），全部用 PascalCase + 引号 identifier 命名
- **坑**：重构后没人删，文件一直留着；新用户看 ApplicationData 目录会以为 starttooler 是项目主库（实际是 config.db + media.db）；grep 全代码 0 引用，schema 也跟现在 MediaRepository 的 `media_files` 完全对不上（早版 Id/FilePath/ThumbnailPath vs 现版 id/project_path/relative_path/...），迁移成本 > 收益
- **解决**：v0.2 在数据层文档（`02-data-layer.md` §1）明确**只用 `config.db` + `media.db` 两库**；`starttooler.db` 标为废弃，不删用户物理文件（数据所有权归用户，需要清空自行 `mavis-trash`）；.idea/dataSources.xml 里的 data source 配置不主动改（Rider 视图无影响）
- **教训**：重构删除的死文件**必须在文档显式标记**，否则新人会以为是 active schema；schema 重写时表名要对齐（早版 PascalCase 跟现版 snake_case 完全两套，对不上 = 没法迁移）

---

## 已固化决策（按模块归类）

### Settings（`06-settings.md` §3.6）

| 决策 | 原因 |
|---|---|
| Save-First 主题切换 | 立即切换会让用户选择错难调 |
| 凭据明文持久化（v0.1）| Keychain / DPAPI 迁移留 v0.2+ |
| OSS Provider 单项（Aliyun）| 真实实现只有一家；UI 留口子 |
| RecentDirectories 上限 10 | 多了不便筛选 |
| FFmpeg 路径必须校验 | 用户最常见的错：把目录当文件 |

### OSS（`04-oss-upload.md`）

| 决策 | 原因 |
|---|---|
| Multipart 阈值 5MB | 阿里云推荐值 |
| PartSize 5MB | 同上 |
| 取消不 Abort | OSS 后台会清；避免重写流程 |
| ResourceKey 一律 `/` 不用 `\` | OSS 标准 |

### 公网代理（`08-public-relay.md`）

| 决策 | 原因 |
|---|---|
| Protocol: 纯 JSON line + `\n` | 简单易调试 |
| Retry: 1s → 10s 指数退避 | VPS 短闪断秒重连，长期断也不浪费流量 |
| `ReplayPending` 满 buffer 丢 | client 来不及消费就该优化 client |
| 一进连接就 `ReplayPending` | 解决 client 断线期间上传丢通知 |
| Binary 嵌入 .NET dll | 多架构一份 dll 全平台 |
| 部署用 `setsid nohup` | SSH 退出 + detach 防 SIGHUP |
| HTTP/TCP 双 port | HTTP 上传 / TCP 通知分工 |
| HTML 模板单源 `Resources/upload.html` | LAN + 公网共用 |

### UploadServer LAN（`07-upload-server-lan.md`）

| 决策 | 原因 |
|---|---|
| 前缀 `http://+:<port>/` | 所有网卡可访问 |
| multipart name "file" 固定 | 前端 FormData 兼容 |
| 多文件支持 | 一个 POST 多个文件 |
| 不做 HTTP basic auth | LAN 受信任，可加 IP-白名单 |

### 数据层（`02-data-layer.md`）

| 决策 | 原因 |
|---|---|
| `Config.db` 与 `media.db` 拆分 | 高频小写 vs 海量数据 |
| 路径规范化双向 | 一致 round-trip |
| `UNIQUE(project_path, relative_path)` | 增量扫描幂等 |
| `parts_uploaded` 用 JSON 列 | 简单 + 量小 |

### Gallery（`05-gallery-view.md`）

| 决策 | 原因 |
|---|---|
| 缩略图并发数 = CPU*2 | ffmpeg 吃 CPU |
| `UploadStatus` UI 瞬时态 | 跨次进 Gallery 重新反推 |
| 启动恢复一次性弹窗 | DB 残留时引导用户续传 |

### 37. `Process.Dispose()` 后读 `ExitCode` 抛 "No process is associated with this object"
- **情境**：`PublicRelayService.DrainBatchAsync` 用 `Process.Start("scp", ...)` + `WaitForExitAsync` 拉文件；`finally` 块做 `proc.Dispose()` 兜底收尾；finally 之后想 Trace 打 `proc.ExitCode` 记录 exit code
- **坑**：`Process.Dispose()` 内部把 `m_processId` 置 0 + 释放 OS handle。finally 之后访问 `proc.ExitCode` → `EnsureWatchingForExit()` 看到 `m_processId == 0` → 抛 `InvalidOperationException("No process is associated with this object.")`。`proc?.ExitCode` 的 `?.` 只防 null，对 disposed Process 完全没用。**现象特征**：异常发生在 finally 之外 → 内层 catch 看不到 → 冒泡到 `PollOnceAsync` 的 catch → 日志只看到外层 `[PublicRelay] poll err: ...`，极其容易误判是 poll 逻辑本身的问题
- **解决**（`PublicRelayService.cs:540-602`）：
  - `try` 块在 `WaitForExitAsync` 成功之后、`finally` 之前立刻 `exitCode = proc.ExitCode` 抓到本地变量（带 try/catch 兜底）
  - `finally` 给 `proc?.Dispose()` 也包 try/catch，handle 已坏的极端情况不再炸
  - 最后 Trace 引用本地 `exitCode`，不再读 disposed 进程
- **教训**：
  - **任何「finally 释放资源 → 资源属性在 finally 之后读」的代码都是定时炸弹**，资源属性必须在 finally 之前读到本地变量
  - **.NET 6/7/8/9 通用**：所有 `System.Diagnostics.Process` 调 `Dispose()` 后访问 `ExitCode` / `HasExited` / `Kill` / `Refresh` 都会抛同样错
  - **跨平台**（实测 macOS CoreCLR 也成立）
  - **诊断提示**：看到 "No process is associated with this object" 第一反应查 `Process` 生命周期是不是有 finally 之后还读的代码
- **预防模板**：
  ```csharp
  int exitCode = -1;
  try {
      proc = Process.Start(psi);
      await proc.WaitForExitAsync(ct);
      // 在 finally 之前抓住
      try { exitCode = proc.ExitCode; } catch { /* handle 已坏 */ }
  } catch (Exception ex) { /* ... */ return; }
  finally {
      try { if (proc != null && !proc.HasExited) proc.Kill(true); } catch { }
      try { proc?.Dispose(); } catch { }
  }
  Trace.WriteLine($"exit={exitCode}");
  ```

### 38. `ConfigService.InitializeDatabase` 迁移顺序错：新装抛 `no such table: config`
- **情境**：`ConfigService` 启动时跑 schema 迁移，兼容老 PascalCase `Config` 表 + `Key/Value/UpdatedAt` 列，要照顾三种库状态：全空（新装）、PascalCase 老库、snake_case 老库
- **坑**：原 `InitializeDatabase` 顺序是 **`MigrateLegacyTableNameIfNeeded` → `MigrateSchemaToSnakeCaseIfNeeded` → `CREATE TABLE IF NOT EXISTS config`**。两个 `Migrate*` 方法内部都假设 `config` 表已存在（直接调 `ALTER TABLE config ADD COLUMN ...` / `RENAME COLUMN`），**新装场景**走到第二步时表还没建 → `SqliteException (0x80004005): SQLite Error 1: 'no such table: config'` → `MainWindowViewModel..ctor` 半路抛 → 整个 App 启动即崩，窗口一片空白。堆栈最深一层是 `SqliteHelpers.cs:97`（`AddColumnIfMissing` 里的 `ALTER TABLE`），表面像「迁移代码写得不对」，实际是**调用顺序错了**
- **解决**（`ConfigService.cs:30-52` + `SqliteHelpers.cs:43-53`）：
  - 把 `CREATE TABLE IF NOT EXISTS config (...)` 提到第 1 步，三步变成「先建表 → 老表名迁移 → 列名迁移」
  - 三种库状态都通过：
    - 新装：第 1 步建表；第 2 步无 `Config` 早退；第 3 步所有列已存在全部 return
    - PascalCase 老库：`CREATE IF NOT EXISTS` 看到 `Config` 已存在不动；第 2 步走两步 RENAME `Config → Config_temp → config`；第 3 步改列名 + 补 `created_at`
    - snake_case 老库：第 1 步 no-op；第 2/3 步无老列全部 return
  - 顺手给 `MigrateSchemaToSnakeCaseIfNeeded` 入口加 `TableExists(connection, "config")` 守卫，**未来如果别的代码路径绕过 `InitializeDatabase` 直接调迁移**（比如新建 service 复用这个 helper），不会再次炸同样的错
  - `SqliteMigrations.TableExists` 新 helper 走 `SELECT 1 FROM sqlite_master WHERE type='table' AND name=@name LIMIT 1`，大小写不敏感（参数化避 NOCASE 坑）
- **教训**：
  - **schema 迁移假设的"前置状态"必须显式建立** —— 不是「我觉得上一步会建好」，而是「我这一步开始前自己保证」。`CREATE TABLE IF NOT EXISTS` 永远是幂等的，应该在所有迁移之前先跑一遍；迁移代码自己也要做存在性守卫（防御性编程）
  - **启动时崩溃的调试优先级**：先看最深一层的 SQL/IO 调用，往往不是逻辑写错，而是调用顺序错。`SqliteException` 第一反应查「这个表/列**为什么不存在**」，而不是「这段 SQL 哪里写错」
  - **新装 vs 升级的兼容性测试要分开跑** —— 老库迁移代码经常漏掉空库场景，因为开发机上从来不是空库。建议：每次加迁移都至少跑两次「干净 ApplicationData 启动」+「旧版数据升级启动」

### 39. `GalleryView` 中心空态 StackPanel 叠加（`HasNoProject` / `IsEmpty` 不互斥）
- **情境**：`GalleryView.axaml` 的右侧 Grid 里有 4 个互斥容器叠在 (0,0)：无项目空态（StackPanel）、无媒体空态（StackPanel）、加载中（ScrollViewer）、正常列表（ScrollViewer），每个都靠 `HorizontalAlignment=Center / VerticalAlignment=Center` 居中
- **坑**：`HasNoProject = string.IsNullOrEmpty(_projectPath)`（路径空）和 `IsEmpty = !IsLoadingDateGroups && DateGroups.Count == 0`（已加载完但无日期组）**默认都 true**（首次启动 `_projectPath = null`、DateGroups 空、IsLoadingDateGroups=false）。Avalonia Grid 子元素默认都放 (0,0) 单元格，**两个 Visible=true 的 StackPanel 都会绘制并居中** → 4 行 TextBlock + 2 个星形图标全挤在屏幕中心，逐字叠加 → 视觉上看起来像「中文乱码」或「字体豆腐」，截图排查很容易误判为字体 fallback 问题（实际是中文正常渲染，只是叠在一起产生重影）
- **解决**（`GalleryViewModel.cs:70`）：在 `IsEmpty` 条件里加 `!HasNoProject &&` 守卫，让两个空态在 VM 层互斥：
  ```csharp
  public bool IsEmpty => !HasNoProject && !IsLoadingDateGroups && DateGroups.Count == 0;
  ```
  优先级：没项目 → 只显示「请先选择项目目录」；有项目但没媒体 → 显示「未发现媒体文件」；互不重叠
- **教训**：
  - **Avalonia Grid 子元素默认都在 (0,0)，靠 `HorizontalAlignment/VerticalAlignment` 定位时没有「互斥」语义** —— 不像 WPF `Grid` 配合 `Visibility=Collapsed` 会"让出"位置。多个 Visible 切换的子元素如果只想显示一个，**互斥逻辑必须在 VM/Selector 层显式声明**，不能靠 XAML 自动
  - **诊断建议**：截图里看到"文字重影/乱码/豆腐"先别急着改字体，先看是不是多个容器在 (0,0) 叠加。可以临时把所有空态改成不同颜色的 Border 看叠加情况
  - **长期方案**（未实施，v0.3+ 考虑）：把空态/内容改用 `ContentControl` + `DataTemplateSelector`，VM 暴露一个 `EmptyState` enum（None / NoProject / NoMedia / Loading），Selector 选唯一一个 template 渲染，从根本上消除叠加可能。或者用 `Grid.RowDefinitions` 把每个状态分到独立单元格（不推荐：动态改 RowDefinitions 麻烦）
  - **Grid (0,0) 叠加检测**：下次加新空态前先在 dev 状态把每个 StackPanel 的 Background 设成不同荧光色，启动看一眼是不是有重叠 —— 比看截图准

---

## 如何在写新代码前查这条清单

1. 改任何模块，先在 spec 找对应模块引用
2. 模块 spec 找不到 → 看本文件 §「详细记录」
3. 涉及「为什么不做」 的设计 → 看「已固化决策」
4. 没记录但新发现的坑 → 加进本文件 **再** 写代码，让下次的人不踩

任何「为什么不这样做」的决定有变更时，**同步更新本文档和对应模块 spec**。
