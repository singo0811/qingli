<p align="center"><img src="src/TidyWin.App/Assets/TidyWin.png" width="96" alt="TidyWin"></p>

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

### Exit-address check (Security page → AI network check)

Pressing the button tests AI endpoints directly and through the computer's proxy, asks the services which country they see you from, and rates the exit address. The rating is TidyWin's own estimate (data-center / VPN / proxy / Tor / abuse flags, whether DNS lookups leave from the same country, whether the system time zone fits the exit's location, whether the address changed since last time). It is not any AI service's verdict.

- The VPN / proxy / Tor / abuse flags come from [ipapi.is](https://ipapi.is), which needs a key. Put your own (free) key in `"ipQualityKey"` in `settings.json` or the `TIDYWIN_IPAPI_KEY` environment variable. Without it the score only uses the network name and says so. The key is sent to ipapi.is only.
- DNS is tested with `whoami.akamai.net`; the last 20 results are kept in `%LOCALAPPDATA%\TidyWin\exit-history.json`.
- Browser fingerprint and WebRTC leak tests belong to web pages; a desktop program cannot run them.
- Nothing is sent until you press the button.

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

AI 联网检查还会给出口地址估一个分(机房 / VPN / 代理 / Tor / 滥用记录、DNS 是否同出口、时区是否对得上、是否与上次不同),是本程序自己的估算,不是任何 AI 服务的判定。VPN / 代理 / Tor / 滥用这几项要你自己的 [ipapi.is](https://ipapi.is) 免费密钥,写进 `settings.json` 的 `ipQualityKey` 或环境变量 `TIDYWIN_IPAPI_KEY`;没填就只按网络名称估计并如实说明。浏览器指纹和 WebRTC 泄漏属于网页检查,桌面程序测不了。点按钮前不会发任何请求。

见上方 English 部分的命令。更新来源默认为空,此时程序不会连接任何地方;设置 `updateFeed` 后每天最多查一次,点击后才会下载安装,下载内容必须与清单里的 `sha256` 和大小一致。

### 致谢

灵感来自 macOS 清理工具 Mole 的思路;TidyWin 是独立的原创实现,没有使用它的代码、文案、图标或名称。
