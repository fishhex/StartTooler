# 15 — Gallery 时间轴（v0.11）

> 对应代码：`Views/GalleryView.axaml`、`ViewModels/GalleryViewModel.cs`、`Converters/TimelineBoolConverters.cs`、`Themes/Colors.axaml`。

---

## 0. 元信息

| 项 | 值 |
|---|---|
| 文档版本 | v0.1（视觉规范草案） |
| 文档状态 | **规范 — 待实现** |
| 适用版本 | 0.11 |
| 关联模块 | Gallery 左栏时间轴、主题资源（DeepSpace / RedNightVision）、Timeline converters |

---

## 1. 设计目标

1. **信息密度**：在同一窗口展示全年日期分组，保证日期与数量双行信息在 56px 高度内清晰可读，匹配截图中的紧凑排布。  
2. **选中可感知**：当前选中日期需一眼识别，圆点、日期文字与行背景同步强化，但仍保留文字可读性。  
3. **主题一致**：所有颜色来自主题 token，允许 DeepSpace / RedNightVision 以及未来主题无条件复用，不再出现硬编码色值。  
4. **可扩展**：数字徽标需支持 4 位数（9999）仍居中展示，未来如引入标签模式可以复用同一模板。

---

## 2. 布局结构

### 2.1 列容器
- 时间轴位于 Gallery 左侧栏 `ScrollViewer` 内，宽度随面板自适应，内容左右内边距 16px，与现有 XAML 一致 @/Users/hex/code/StartTooler/StartTooler/Views/GalleryView.axaml#52-95。
- 列表顶部与底部保留 24px 呼吸距离，滚动条使用默认样式。

### 2.2 节点框架
- 单节点模板基于 `Button.timeline-node`，内部 `Canvas` 固定宽 150px、高 56px；该高度允许日期 + 数量双行排版，不得再缩减 @/Users/hex/code/StartTooler/StartTooler/Views/GalleryView.axaml#68-88。
- 左侧 8px 处绘制 10×10 圆点，圆点中心与文本基线对齐；圆点下方延伸 32px 的纵向分隔线，形成时间流逝感。

### 2.3 文本区域
- 文本容器 `StackPanel` 左边距 32px，上边距 14px，与现有实现保持一致以对齐圆点与文字。  
- 日期行使用等宽体 `Font.Mono`、13px、Bold/Normal 由选中态决定；数量行使用 10px 常规字重 @/Users/hex/code/StartTooler/StartTooler/Views/GalleryView.axaml#79-87。
- 数量行文案格式：`{photoCount} 张`，不补前导零。

### 2.4 排列与断点
- 当窗口高度不足以展示全部节点时允许垂直滚动；不进行虚拟化（条目数量通常 < 365）。
- 未来 Tag 模式可复用同一模板，但需要替换日期文本与数量描述，本规范对模板可复用性做约束。

---

## 3. 视觉状态与主题

| 状态 | 圆点填充 | 日期文字 | 数量文字 | 背景/线条 |
|---|---|---|---|---|
| 默认 | `Bg.Divider` | `Text.Secondary` | `Text.Tertiary` | 线条 `Timeline.Line` |
| Hover | `Timeline.Dot.Selected` + 20% 透明外环（可用 `Overlay.Selection`） | `Text.Primary` | `Text.Secondary` | 行背景 `Bg.HoverSubtle` |
| 选中 | `Timeline.Dot.Selected` | `Timeline.Selected` | `Text.Primary` | 行背景 `Bg.Hover` + 2px 左侧强调条 `Accent.Stellar` |

- 所有色值必须通过 `DynamicResource` 绑定到 `Themes/Colors.axaml` 中已有 token：`Timeline.Selected`、`Timeline.Dot.Selected`、`Timeline.Line`、`Bg.*`、`Accent.Stellar` 等 @/Users/hex/code/StartTooler/StartTooler/Themes/Colors.axaml#20-90。
- RedNightVision 主题将覆盖上述 token；不得在 XAML 或 converter 中直接 `Brush.Parse` 固定颜色。
- Hover 外环建议通过 `DropShadowEffect` 或绘制第二个透明度 0.2 的 14×14 圆实现，保持浅提示即可。

---

## 4. 交互行为

1. **点击**：点击节点触发 `SelectCommand` → `GalleryViewModel.SelectAsync`，切换选中态并加载对应日期媒体列表 @/Users/hex/code/StartTooler/StartTooler/Views/GalleryView.axaml#62-65 @/Users/hex/code/StartTooler/StartTooler/ViewModels/GalleryViewModel.cs#631-651。  
2. **滚轮/触摸**：默认滚动行为即可；不需要额外的惯性或分页。  
3. **键盘**：当焦点在时间轴列表时，`↑/↓` 需要顺序切换节点；`Enter` 等价点击。若控件暂不支持键盘导航，需在实现时添加 `KeyboardNavigation.TabNavigation="Cycle"` 并手动处理 `KeyDown`。  
4. **保持选中**：切换到 Tag 模式或其它 Tab 再返回时，应保留上次选中的日期，除非当前数据源为空（沿用现有逻辑）。

---

## 5. 数据与文案

- 数据源来自 `GalleryViewModel.DateGroups`，由 `_mediaRepo.GetDateGroupsAsync` 生成，包含日期与数量两个字段 @/Users/hex/code/StartTooler/StartTooler/ViewModels/GalleryViewModel.cs#602-606。  
- 数量阈值：`>= 1000` 仍直接显示 `1234 张`，不改为缩写；`0` 条（理论上不会出现）应显示 `0 张`。  
- 今日标记：若日期等于系统日期，可在数量行前追加 `•` 微符号（非必选，可延后）。

---

## 6. 主题与资源要求

1. 将 `BoolToAccentOrDividerConverter`、`BoolToAccentOrSecondaryConverter` 等迁移为使用 `IResourceHost` 动态查找颜色，彻底移除硬编码 #4FC3F7 / #FF6B6B / #8892B0 @/Users/hex/code/StartTooler/StartTooler/Converters/TimelineBoolConverters.cs#8-45。  
2. 若需要新增 hover 左侧强调条颜色，可直接重用 `Accent.Stellar`，避免创建重复 token。  
3. 在 RedNightVision 中覆盖 `Timeline.*` token，确保夜视模式下仍为红系。

---

## 7. 实施检查清单

- [ ] XAML 模板中的所有颜色改为 `{DynamicResource ...}`。  
- [ ] `TimelineBoolConverters` 支持主题切换（监听 `ActualThemeVariant` 或使用 `ThemeResourceBinding`）。  
- [ ] 选中节点左侧添加 2px 宽强调条，颜色来源 `Accent.Stellar`。  
- [ ] Hover 状态验证：鼠标停留时背景变浅、文字升一级，离开后恢复。  
- [ ] 主题切换（DeepSpace ↔ RedNightVision）下颜色即时更新。  
- [ ] 单元测试或 UI Test：模拟主题切换，验证转换器不再抛异常。
