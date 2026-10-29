# AGENTS.md — WindowsProcessViewer

Windows 桌面工具：列出全部进程，并回答「属于什么软件 / 作用 / 谁启动的 / 能否关闭 / 如何关闭」。
可选接入 MiniMax AI。用户可见说明在 `README.md`，本文件只记容易踩错的地方。

## 技术栈（用户已明确指定）

- **.NET 9 + WinForms**，`net9.0-windows`，SDK 式 csproj。用户要求「有 .NET 9 就用 .NET 9，不要装多余的其他」——
  不要降到 net8.0，也不要安装任何 SDK/工作负载。
- NuGet 依赖只有两个：`System.Management 9.0.0`（WMI）、`Microsoft.Data.Sqlite 9.0.0`（缓存）。加新依赖前先问用户。
- 界面全部代码构建（`Forms/MainForm.cs`），没有 designer / `.resx`，不要引入设计器文件。
- 面向用户的文案一律中文，新增字符串也保持中文。
- `app.manifest` 为 `asInvoker`；高 DPI 由 csproj 的 `ApplicationHighDpiMode` 控制，
  **不要**再往清单里加 `dpiAware`（会触发 WFO0003 警告）。

## 命令

- 构建：`dotnet build -c Release`（要求 0 warning / 0 error）。
- 采集链路自检：`bin\Release\net9.0-windows\WindowsProcessViewer.exe --export <csv>`，退出码 0、约 260 行。
- AI 自检（会真实消耗 token）：`... --ai-test`，退出码 0 表示 Key/区域/模型可用且返回 JSON 可解析。
- GUI 冒烟：启动 exe 后用 UIA 读 DataGrid 的 `RowCount` / `ColumnCount` 与状态栏文字，不必截图；
  中文断言写入 UTF-8 文件再用 read 工具查看（本机控制台是 936 代码页）。
- **多显示器：调试时一律加 `--screen 1`**，把窗口放到副屏（DISPLAY1，`X=-1920`，OpenCode 所在屏），
  不要占用用户主屏（DISPLAY5，3440x1440，`X=0`）。可用 UIA `BoundingRectangle` 断言 `X < 0`。
- 结束进程逻辑改动后，用临时控制台工程引用本项目调用 `ProcessTerminator` 验证（无界面、不抢焦点），
  不要靠 UI 自动化点右键菜单：`InvokePattern` 在模态确认框上会阻塞，菜单项坐标在多屏/高 DPI 下还可能为负值。

## 数据流（改功能前先读）

```
Program → ConfigService.Load（%APPDATA%）→ AppCache（cache.db 整表读入内存）
        → MainForm.RefreshAsync (Task.Run)
             ProcessCollector.Collect
                WMI 一次取 Win32_Process（命令行/父进程/路径）
                WMI 一次取 Win32_Service（PID→服务）
                P/Invoke 令牌取运行账户与是否提权
                WinVerifyTrust 校验签名（含目录签名）——命中缓存则跳过
                读 Run 键 + 启动文件夹判断开机自启
             ProcessAnalyzer.AnalyzeAll
                知识库 → 缓存 → 规则推断；LauncherResolver 算“谁启动的”；AI 结果从缓存回填
             AppCache.Flush（只写 Dirty 行）
        → 筛选 → SortableBindingList → DataGridView
```

AI：`AiService` 把进程信息压成纯文本发给 MiniMax `/v1/chat/completions`，要求返回 JSON 数组，
按 `pid` 回填 `Ai*` 字段并写进缓存。**AI 结论与内置结论分开存储，互不覆盖**，界面分列显示。

## 容易改错的地方

- **配置路径是 `%APPDATA%\WindowsProcessViewer\appsettings.json`**，不是 exe 同目录。
  用户曾在源码根目录改 Key、程序却读输出目录那份，白折腾一轮；不要再改回 exe 目录。
  仓库里的 `appsettings.json` 只是模板，`WPV_CONFIG` 可覆盖路径。
- 配置含真实 API Key：不要打印 Key、不要写进日志/CSV/README 示例/提交信息。
- 知识库键是「小写、去掉 `.exe`」（`KnowledgeBase.Normalize`）；`Add()` 是覆盖写，
  `Seed*` 调用顺序决定同名键谁生效，新增条目不要与已有键冲突。
- 结束进程的硬保护集中在 `Services/ProcessTerminator.IsBlocked`（`ForbiddenNames` + `pid <= 4` + 本程序自身），
  右键菜单启用判断和 `MainForm.KillSelected` 都调用它；不要在界面里另写一套判断，也不要放宽。
- `Process.MainModule` / `Modules` / `StartTime` 对跨位数、受保护进程会抛 `Win32Exception`，
  采集必须逐进程 try/catch，单个失败不能中断整体（现实现已如此）。
- WMI 可能整体不可用；`WmiQuery` 返回空表后必须仍能列出进程，只损失命令行与父进程。
- **CPU% 首次刷新为 0 是预期行为**（需要前后两次采样），不要改成瞬时值。
- 签名走 `SignatureChecker`（WinVerifyTrust）。不要退回 `X509Certificate.CreateFromSignedFile`：
  已过时（SYSLIB0057），且把目录签名的系统文件误判为“无签名”。
- `appsettings.json` 允许 `//` 注释和尾逗号（`JsonCommentHandling.Skip`），不要改成严格 JSON。
- **“谁启动的”不是运行账户**：账户在 `Owner` 列。启动者由 `LauncherResolver` 计算：
  命令行 `--webview-exe-name` / `--user-data-dir` / `--app-path` → 父进程链 →
  跳过 `extracted.exe`、`crashpad_handler`、纯版本号命名等辅助进程 → 退回同名主进程。
  改动这条链路时要同时看 `CommandLineSummary` 列，别把账户又拼回 `LaunchedBy`。
- 命令行的宿主线索有噪声：`--user-data-dir` 里的 `CEF`、`Electron`、`User Data`、`desktop` 这类通用目录名
  不能当软件名（`GenericTokens`），`--app-path` 只认 `.exe`（Electron 常指向 `app.asar`）。
  否则 WPS 的 CEF 子进程会被误标成“CEF 启动”。
- SQLite 缓存的键是「路径」：临时目录里的同名程序会各存一条；文件大小/写入时间变化才重读版本与签名，
  **不要**在 `GetVersionMeta` 里无条件覆盖缓存，否则每次刷新都会写盘。
- 缓存写入必须走 `AppCache.Flush()`（Dirty 标记），不要在采集循环里直接逐条 `Upsert`。
- **排序三角别只依赖 `SortGlyphDirection`**：本机主题下 DataGridView 不画那个三角。现在的做法是
  `ApplyCurrentSort` 同时把表头文字改成 `列名 ▲/▼`（基名存在 `column.Tag`），glyph 仅作兜底。
  点击事件走 `ColumnHeaderMouseClick` + `SortableBindingList.SortBy`（`BindingList.ApplySort` 是显式接口实现，不能直接调）。
- AI 批量分析不要用 `UseWaitCursor`（用户反馈只看到转圈、看不到进度）：用状态栏 `ToolStripProgressBar`
  + `ShowAiProgress(done,total)` 显示“已处理/总数（百分比）”。
- 异步回调可能在窗口关闭后返回：每个 `await` 之后、更新 UI 之前先判 `IsDisposed || Disposing`。
- 自动刷新定时器、排序与筛选都在 UI 线程；采集分析必须在 `Task.Run` 里。

## 验证边界

- 没有测试工程、lint、CI；要新增先问用户。
- **不要**拿系统进程或用户正在使用的程序测试“结束进程”。只可用一次性进程（如 `powershell Start-Sleep`）验证，
  并在回复里说明用的 PID、是否已清理。
- `--ai-test` 会真实调用接口并产生费用，只在用户明确要求验证 AI 时运行。
- 本机只有 .NET SDK 6.0.428 / 9.0.320，没有 8.x；不要新增 `global.json` 锁 8.x。
