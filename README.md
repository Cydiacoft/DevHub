# DevHub

Windows 开发环境与命令行工具管理器。使用 C#、.NET 10、WinUI 3 和原生 Fluent 控件。

## 打开软件

双击项目根目录的 **DevHub 快捷方式**，或 **启动 DevHub.cmd**。发布文件位于 `.devhub/build/publish/DevHub`，整个发布目录需要一起保留。发布时自动生成指向当前项目的快捷方式，并将启动脚本保存为 Windows 的 CRLF 换行格式。

运行时优先使用 `DEVHUB_ROOT` 指定的目录，否则向上查找 `DevHub.root`。把软件单独复制出去时，默认在软件所在目录创建 `.devhub`。

## 页面与操作

- **概览**：统计卡片可点击，查看工具与安装明细、项目环境、诊断问题。
- **工具库**：默认以开发环境卡片显示；开发工具按用途分组；未知 CLI 按来源收起，同一工具的版本合并在详情里。
- **AI 工具**：发现 Agent / AI 客户端安装、版本和配置来源，检查 MCP 启动入口，列出全局及项目的子 Agent、Skills 和规则文件。
- **版本管理**：查询官方下载目录，校验并安装 ZIP；Python 通过 uv，Rust 通过 rustup 管理。安装新版本后，再选择是否切换环境。
- **项目环境**：选择各工具版本、创建或选择 Python 虚拟环境、打开专用终端、写入版本声明、配置 IntelliJ IDEA。
- **环境诊断**：检查 Java 环境变量、入口冲突、pip 与 Python 对应关系、重复 PATH 和失效项目绑定。
- **环境对比**：从诊断页对比当前进程、新终端和项目环境，查看所有已知命令的命中顺序、真实路径及配置覆盖。解析使用无个人配置的 PowerShell，不加载个人函数、别名或启动脚本。
- **缓存与空间**：统计电脑上的常见开发缓存、发现的项目构建缓存和依赖，以及 DevHub 自身缓存。按工具或项目分组，支持目录查看和逐项确认清理。
- **任务与日志**：查看当前运行的任务与输出，支持取消；完整日志留在 `.devhub/logs`。
- **设置**：外观、扫描性能、额外工具目录、项目发现范围、Everything 接口、自定义 CLI 和配置恢复。

## 扫描与索引

普通扫描使用 PATH、Windows 安装记录、常见 SDK 目录和手动添加的目录，不逐盘遍历文件。

快速扫描根据入口及版本文件的路径、长度、修改时间和版本查询参数复用记录；默认有效期 24 小时。完整扫描重新核验版本。默认并发 5 个查询，普通版本命令超时 6 秒；设置中可调整。未知命令只登记位置，添加明确的识别规则后才查询版本。

提供可选的 Everything ES 接口，查询匹配已知工具名称的候选文件，再进行版本及归属识别。Everything 本身需要已安装、正在运行且索引可用；缺少接口或查询失败时自动回退。DevHub 不安装 Everything 服务，不重建它的磁盘索引。

## Agent 与 MCP 检测

支持 Codex CLI、Claude Code、Gemini CLI、OpenCode、Aider、Goose、GitHub Copilot CLI 的已知命令识别，以及 Cursor、VS Code、Claude Desktop 的常见安装位置。CLI 版本沿用工具扫描结果，桌面软件读取程序版本信息；同名进程数只作参考，不表示会话正在工作，也不代表 MCP 已连接。配置残留不会被标记成已安装。

AI 页面检查用户目录、已添加项目、缓存项目发现范围和 DevHub 所在父目录中的项目。解析常见 JSON / JSONC / TOML MCP 配置，保留来源与作用范围，不把重复配置当作多个有效服务。可检查启动入口和明确的绝对工作目录；不会执行配置中的命令、安装 MCP、请求远程服务或尝试登录。环境变量值、命令参数、请求头和提示词正文不进入检测结果；远程地址只显示来源域名。YAML 配置目前仅登记位置。

子 Agent 定义按 `.claude/agents`、`.opencode/agents` 等常见目录和配置中的定义发现，Skills 与规则按文件位置登记。未覆盖动态会话配置、插件内注册项、所有编辑器扩展、组织策略或云端 Agent。自定义 `CODEX_HOME` / XDG 等路径覆盖和客户端最终配置合并暂未完整实现，因此列表表示发现记录，不能替代客户端的实际加载状态。

只读检测报告：`./build.ps1 -Task Agents -NoRestore`，输出 `.devhub/logs/agents-report.json`。

## 缓存范围

电脑缓存发现覆盖 pip、uv、npm、pnpm、Yarn、NuGet、Gradle、Maven、Go、Cargo、Dart/Flutter、Android、Scoop 和部分模型存储位置；同时读取部分已安装工具的当前缓存配置。

项目发现默认从管理器项目所在的父目录、用户 Projects、source/repos、Documents、Desktop 和已添加项目出发，向下检查四层。可在设置中添加其他根目录。发现依据是 package.json、pom.xml、Gradle、Cargo、Go、Flutter、Python、.NET 等项目标识；跳过 SDK、依赖与构建目录，不保证找到未纳入范围的每一个项目。

缓存占用是可访问文件的估算，跳过目录链接。Maven 本地仓库、pnpm 共享存储、Python 虚拟环境、node_modules、Flutter 内置 Dart/引擎、模型快照等会显示占用，但提供所属工具管理提示，不直接当作普通缓存删除。这里只做发现和统计；实际清理需要在界面逐项确认。

可按 C 盘、D 盘等磁盘筛选，同时显示磁盘剩余容量。“系统与应用”分类纳入用户临时文件、系统临时目录、更新下载、浏览器和图形缓存、安装器维护数据，以及已识别的升级残留目录。用户临时目录仅清理超过七天且未被占用的文件；系统升级和安装器缓存通过 Windows 存储设置处理。此功能统计已识别目录，不等同于整盘全部文件的空间分解。

uv 缓存通过原生 `uv cache prune --cache-dir` 管理，清理未使用项并保留锁与缓存结构，不直接删除缓存目录；可回收量在执行后确定。普通文件缓存跳过占用或无法访问的文件，并报告实际删除数量及释放空间估算。

## 环境切换与移除

系统级切换先展示差异，记录环境变量的原值和注册表类型，再调用单独的管理员辅助程序。辅助程序仅接受规定的环境变量，并在写入前检查原值、保存备份、验证结果；失败时回滚。原有终端和 IDE 需要重新打开。

默认修改当前用户；只有主动选择系统范围才请求管理员授权。用户 PATH 无法覆盖系统入口时，会在预览阶段阻止无效切换，提示使用项目终端或明确选择系统范围。实际写入后重新从 Windows 获取用户环境并解析命令，Java 同时验证 java、javac 与 JAVA_HOME；验证不符不会报告成功，可从配置备份恢复。

项目终端通过子进程环境隔离各工具版本。`.NET` 项目保存绑定时同时确认 `global.json`；已有版本声明会在替换前明确确认和备份。IDEA 配置要求关闭 IDEA，支持项目 JDK 和已有 Gradle JVM 配置，模块及 Maven 的独立覆盖仍需在 IDE 中检查。

DevHub 安装的独立 SDK 移除后进入 `.devhub/data/removed-sdks` 待清理区，可在缓存页永久清理。uv、rustup 和系统注册的安装通过对应管理器或原安装器处理。受项目引用或仍作为当前入口的版本需要先解除绑定或切换。

## 项目内数据

所有可配置的构建和 DevHub 运行数据保存在项目的 `.devhub` 下：

- `cache`：依赖下载、安装包、版本元数据、扫描记录与工具缓存。
- `build`：中间输出、验证夹具和发布目录。
- `sdks`：管理器独立安装的 SDK。
- `data`：设置、安装记录、项目绑定及混合保存配置和缓存的工具目录。
- `logs`、`backups`：命令输出与配置恢复记录。

通过 DevHub 运行的工具会注入相应的缓存配置；这不会自动迁移或改写电脑原有缓存。缓存页单独读取电脑上的现有位置。现有 Rust 工具链沿用 rustup，管理器内 Cargo 的工作数据保存在项目目录。

## 构建与验证

构建需要 Windows、PowerShell 7、.NET SDK 10.0.200 或兼容的 10.0 SDK，以及 Windows SDK / WinUI 构建工具。首次依赖还原需要访问 NuGet。运行发布版使用系统自带 Windows PowerShell，不需要另外安装 PowerShell 7 或 .NET SDK。

```powershell
.\build.ps1 -Task Build
.\build.ps1 -Task Test
.\build.ps1 -Task Publish
.\build.ps1 -Task Package  # 生成干净的 ZIP、更新描述和 SHA-256
```

其他只读检查：

```powershell
.\build.ps1 -Task Scan       # 实际工具发现报告
.\build.ps1 -Task Caches     # 电脑与项目缓存报告，不清理文件
.\build.ps1 -Task Providers  # 联网核对官方下载目录，不安装 SDK
.\build.ps1 -Task Doctor     # 当前进程 / 新终端环境证据，不修改系统
```

测试使用独立夹具和内存环境变量存储，覆盖回滚、并发修改检查、项目隔离、解释器绑定、命令参数、校验失败、安装布局、ZIP 路径边界、缓存清理边界及 IDEA 备份恢复。系统级环境切换和真实卸载未在当前电脑上执行。

Everything 加速接口可用下面的脚本准备；下载到项目目录并验证固定版本 SHA-256，不安装后台服务：

```powershell
.\scripts\prepare-integrations.ps1
.\build.ps1 -Task Publish
```

## 扩展工具

内置规则位于 `src/DevHub.Core/Catalog.json`，使用版本化 JSON 描述名称、命令入口、查询参数及 WinGet/Scoop/Chocolatey 包标识。复杂安装、环境和项目行为由专用服务处理。也可在界面中添加自定义工具规则。

程序不提供项目代码的 Git 历史管理；Git 本身作为 CLI 工具纳入发现与安装维护。

## 启动恢复与程序更新

首次窗口显示不等待工具扫描或联网。损坏的 JSON 配置先复制到 `.devhub/backups/corrupt-*.json`，再使用默认配置；原始内容保留在备份中。启动脚本检查程序的就绪记录；启动失败会显示错误，日志在 `.devhub/logs/app-errors.log`。发布包内的 `Start-DevHub.cmd` 使用系统自带的 Windows PowerShell，支持中文与空格路径；也可直接打开 `DevHub.App.exe`。

设置页显示当前版本、自动检查开关、检查结果、发布说明和“下载并重启更新”。每天最多后台检查一次，只查询 `Cydiacoft/DevHub` 的公开稳定版；没有 Release、断网或 GitHub 限流不影响软件启动。首次提供更新能力的版本为 1.1.0，之后需要发布更高版本才会出现更新提示。

下载位于 `.devhub/cache/updates`，包必须通过发布描述、大小、SHA-256、文件清单和路径校验。独立更新程序运行在 `.devhub/data/app-updates`，等待原窗口退出后只替换清单内程序文件；SDK、项目配置、虚拟环境、源码和未管理的用户文件不在替换范围。旧程序保存在 `.devhub/backups/app-updates`；新版启动失败自动恢复旧版，更新中断后可从设置页恢复。若主程序本身无法启动，`Start-DevHub.cmd` 和项目快捷方式会先提供独立恢复入口。所有更新均由用户确认，不静默安装，不自动提权。

GitHub Actions 在提交和 PR 时构建、测试并生成完整 ZIP；标签必须与 `Directory.Build.props` 中的版本一致。`v1.1.0` 这类标签通过检查后创建 GitHub Release **草稿**，公开发布前仍需按 `docs/VALIDATION.md` 在干净 Windows 环境验收。ZIP、`.update.json` 和 `.sha256` 必须一起发布。普通 CI 机器已安装开发运行时，不能替代干净系统验收。

本版未提供安装器或代码签名。SHA-256 用于发布包完整性检查，更新来源固定为上述仓库的 HTTPS Release。
