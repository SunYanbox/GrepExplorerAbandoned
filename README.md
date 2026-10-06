# Grep Explorer

一个面向 Windows 的 WPF 桌面工具：以 **ripgrep 语义的 Glob** 与 **.NET Regex 的 Grep** 搜索本地文件，
内置 **Markdown 编辑器**、**正则编辑器**、**grep 风格命令编辑器**、**组管理** 与 **历史记录**，
全部 UI 为中文，主题跟随系统深浅色（WPF Fluent）。

## 1. 构建步骤

要求：Windows 10/11 + [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)（net10.0-windows）。

```powershell
# 还原 + 编译（EnableWindowsTargeting 已在 Directory.Build.props 中设置）
dotnet build GrepExplorer.slnx -c Release

# 运行
dotnet run --project src/App/App.csproj -c Release

# 运行单元测试（97 条，xunit）
dotnet test tests/Core.Tests/Core.Tests.csproj
```

- 产物：`src/App/bin/Release/net10.0-windows/GrepExplorer.exe`
- 数据目录：`%LOCALAPPDATA%\GrepExplorer\`（`history.db`（WAL）、`snapshots\`、`config.json`）
- 多实例：允许同时运行多个实例；每次启动的工作目录 = 进程启动目录（`Environment.CurrentDirectory`），不恢复"上次目录"。

## 2. 快捷键

| 位置 | 快捷键 | 作用 |
|---|---|---|
| Markdown / 正则 / 结果编辑器 | `Ctrl+F` | 查找（AvalonEdit SearchPanel） |
| Markdown / 正则 / 结果编辑器 | `Ctrl+H` | 查找替换（仅当前文件） |
| Markdown 编辑器 | `Ctrl+S` | 保存为 .md（来源于搜索/历史时自动追加 front matter） |
| 搜索页 | `取消` 按钮 | 终止正在执行的搜索（流式） |
| 主窗口 | `Alt+F4` / 关闭 | 触发"未保存更改"对话框（保存到文件 / 放弃更改 / 取消操作） |

## 3. 组与历史数据表结构

数据库：`%LOCALAPPDATA%\GrepExplorer\history.db`，`PRAGMA journal_mode=WAL`，`busy_timeout=5000`，
写入遇 `SQLITE_BUSY` 指数退避重试 3 次（100/200/400 ms）。

### Groups（组）

| 列 | 类型 | 说明 |
|---|---|---|
| Id | TEXT PK | GUID |
| Alias | TEXT UNIQUE NOT NULL | 唯一、非空、≤32 字符 |
| Include / Exclude / ExcludeFrom / ExcludeDir | TEXT | 每行一条规则（`''` 为空） |
| Remark | TEXT | 备注 |
| CreatedAt / UpdatedAt | TEXT (ISO 8601) | 时间戳 |
| IsBuiltIn | INTEGER | 1 = 内置组（图标区分，可编辑可删除，可"恢复内置组"） |

辅表 `AppMeta(Key TEXT PK, Value TEXT)`：`BuiltInsSeeded=1` 记录内置组种子已插入（幂等）。

### History（历史）

| 列 | 类型 | 说明 |
|---|---|---|
| Id | INTEGER PK AUTOINCREMENT | |
| CreatedAt | TEXT (ISO 8601) | 时间戳（倒序） |
| Mode | TEXT | `Glob` / `Grep` |
| RootPath | TEXT | 根目录（"当前项目"归属判断字段） |
| GroupAliases | TEXT | 勾选组别名快照（每行一条） |
| Include/Exclude/ExcludeFrom/ExcludeDir | TEXT | 四类规则快照 |
| Pattern | TEXT | 正则 / Glob 表达式快照 |
| CommandText | TEXT | 规范化命令文本（去重键 = RootPath + CommandText） |
| HitFiles / TotalHits | INTEGER | 命中文件数 / 总命中行数 |
| SnapshotBytes | INTEGER | 结果快照字节数（插入时计算，统计用 `SUM`，禁止重算） |
| SnapshotPath | TEXT | 外置快照文件路径（随记录同步删除） |

历史上限可配置（`config.json` 的 `HistoryLimit`，`-1` = 无上限），超出滚动删除最旧；
六种清理入口（全部 / 当前项目全部 / 全局 ≥N 天 / 当前项目 ≥N 天 / 勾选记录 / 重复项只留最新）
均在事务内执行，执行前展示预估影响条数。

## 4. 命令语法参考（应用内命令编辑器）

块 ↔ 命令文本双向转换；解析为手写三段式（分词 → flag 识别 → 值提取，报错带 token 位置）。
执行永远以块列表为准；文本手改后必须点"解析"刷新块，否则执行前弹确认。

```
--mode grep | --mode glob        模式（恰好一个，最前）
-e "PATTERN"                     Pattern（多值 OR；仅 Grep）
-f "FILE"                        Pattern 文件（每行一条；仅 Grep）
-g "GLOB"                        Glob 表达式（多值；仅 Glob 模式）
-E（默认，省略）/ -F              方言；-G/-P 可解析并映射为 .NET Regex（提示差异）
-i / --no-ignore-case            大小写（省略 = 区分）
-w / -x                          整词 / 整行（互斥，后选者胜）
-v                               反向匹配
-m "NUM"                         每文件最大命中行数（0 = 不限，截断即停读，计数严格 = NUM）
-c / -l / -L / -n                输出模式（恰好一个，省略 = -c）
-B N / -A N / -C N               上下文（Before==After>0 规范化为 -C N；-C 解析只填 Context）
-b                               字节偏移（仅 -n）
-o                               仅匹配片段（仅 -n）
--group-separator "SEP"          组分隔符（省略 = --）
-s                               抑制错误消息（隐藏"跳过文件汇总"）
--show-eol                       行尾显示（LF→$，CRLF→^M$）
-I（默认，省略）/ -a              二进制：跳过 / 当文本
-R                               跟随符号链接（省略 = 不跟随）
--hidden                         包含隐藏文件（省略 = 跳过）
--gitignore                      读取 .gitignore（省略 = 不读取）
--include "GLOB"                 Include（多值）
--exclude "GLOB"                 Exclude（多值，一票否决；! 前缀 = 豁免）
--exclude-from "FILE"            Exclude-From（多值）
--exclude-dir "GLOB"             Exclude-Dir（多值，目录名任意层级）
--group "别名"                   Group（多值，去重；含空格必须整体加引号）
"D:\path"                        根目录（位置参数，恰好一个，末尾）
```

规范序列化顺序：模式 → 大小写 → 整词/整行 → 反向 → 方言 → 最大命中 → 输出模式 → 上下文 →
字节偏移 → 仅匹配片段 → 组分隔符 → 抑制错误 → 行尾 → 二进制 → 符号链接 → 隐藏 → gitignore →
全部 Pattern/-f/-g → Include → Exclude → Exclude-From → Exclude-Dir → Group → 根目录。
同值块去重（含 Group 别名）。非 `-n` 输出模式下上下文类块（-B/-A/-C/-o/-b/--group-separator）
保留但输出为注释行 `# ...`，解析时同样处理不报错。

引号规则：规范输出统一双引号并转义 `"` 与 `\`；解析接受单/双引号与裸 token；
未闭合引号报错并定位；未列出的 grep flag（-d/-D/-z/-U/-T/-Z/--color/-q/--line-buffered/--label/-V 等）
明确报"未支持的 grep 参数"并给出最接近替代建议（编辑距离 ≤2），不阻断其余块。

> UI 默认值：搜索页与命令编辑器默认以 `-n`（行号详情）起步 —— `-c` 只给"每文件计数"，
> 看不到命中行内容；命令语言层面"省略输出模式"仍等于 `-c`（见上表，`CommandSpec.Output` 未改）。
> 同理 `-C 0` 表示只显示命中行本身，Before/After/Context 都是**额外**多显示的行数。

### Glob 语义（对齐 ripgrep）

- `*` 不跨 `/`；`**` 跨 `/`（`a/**/b` 可匹配 `a/b`）；`?` 匹配单字符（不含 `/`）；
- `[abc]` / `[!abc]` 字符类；
- 以 `/` 开头相对搜索根；不含 `/` 的模式匹配任意层级的名称；其余锚定到根；
- 尾部 `/` 仅匹配目录；`!` 前缀（include 列表中等价 exclude）；
- 匹配时统一使用 `/` 分隔符（Windows 路径在匹配前归一化）。

### 多实例并发

SQLite WAL + `busy_timeout=5000ms` + SQLITE_BUSY 指数退避重试（连接工厂单例）；
内置组补插、历史清理在事务内执行；快照文件写入数据库事务提交后进行。

## 5. 项目结构

```
GrepExplorer.slnx
src/App               启动、DI 装配、Fluent 主题（/PresentationFramework.Fluent;component/Themes/Fluent.xaml）
src/UI/Views          SearchView（含命令编辑器子视图）、MdEditorView、RegexEditorView、GroupsView、HistoryView
src/UI/ViewModels     与 View 一一对应（MVVM / CommunityToolkit.Mvvm）
src/UI/Controls       CodeEditor（AvalonEdit 封装）、着色、InlineDiff、Context 三输入框、标签列表 TagListControl（回车建标签 / 双击改名 / ✕ 删除 + 语法说明）
src/Core/Globbing     Glob 解释器（ripgrep 语义）
src/Core/Grep         流式搜索引擎（IAsyncEnumerable + Channel）+ 命令解析/序列化 + front matter 单一实现
src/Core/Groups       组 CRUD、规则合并（include OR / exclude OR / 一票否决）、内置组种子
src/Core/History      历史持久化、六种清理、滚动上限
src/Core/Diff         DiffPlex 词级 Inline Diff
src/Infrastructure    SQLite 连接工厂（WAL/重试）、文件系统遍历、AppConfig
tests/Core.Tests      97 条单元测试
```

## 6. 第三方依赖（仅限清单内）

| 包 | 版本 | 用途 |
|---|---|---|
| AvalonEdit | 6.3.1.120 | 编辑器内核（Markdown / 正则 / 结果展示） |
| CommunityToolkit.Mvvm | 8.4.0 | MVVM |
| Microsoft.Extensions.DependencyInjection | 10.0.0 | DI |
| Microsoft.Data.Sqlite | 10.0.0 | 存储 |
| DiffPlex | 1.7.2 | 词级 diff |
| SQLitePCLRaw.lib.e_sqlite3 | 2.1.13 | 显式覆盖 `Microsoft.Data.Sqlite` 传递引入的 2.1.11（NU1903：GHSA-2m69-gcr7-jv3q） |
| xunit / Microsoft.NET.Test.Sdk | — | 测试 |

> WPF Fluent 主题由 .NET 10 运行时内置提供（`/PresentationFramework.Fluent;component/Themes/Fluent.xaml`），
> 跟随系统深浅色，无需第三方包。

## 7. 行为边界（明确不做）

不做 Markdown 预览 / HTML / PDF；不做跨文件批量替换；不自动读 .gitignore（除非 `--gitignore`）；
不做组间 AND；不做正则调试器；不做多语言/主题切换；不做启动参数解析；
编辑器内容永不回写数据库；UNC 网络路径扫描前需确认。
