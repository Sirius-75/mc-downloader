# mc-download：原版 Minecraft 下载脚本

> [!IMPORTANT]
> **说明 / Notice**
>
> 本项目通过 **vibe coding（AI 辅助编程）** 实现，**仅供个人使用**。不保证后续更新或维护，按「现状」提供（as-is）。
>
> This project was implemented via **vibe coding (AI-assisted)**. It is intended for **personal use only**, comes with **no guarantee of updates or maintenance**, and is provided **as-is**.

从 Mojang 官方公开文件服务器（或 BMCLAPI 镜像）下载原版 Minecraft，并写成与官方启动器、HMCL、PCL 兼容的 `.minecraft` 目录。脚本只下载文件，不启动游戏。

**English:** A standalone Windows PowerShell script that downloads vanilla Minecraft public files from Mojang’s official servers or the BMCLAPI mirror into a launcher-compatible `.minecraft` folder. It does not launch the game.

## 功能

- 支持 Windows PowerShell 5.1（Windows 10/11 自带）和 PowerShell 7。
- 默认下载最新正式版，也可指定版本号、最新快照，或只列出版本。
- 下载源可选官方、BMCLAPI，或按系统语言 / 时区自动选择。除「仅官方」外，单个文件失败会改用另一个源，最多重试 3 轮。
- 多线程下载（1–64）。已存在且通过 SHA1 与文件大小校验的文件会跳过，中断后重新运行即可补齐。
- 目录与官方启动器、HMCL、PCL 兼容：版本 json、客户端 jar、按 Windows 规则筛选的支持库和 natives（x64 / x86 / ARM64，含新旧两种 natives 格式）、资源索引与资源对象、日志配置。
- 旧版本资源会额外复制到 `assets/virtual` 或 `resources`。
- 目录里还没有 `launcher_profiles.json` 时，会写入一份最小配置，方便启动器列出该版本。

## 使用方法

双击 `mc-download.bat` 即可。它会在脚本所在目录创建 `.minecraft`，并下载当前最新正式版。

在 cmd 里也可以把参数传给 bat：

```bat
mc-download.bat -List
mc-download.bat -List -IncludeSnapshots
mc-download.bat -Version 1.20.1
mc-download.bat -Version latest-snapshot
mc-download.bat -Version 1.12.2 -GameDir D:\Games\.minecraft -Source BMCLAPI -Threads 16
```

或在 PowerShell 中直接运行（bat 已包含 `-ExecutionPolicy Bypass`）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\mc-download.ps1 -Version 1.20.1
```

## 参数

| 参数 | 说明 | 默认值 |
|---|---|---|
| `-Version` | 版本号。`latest` 与 `latest-release` 表示最新正式版，`latest-snapshot` 表示最新快照 | `latest` |
| `-GameDir` | 目标 `.minecraft` 目录 | 脚本旁边的 `.minecraft` |
| `-Source` | `Official` 只用官方源。`BMCLAPI` 镜像优先，失败再试官方。`Auto` 在简体中文系统或 UTC+8 时镜像优先，否则官方优先；除 `Official` 外，单个文件失败都会换源 | `Auto` |
| `-Threads` | 并行下载线程数，范围 1–64 | `8` |
| `-List` | 列出版本后退出，不下载 | （关） |
| `-IncludeSnapshots` | 与 `-List` 一起使用时，连同快照以及旧版 beta / alpha 一并列出 | （关） |

## Java 与启动

脚本不安装 Java，也不启动游戏。natives 由启动器在启动时解压。

| 游戏版本 | Java |
|---|---|
| 1.16.5 及更早 | Java 8 |
| 1.17.x | Java 16 |
| 1.18 – 1.20.4 | Java 17 |
| 1.20.5 及之后（含 1.21 系列） | Java 21 |

更新的版本可能需要更高的 JDK，以该版本的官方要求为准。HMCL 和 PCL 可以按版本选择或下载 Java。下载完成后，在启动器中选中这个 `.minecraft` 文件夹即可看到对应版本。

正版联机需要使用正版微软账号登录。

## 免责声明

- 本仓库是独立实现，不是从 PCL 代码派生或改写而来。
- 脚本只下载 Mojang 官方服务器或 BMCLAPI 镜像上的公开文件（版本清单、客户端、支持库、资源等），不提供盗版登录或离线账号。
- Minecraft 是 Mojang Synergies AB 与 Microsoft 的商标。本项目与 Mojang、Microsoft、BMCLAPI 没有隶属或授权关系。
- 登录正版服务器、进行联机，需要用户自己拥有正版账号。
- 软件按「现状」提供，不提供任何明示或暗示的担保。详见 [MIT License](LICENSE)。

## 许可

[MIT License](LICENSE) © 2026 Sirius-75

推送到 GitHub 后，Actions 会用 PSScriptAnalyzer 检查 `mc-download.ps1`。警告不会令该检查失败；语法错误和 Error 级别问题会使检查失败。
