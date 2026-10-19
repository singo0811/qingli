# TidyWin

[English](#english) · [中文](#中文)

## English

A small, careful Windows cleaner and optimizer, built for the era when AI tools quietly fill your disk. WinUI 3, self-contained, no telemetry, no background service.

**The one rule: never delete something you did not clearly understand.** Every item says what it is and what happens if you remove it. Nothing is ticked for you where the data could be your own work, and every delete goes through the same guarded engine.

### What it does

- **System clean-up** and **AI junk clean-up**: caches and leftovers of Claude, Codex, Cursor, Gemini, Windsurf, Cline, Ollama, ComfyUI, LM Studio, Hugging Face and others.
- **Erase AI traces**: conversations, sign-ins, caches and settings of one AI tool at once, including per-project `.claude` / `.cursor` / `.codex` / `.gemini` / `.windsurf` folders (listed, never ticked by default) and Store/MSIX installs. It also tells you what has to be done on the server side.
- **Disk analysis** (parallel full-disk scan), **find big AI files**, **local models** (Ollama, LM Studio, Hugging Face), **software** (uninstall via the program's own uninstaller, winget updates), **start-up items**, **memory**, **security check** (read-only, including AI-connectivity diagnosis), **system health**, **minor fixes**.
- English and Chinese UI (follows Windows; switch with the button in the title bar).

### Safety design

- `PathGuard`: a path must be inside an allowed root, never a reparse point / junction, never a system or user-profile folder itself.
- One `OperationGate`: only one thing that changes the computer runs at a time.
- Every clean-up supports a **dry run**; the CLI only does dry runs.
- Long-running programs (the tool's own host process) must be closed before their data is touched.

### Build

Requirements: Windows 10 1809+ / 11, .NET 10 SDK.

```powershell
dotnet test tests\TidyWin.Core.Tests
dotnet test tests\TidyWin.Platform.Tests
dotnet build src\TidyWin.App -c Release
```

Command line (preview only): `tidywin --dry-run [system|ai|all] [--json] [--lang=en]`.

### Installer and updates

```powershell
winget install JRSoftware.InnoSetup
installer\build.ps1 -Version 1.0.0 -BaseUrl https://your.host/tidywin
```

This publishes the app, builds `dist\TidyWin-Setup-<version>.exe` (per-user, no administrator prompt) and writes `dist\latest.json`.

The app never contacts anything until you set `"updateFeed": "https://…/latest.json"` in `%LOCALAPPDATA%\TidyWin\settings.json`. Then it checks once a day (`"autoCheckUpdates": false` turns that off), and only installs after you click. The feed must be https, and the download must match the `sha256` and `size` in it.

> The checksum protects against corruption and a swapped file, not against a hacked host, because the checksum comes from the same place. Sign the installer and host the feed somewhere you control.

### Languages

Source text is Chinese; the Chinese sentence is the key into `src/TidyWin.Core/Localization/en.json`. A sentence without a translation shows in Chinese. Contributions of other languages are welcome.

### License

[Apache-2.0](LICENSE).

### Credits

Inspired by the idea of Mole (a macOS cleaner). TidyWin is an independent, original implementation: no code, text, icons or name are taken from it.

## 中文

一个小而谨慎的 Windows 清理优化工具,为"AI 工具悄悄把磁盘塞满"这个年代而做。WinUI 3,自包含,无遥测,无后台服务。

**核心原则:不删自己没弄明白的东西。** 每一项都写明它是什么、删掉会怎样;可能是你自己作品的数据不会替你勾选;所有删除走同一套带保护的引擎。

### 功能

- **系统清理**、**AI 垃圾清理**:Claude、Codex、Cursor、Gemini、Windsurf、Cline、Ollama、ComfyUI、LM Studio、Hugging Face 等的缓存和残留。
- **清除 AI 痕迹**:一次清掉某个 AI 工具的对话、登录、缓存和设置,包括各项目里的 `.claude` / `.cursor` / `.codex` / `.gemini` / `.windsurf`(只列出,默认不勾选)和 Store/MSIX 版本;并告诉你服务器那边还要自己做什么。
- **磁盘分析**(并行全盘扫描)、**找 AI 大文件**、**本地模型**、**软件管理**(调用软件自己的卸载程序、winget 更新)、**开机启动**、**内存**、**安全体检**(只读,含 AI 联网诊断)、**系统健康**、**小修小补**。
- 中英文界面(跟随 Windows,标题栏按钮可切换)。

### 构建、安装包与更新

见上方 English 部分的命令。更新来源默认为空,此时程序不会连接任何地方;设置 `updateFeed` 后每天最多查一次,点击后才会下载安装,下载内容必须与清单里的 `sha256` 和大小一致。

### 致谢

灵感来自 macOS 清理工具 Mole 的思路;TidyWin 是独立的原创实现,没有使用它的代码、文案、图标或名称。
