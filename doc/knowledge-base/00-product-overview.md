# 00 · 产品定位与全景

## 一、产品名

**星助 StartTooler**。一个为天文摄影爱好者做的本地媒体管理工作台。设置页关于 Tab 写法：「星助 StartTooler」[SettingsView.axaml:634](../../StartTooler/Views/SettingsView.axaml#L634)。

## 二、用户是谁

天文摄影爱好者。一次出门拍一个目标（星云、星系、行星），能产出 30–500 张照片，本地存在某文件夹/外接硬盘里，需要：

- 把照片按时间排好浏览、挑出能用的
- 给照片打标签（「昴星团」「拉直」「过曝」）和评级（1–5 星）
- 备份到阿里云 OSS，手机扫码随时访问
- 拍完之后回顾这次出门：「那天晚上拍了多少张、目标是什么、天气怎么样、写了点笔记」

星助就是为这个流程做的工作台。本地软件，单文件应用，跑在 macOS / Windows / Linux 上。

## 三、核心场景链

一次完整的使用流程：

```
选择项目目录  →  导入照片（拖拽或刷新扫描）
              ↓
        浏览（时间 / 标签双视图，缩略图 + 灯箱）
              ↓
        AI 打标（按厂商 LLM 自动加主体标签）或人工编辑
              ↓
        评分 / 拉直 / 等修图动作
              ↓
        上传到 OSS（局域网直传 或 公网 VPS 中转）
              ↓
        删除本地（进回收站，待确认后清空）
              ↓
        拍完一次，写日记（地点 / 天气 / 笔记 / 精选照片）
              ↓
        下次回来随时按时间 / 按标签重新浏览
```

## 四、六个页面

应用主窗口是一条左侧 NavRail + 右侧内容区的两栏布局 [MainWindowViewModel.cs:18-26](../../StartTooler/ViewModels/MainWindowViewModel.cs#L18-L26) + [MainWindow.axaml:43](../../StartTooler/Views/MainWindow.axaml#L43)。六个页面 + 全局快捷键：

| # | 名称 | 职能 | 快捷键 |
|---|---|---|---|
| 1 | **媒体** Gallery | 浏览、按时间/标签筛选、AI 打标、上传、删除 | `Ctrl+1` |
| 2 | **上传服务** Upload | 启动局域网扫码上传 / 配置公网 VPS 中转 | `Ctrl+2` |
| 3 | **垃圾筒** Trash | 恢复误删、清空两段（已上传 / 仅本地） | `Ctrl+3` |
| 4 | **设置** Settings | 项目目录 / 主题 / OSS / AI / 高级 / 关于 4 个 Tab | `Ctrl+4` |
| 5 | **日记** Diary | 拍摄会话回顾，地点·天气·笔记·精选 | `Ctrl+5` |
| 6 | **高级** Advanced | 上传任务管理 + 数据库（开发者工具） | `Ctrl+6` |

页面切换在 [MainWindowViewModel.cs:281-471](../../StartTooler/ViewModels/MainWindowViewModel.cs#L281-L471)，每个 `NavigateToXxx` 都对应一个 `[RelayCommand]`。

## 五、关键 UI 概念

### 5.1 三种媒体类型

工具栏右上的过滤器有四项 [MainWindow.axaml:199-259](../../StartTooler/Views/MainWindow.axaml#L199-L259)：

- **全部**——所有媒体
- **图片**——单张照片（jpg / png / raw / fit / tif 等）
- **视频**——视频文件
- **采集序列**——`.ser` 多帧原始数据（ZWO ASICAP / SharpCap）。瓦片上显示相机图标 + "SER" 文字占位 [GalleryView.axaml:416-432](../../StartTooler/Views/GalleryView.axaml#L416-L432)

视频 / SER 双击不进灯箱，按 Space 触发系统默认播放器 [LightboxWindow.axaml.cs:60-67](../../StartTooler/Views/LightboxWindow.axaml.cs#L60-L67)。

### 5.2 两种标签

- **主体标签**——天文对象名（"昴星团"、"M42"、"仙女座大星系"等），大约 18 个常驻分类，在 Gallery 瓦片底部条显示 [GalleryView.axaml:496-517](../../StartTooler/Views/GalleryView.axaml#L496-L517)
- **质量标签**——拍摄瑕疵（"拉直"、"过曝"、"脱焦"、"拉丝"等），大约 10 个，仅在瓦片 hover 或键盘焦点时显示（贴下方 chip） [GalleryView.axaml:519-537](../../StartTooler/Views/GalleryView.axaml#L519-L537)

### 5.3 同步状态徽章

瓦片右上角一个小圆图标 [GalleryView.axaml:475-489](../../StartTooler/Views/GalleryView.axaml#L475-L489)，4 状态合一：

- 颜色——蓝（已上传）/ 橙（部分）/ 灰（未上传）/ 红（失败）
- 图标——checkmark / 部分进度 / cloud / alert
- hover 看 tooltip 详情

### 5.4 通知系统

右下角 Toast 浮层 [MainWindow.axaml:415-433](../../StartTooler/Views/MainWindow.axaml#L415-L433)，三类：

- **进度类**——长任务进度条，2 秒后自动消失（[GalleryViewModel.cs:226-388](../../StartTooler/ViewModels/GalleryViewModel.cs#L226-L388)）
- **简短类**——"已恢复 xxx"等一次性提示
- **错误类**——红色边框、长期挂

状态栏右下的铃铛可以看历史 [MainWindow.axaml:466-512](../../StartTooler/Views/MainWindow.axaml#L466-L512)。

### 5.5 三类主题

设置→通用：「Deep Space」（默认）+「Red Night Vision」（红色夜间模式，不破坏暗适应）[SettingsView.axaml:118-131](../../StartTooler/Views/SettingsView.axaml#L118-L131)。

### 5.6 状态栏（窗口底部 28px）

[MainWindow.axaml:436-514](../../StartTooler/Views/MainWindow.axaml#L436-L514)

- 左侧——「刚刚刷新 / N 分钟前刷新 / yyyy-MM-dd HH:mm 刷新」（仅 Gallery 显示）
- 中间——「当前过滤 X GB / 总占用 Y GB」文字 + 浅色背景胶囊（无独立进度条）
- 右侧——通知历史铃铛

## 六、不在产品形态内的功能

明确说"我们不做"的：

- ❌ 多设备同步——`doc/0.12/spec/01-cross-device-sync.md` 有规划但代码层无 Sync 服务
- ❌ 星空地图媒体墙——`doc/0.12/demand/01-star-map-media-wall.md` 仅停留在需求文档
- ❌ 插件系统——`doc/0.12/demand/01-plugin-system.md` 仅停留在需求文档
- ❌ 设备管理 / 批量改名 / 一键合成——`doc/0.11/demand/13-equipment-management.md` 等仅停留在需求文档
- ❌ 仪表盘统计页——`doc/0.11/spec/19-statistics-dashboard.md` 仅停留在需求文档（**半成品**，见 [03-half-built.md](03-half-built.md)）
