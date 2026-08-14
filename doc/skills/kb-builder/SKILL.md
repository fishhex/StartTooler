---
name: kb-builder
description: "Generates and maintains the business knowledge base for the StartTooler project. Invoke when the user asks to organize project knowledge, write a business dictionary, build/maintain the KB at doc/knowledge-base/, audit KB links, or add a new business object / scenario / half-built item."
description_zh: "为 StartTooler 项目生成并维护业务知识库。当用户要求整理项目知识、写业务字典、生成/维护 doc/knowledge-base/、审计 KB 链接、追加业务对象/场景/半成品时调用。"
user-invocable: true
disable-model-invocation: true
---

# kb-builder — StartTooler 业务知识库构建器

> 这是仓库内文档化的工作流定义（位于 `doc/skills/kb-builder/SKILL.md`），
> 与代码同源维护。**模型若在 user-invocable 模式下被显式触发**才进入此流程。

## 1. 触发判定

**进入条件（满足任一）：**
- 用户说「整理项目知识库 / 写业务字典 / 更新 KB / 审计 KB」
- 用户要求追加某个新业务对象 / 场景 / 半成品条目
- 用户要求校验 KB 中链接 / 行号 / 中文锚点 / `file://` 残留

**不进入条件：**
- 用户要求写技术参考 / API 文档 / 架构图 / schema —— KB 只覆盖业务侧
- 用户要求引用 `doc/0.10/`、`doc/0.11/`、`doc/0.12/` 中尚未实现的规范 —— KB 不向其反向追溯
- 用户要求修代码（KB 是事实归档，**永远不改代码**）

## 2. 工作目录

```
doc/knowledge-base/
├── README.md                    · 索引
├── 00-product-overview.md       · 产品定位 / 6 页 / UI 概念 / 不在产品内
├── 01-objects.md                · 业务对象字典（19 个，目前）
├── 02-scenarios.md              · 7 个用户场景脚本 + 配置矩阵
├── 03-half-built.md             · H1-H3 半成品 + D1-D9 设计取舍
└── scripts/
    └── validate.py              · KB 链接 / 行号 / 平衡校验
```

**绝对不写到 `doc/knowledge-base/` 内：** SQL schema、namespace、MVVM 工具包、Trace 日志细节、Go↔C# 协议、构建命令。

## 3. 事实源优先级（从高到低）

1. **当前代码**：`StartTooler/ViewModels/`、`StartTooler/Views/`、`StartTooler/Controls/`、`StartTooler/Services/`、`StartTooler/Data/`、`StartTooler/Models/`
2. **根 README.md**：已发布功能的官方列表
3. **`doc/knowledge-base/` 既有内容**：可作为增量上下文，不作为权威
4. **旧版规范**（`doc/0.10/`、`doc/0.11/`、`doc/0.12/`）：**仅在用户明确要求按规范实现时**才参考；默认不交叉引用

## 4. 行号引用铁律

每条业务对象 / 场景 / 半成品条目**必须**配 `file:line` 引用：

| 维度 | 规则 |
|---|---|
| 形态 | `../../StartTooler/Views/SettingsView.axaml#L118-L131` |
| 范围 | 单行 `:L118` 或区间 `:L118-L131`，端点闭合 |
| 路径形态 | **相对路径**，禁止 `file:///Users/...` |
| 链接文字 | 文件 basename（例：`SettingsView.axaml:118-131`），与 URL 的 basename 一致 |
| 中文锚点 | 禁止 `#lightbox灯箱` 之类；改用「见本篇『Lightbox（灯箱）』」 |
| 验证 | 每次写完跑 `python3 doc/knowledge-base/scripts/validate.py` |

## 5. 业务对象字典条目结构（01-objects.md）

每个对象三段：

```
### <对象名>（中文）
- **业务定义**：1-2 句说清这个对象是什么
- **用户看到什么**：UI 形态（控件、文字、图标、状态、徽章）
- **用户能做的动作**：动词列表，每条配 [file:line] 引用
- **所在页面**：快捷键 / Tab 名 / 触发时机
- **见 [00 §X.Y](../../knowledge-base/00-product-overview.md)**：链到 00 中的概念解释（相对 KB 目录的路径）
```

## 6. 场景脚本写法（02-scenarios.md）

每场景 N 步，每步格式：

```
[N]. <动词> [<对象: ObjectName>] — <一句话说明>
    [file:line] 关键代码或 XAML
```

**约定标记 `[对象: X]` 不是 markdown 链接**，开头就要告诉读者用 Ctrl+F 定位。

## 7. 半成品条目结构（03-half-built.md）

```
### H<n> — <半成品名>
- **现状**：代码已落库/已实现 / UI 部分可见 / 仅 API 暴露
- **缺口**：用户视角缺什么动作
- **已落代码**：[file:line] 关键实现位置
- **未来增量需求**（不实现）：交付时可被谁代替；用一行话
```

## 8. D 系列设计取舍条目

```
### D<n> — <取舍名>
- **要解决的问题**：
- **当前方案**：
- **被否定的方案**：列出 1-2 个替代选项 + 为什么不选
- **用户感知到的副作用**（如有）：
```

## 9. 校验与修复流程

每次新增 / 修改 KB 后：

```bash
python3 doc/knowledge-base/scripts/validate.py
```

| 退出码 | 处理 |
|---|---|
| 0 | 通过，可继续 |
| 1 | 必修 error，常见为：(a) 行号漂移 / (b) text-url basename 不一致 / (c) 中文锚点 / (d) 路径损坏 |
| 2 | 仅 warn，多为表格列不一致 / URL 含中文，可继续 |

修复完 → 再跑一次校验直到 exit=0。

## 10. 退出条件

- 4 篇或新结构文档落盘
- `validate.py` 退出码为 0
- 行号引用基于实际代码，且对得上文档里的描述
- 用户口风出现新需求 → 再次进入第 1 节「触发判定」循环

## 11. 不做的事（明确边界）

| 类型 | 例子 | 处理 |
|---|---|---|
| 技术栈描述 | SQLite、Cloudflare R2、OssClient、SSH.NET | **不写**到 KB |
| 跨模块铁律 | 路径规范化 / Go↔C# JSON-line 协议 | **不写**到 KB |
| 未实现规范 | `doc/0.12/demand/01-cross-device-sync.md` | 不做交叉引用 |
| Schema 详情 | 表结构、字段类型、PRIMARY KEY | 不写 |
| 启动 / 构建命令 | `dotnet build`、release 配置 | 不写 |
| 测试 / CI / 调试 | xUnit、断言、Trace | 不写 |
