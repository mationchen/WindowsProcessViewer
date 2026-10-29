# 会话交接单（HANDOVER）

## 0. 基本信息

| 项 | 内容 |
|---|---|
| 项目 | WindowsProcessViewer —— Windows 进程查看器（来源 / 作用 / 谁启动的 / 能否关闭） |
| 仓库路径 | `C:\Extech\ExtWebsites\WindowsProcessViewer` |
| 最后更新 | 2026-10-29 17:10（本地时区） / 会话：进程查看器从零搭建 + AI/缓存/启动者识别 |
| 分支 / 工作区 | **尚未 `git init`**，无分支、无提交 |
| 当前版本 | `1.0.0`（csproj `<Version>`），未发布 tag |
| 运行环境 | Windows 10/11 x64；.NET SDK 9.0.320（无 8.x）；`net9.0-windows`；WinForms |

## 1. 这一轮的目标

按用户三条需求做一个本机 Windows 小工具：列出全部进程；标明属于什么软件、作用、谁启动的、能否关闭/怎么关；
可填 MiniMax API Key 让 AI 辅助判断。后续追加：AI 进度条、关闭建议多选筛选、排序三角标识、SQLite 缓存。

## 2. 已完成（必须带验证方式）

| 改动 | 涉及文件 | 怎么验证的 / 结果 |
|---|---|---|
| WinForms 主界面（14 列 + 详情 + AI 页） | `Forms/MainForm.cs` | 启动后 UIA 读 DataGrid：265~277 行 × 14 列，状态栏“刷新完成” |
| 进程采集（WMI + Process + 令牌 API + WinVerifyTrust + 服务/自启） | `Services/ProcessCollector.cs`、`WmiQuery.cs`、`NativeProcess.cs`、`SignatureChecker.cs`、`AutostartScanner.cs` | `--export` 导出 265 行，`所属软件`/`能否关闭` 无空值 |
| 内置知识库 + 规则推断 | `Services/KnowledgeBase.cs`、`ProcessAnalyzer.cs` | `svchost`→服务宿主/谨慎关闭；`lsass`→禁止关闭；未知 exe 按厂商/路径/签名推断 |
| “谁启动的”= 启动程序（不是账户） | `Services/LauncherResolver.cs` | `msedgewebview2` → `clash-verge.exe（Clash Verge）`；微信子进程 → `Weixin.exe（PID 12408）`；WPS 子进程 → `wps.exe` |
| MiniMax AI（OpenAI 兼容接口 + JSON 解析） | `Services/AiService.cs` | `--ai-test`：连通正常，3/3 条解析成功（token 929+411） |
| AI 结果持久化 + 应用信息缓存 | `Services/AppCache.cs`（SQLite `cache.db`） | 首次导出 2.37s → 二次 0.89s；重启后 AI 结论仍带出（CSV 中 7 行有值） |
| AI 进度条 / 关闭建议多选 / 排序 ▲▼ | `Forms/MainForm.cs`、`Services/SortableBindingList.cs` | UIA：进度条可见 10 次采样；筛选“禁止关闭”265→10 行，加“可以关闭”→76 行；点 PID 列头两次首行为 34424、33648、32844（降序） |
| 结束进程保护 | `Services/ProcessTerminator.cs` | 无界面探针：普通进程可结束；`csrss`/`lsass`/PID≤4/自身被拦截 |
| 文档 | `README.md`、`AGENTS.md`、`.gitignore`、`appsettings.json`（模板） | 阅读核对 |

## 3. 当前状态

- **能跑**：`C:\Extech\ExtWebsites\WindowsProcessViewer\bin\Release\net9.0-windows\WindowsProcessViewer.exe`（可加 `--screen 1` 放到副屏）。
- **卡点**：无。
- **未提交**：全部源码（仓库尚未 `git init`）。`bin/`、`obj/`、`cache.db`、`Logs/`、`*.csv` 已在 `.gitignore`。
- **遗留数据**：真实 API Key 在 `%APPDATA%\WindowsProcessViewer\appsettings.json`（**不在仓库内**，不要在提交里出现）；
  仓库内 `appsettings.json` 的 `ApiKey` 已清空。测试用一次性进程已结束，临时脚本/截图已清理。

## 4. 关键决策（为什么这么做）

- **.NET 9 + WinForms**：用户明确「有 .NET 9 就用 .NET 9，不要装多余的其他」；只用 `System.Management`、`Microsoft.Data.Sqlite` 两个依赖。
- **配置放 `%APPDATA%\WindowsProcessViewer\appsettings.json`**：用户曾在仓库根改 Key、程序读 exe 目录那份，白折腾一轮；固定单一位置后不会随重编译被覆盖。`WPV_CONFIG` 可覆盖。
- **SQLite 缓存（`cache.db`）**：用户要求“不要每次都现查”。启动整表读入内存、只写 Dirty 行；文件大小/写入时间变化才重读版本与签名。
- **“谁启动的”用命令行 + 父进程链推断**：任务管理器里能看到的 `--webview-exe-name` / `--user-data-dir` 才是宿主的真实线索；账户单独一列。
- **排序标识写进表头文字（▲/▼）**：本机视觉样式下 `SortGlyphDirection` 的三角根本不渲染（截图验证），glyph 仅作兜底。
  - 放弃：仅依赖 glyph（用户看不到排序方向）。
- **AI 分析不用 `UseWaitCursor`**：用户反馈只看到转圈；改为状态栏进度条 + “已处理/总数（百分比）”。

## 5. 已知坑（现象 → 原因 → 绕法）

- **`dotnet build` 报 exe 被占用**：程序还在跑。→ 先关进程再构建。
- **Key 填了却提示未配置**：改了仓库里那份 `appsettings.json`。→ 看 `%APPDATA%` 那份，界面上“打开配置文件”直接打开真实路径。
- **`CEF` 之类的通用数据目录**：`--user-data-dir` 里出现 `CEF`/`Electron`/`User Data`/`desktop` 时不能当软件名，否则 WPS 子进程被标成“CEF 启动”。已在 `LauncherResolver.GenericTokens` 过滤；`--app-path` 只认 `.exe`（Electron 常指向 `app.asar`）。
- **CPU% 首次为 0**：需要两次采样，属正常。
- **UIA 点右键菜单不可行**：`InvokePattern` 在模态确认框上会阻塞，多屏/高 DPI 下菜单项坐标可能为负；结束进程逻辑改用临时控制台工程直接调 `ProcessTerminator` 验证。
- **UIA 的 `Invoke` 打列头不触发 `ColumnHeaderMouseClick`**：只能用真实鼠标点击验证排序。
- **本机没有 .NET 8 SDK**：不要加 `global.json` 锁 8.x。

## 6. 下一步（可执行）

1. [ ] 由用户执行 `git init` + 首次提交（见下节命令；Agent 不代跑 git 写操作）。
2. [ ] 若要更准的“所属软件”：把本机不认识的进程（如 `CEF`、`app.asar`）补进 `KnowledgeBase`，或对它们跑一次 AI 让结果入缓存。
3. [ ] 可选：列宽/可见列做成 `Ui` 配置项；AI 批量分析加“取消”按钮。
4. [ ] 可选：给 `LauncherResolver`、`ProcessAnalyzer` 补最小单元测试（目前无测试工程，需先与用户确认）。

## 7. 常用命令（照抄即可）

```powershell
# 构建（先确认没有实例在跑）
Get-Process WindowsProcessViewer -ErrorAction SilentlyContinue | Stop-Process -Force
cd C:\Extech\ExtWebsites\WindowsProcessViewer; dotnet build -c Release

# 启动（副屏，避免占用主屏）
.\bin\Release\net9.0-windows\WindowsProcessViewer.exe --screen 1

# 采集链路自检（退出码 0、约 265 行）
.\bin\Release\net9.0-windows\WindowsProcessViewer.exe --export E:\OpenCodeCache\windows-temp\opencode\p.csv

# AI 自检（会真实消耗 token）
.\bin\Release\net9.0-windows\WindowsProcessViewer.exe --ai-test
```

## 8. 环境与外部依赖

- 显示器：DISPLAY1（副屏 1920×1080，`X=-1920`，OpenCode 所在）/ DISPLAY5（主屏 3440×1440）；调试一律 `--screen 1`。
- 配置：`%APPDATA%\WindowsProcessViewer\appsettings.json`（含真实 Key，**不提交**）；模板在仓库根。
- 数据：`cache.db`（exe 同目录，已 gitignore）；日志 `Logs\WindowsProcessViewer_yyyyMMdd.log`。
- 外部服务：MiniMax OpenAI 兼容接口，国内 `https://api.minimaxi.com/v1/chat/completions`（Key 与区域必须匹配）。

## 9. 相关链接（可选）

- 使用说明：`README.md`
- 项目规范：`AGENTS.md`
- 指令日志：本仓库尚无 `开发相关资源/历史迭代指令汇总.md`（如需按全局约定记录，可新建）
