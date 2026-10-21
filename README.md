# mc-download：原版 Minecraft 下载器

> [!IMPORTANT]
> **说明 / Notice**
>
> 本项目通过 **vibe coding（AI 辅助编程）** 实现，**仅供个人使用**。不保证后续更新或维护，按「现状」提供（as-is）。
>
> This project was implemented via **vibe coding (AI-assisted)**. It is intended for **personal use only**, comes with **no guarantee of updates or maintenance**, and is provided **as-is**.

从 Mojang 官方公开文件服务器（或 BMCLAPI 镜像）下载原版 Minecraft，安装到与官方启动器、HMCL、PCL 兼容的 `.minecraft` 目录。程序只下载文件，不启动游戏。

2.0.0 起附带一个图形界面 `mc-download.exe`。原来的 PowerShell 脚本仍然可以用。

**English:** A personal Windows tool that downloads vanilla Minecraft public files from Mojang’s official servers or the BMCLAPI mirror into a launcher-compatible `.minecraft` folder. Version 2.0.0 adds a single-file GUI. It does not launch the game.

## 图形界面

双击 `mc-download.exe` 即可。界面是简体中文，只有两件事：

1. 从 Mojang 版本清单里选一个版本。默认只列正式版，勾选「包含快照及旧版」后会连同快照、旧 beta 和 alpha 一起列出。
2. 选下载目录，也就是 `.minecraft` 文件夹。可以用「浏览…」，默认是 exe 旁边的 `.minecraft`。

然后点「开始下载」。窗口里有进度条和日志。下载用 8 个线程。下载源是 Auto：简体中文系统或 UTC+8 时 BMCLAPI 优先，否则官方优先；单个文件失败会换另一个源，最多重试 3 轮。已经存在且 SHA1（以及清单里的文件大小）对得上的文件会跳过，所以同一个目录可以反复下载，只补缺的文件。

下载内容和脚本相同：版本清单 v2、版本 json、客户端 jar、按 Windows 规则筛选的支持库、只下当前 CPU 架构的 natives、资源索引和资源对象、旧版 `virtual` / `map_to_resources` 资源、日志配置。目录里还没有 `launcher_profiles.json` 时，会写一份最小配置。

### 为什么是单个 exe，不用安装

`mc-download.exe` 用 **.NET Framework 4.8 / WinForms** 编译，是一个独立文件：

- Windows 10（1903 及以后）和 Windows 11 已经自带 .NET Framework 4.8，双击就能跑，不用再装运行库，也不用安装程序。
- 不写注册表，不往 AppData 或系统目录放文件。
- 游戏文件只写入你选的那个目录。
- 如果保存过下载目录，设置写在 exe 旁边的 `mc-download.settings.json`，没有别处。

从 [Release v2.0.0](https://github.com/Sirius-75/mc-downloader/releases/tag/v2.0.0) 下载 `mc-download.exe` 即可。`mc-downloader-v2.0.0.zip` 里同时有这个 exe 和原来的脚本。

在 Windows 上从源码编译（需要 .NET SDK，产物仍是依赖系统自带 .NET Framework 4.8 的单个 exe）：

```bat
dotnet build src\mc-download\mc-download.csproj -c Release
```

生成的文件在 `src\mc-download\bin\Release\net48\mc-download.exe`。

## 脚本

不想用图形界面时，双击 `mc-download.bat` 即可。它会在脚本所在目录创建 `.minecraft`，并下载当前最新正式版。

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

## 功能

- 图形界面是单个 exe，兼容 Windows PowerShell 5.1（Windows 10/11 自带）和 PowerShell 7 的脚本也还在。
- 默认下载最新正式版，也可指定版本号、最新快照，或只列出版本。
- 下载源可选官方、BMCLAPI，或按系统语言 / 时区自动选择。`-Source Official` 只用官方源；其余模式在单个文件失败时会换另一个源，最多重试 3 轮。图形界面固定使用 Auto。
- 多线程下载（脚本 1–64，界面为 8）。已存在且通过 SHA1 与文件大小校验的文件会跳过，中断后重新运行即可补齐。
- 目录与官方启动器、HMCL、PCL 兼容：版本 json、客户端 jar、按 Windows 规则筛选的支持库和 natives（x64 / x86 / ARM64，含新旧两种 natives 格式）、资源索引与资源对象、日志配置。
- 旧版本资源会额外复制到 `assets/virtual` 或 `resources`。
- 目录里还没有 `launcher_profiles.json` 时，会写入一份最小配置，方便启动器列出该版本。

## 参数

脚本参数。图形界面不提供这些开关，线程数是 8，下载源是 Auto。

| 参数 | 说明 | 默认值 |
|---|---|---|
| `-Version` | 版本号。`latest` 与 `latest-release` 表示最新正式版，`latest-snapshot` 表示最新快照 | `latest` |
| `-GameDir` | 目标 `.minecraft` 目录 | 脚本旁边的 `.minecraft` |
| `-Source` | `Official` 只用官方源。`BMCLAPI` 镜像优先，失败再试官方。`Auto` 在简体中文系统或 UTC+8 时镜像优先，否则官方优先；除 `Official` 外，单个文件失败都会换源 | `Auto` |
| `-Threads` | 并行下载线程数，范围 1–64 | `8` |
| `-List` | 列出版本后退出，不下载 | （关） |
| `-IncludeSnapshots` | 与 `-List` 一起使用时，连同快照以及旧版 beta / alpha 一并列出 | （关） |

## Java 与启动

程序不安装 Java，也不启动游戏。natives 由启动器在启动时解压。

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
- 程序只下载 Mojang 官方服务器或 BMCLAPI 镜像上的公开文件（版本清单、客户端、支持库、资源等），不提供盗版登录或离线账号。
- Minecraft 是 Mojang Synergies AB 与 Microsoft 的商标。本项目与 Mojang、Microsoft、BMCLAPI 没有隶属或授权关系。
- 登录正版服务器、进行联机，需要用户自己拥有正版账号。
- 软件按「现状」提供，不提供任何明示或暗示的担保。详见 [MIT License](LICENSE)。

## 许可

[MIT License](LICENSE) © 2026 Sirius-75

推送到 GitHub 后，Actions 会用 PSScriptAnalyzer 检查 `mc-download.ps1`，并在 Windows 运行器上编译 `mc-download.exe`、跑自测，再实际下载 1.12.2 做校验。警告不会令 PSScriptAnalyzer 失败；语法错误、Error 级别问题、exe 编译失败或下载自测失败会使检查失败。
