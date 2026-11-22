# Steam Stat Core

## 项目概述

Steam Stat 的**后端仓**（开源数据层：.NET Host + Core + IPC 契约）。应用 UI（Vue 渲染层）在私有的 **UI 仓**，不在本仓库中；本仓库可独立 `dotnet build` / `dotnet test`。

## 相关仓库

Steam Stat 共五个仓库。文档、提交信息中统一使用下表的称呼（不再说「前端仓 / 私有仓 / 公开仓 / 渲染层仓库」等）：

| 称呼 | 仓库 | 说明 |
|---|---|---|
| **后端仓** | [`DYH1319/steam-stat`](https://github.com/DYH1319/steam-stat) | .NET Host + Core + IPC 契约；Releases 是下载入口 |
| **UI 仓** | 私有 | Vue 渲染层与发行构建，以 submodule 引用后端仓 |
| **Electron.NET fork 仓** | [`DYH1319/Electron.NET`](https://github.com/DYH1319/Electron.NET) | 自维护的 Electron.NET fork，后端仓 submodule `third_party/Electron.NET` |
| **公共数据仓** | [`DYH1319/steam-stat-public-data`](https://github.com/DYH1319/steam-stat-public-data) | 游戏元数据与成就 schema，Host 运行时下载 |
| **插件仓** | [`DYH1319/steam-stat-plugins`](https://github.com/DYH1319/steam-stat-plugins) | 存档分析插件，Host 运行时安装 |

依赖方向：UI 仓 →（submodule `vendor/core`）后端仓 →（submodule `third_party/Electron.NET`）Electron.NET fork 仓。公共数据仓、插件仓与本仓库没有 git 关系，只通过数据格式 / 契约耦合，改动时两边同步并保持对已发布客户端的兼容。

## 技术栈

- **运行时**: .NET 10 + Electron.NET（自维护 fork，见 `third_party/Electron.NET` submodule）
- **数据**: SQLite + EF Core
- **Steam 接入**: SteamKit2（CM 会话）、ValveKeyValue（VDF 解析）
- **渲染层契约**: `tools/GenerateIpcContracts` 生成 preload.mjs + TypeScript 类型 + snapshot

## 目录结构

```
├── ElectronNet/
│   ├── ElectronNet/          # Electron Host：窗口/托盘/IPC/启动协调/EF Core
│   └── ElectronNet.Tests/
├── backend/
│   ├── src/
│   │   ├── SteamStat.Cli/            # 命令行工具 steamstat（本地接口的客户端）
│   │   ├── SteamStat.Contracts/      # IPC 契约与 DTO（IpcCatalog 在此注册端点）
│   │   ├── SteamStat.Core/           # 纯数据层：Steam 网关、会话、统计、无 Electron 依赖
│   │   ├── SteamStat.Platform.Windows/  # Windows 平台实现（注册表/进程控制/DPAPI/桌面活动）
│   │   └── SteamStat.Platform.Linux/    # Linux / Steam Deck 实现（registry.vdf + /proc/libsecret/X11）
│   └── tests/
│       ├── SteamStat.Core.Tests/
│       ├── SteamStat.Platform.Linux.Tests/ # 夹具化的 home 与 /proc，不依赖本机 Steam
│       └── SteamStat.Architecture.Tests/   # 架构边界守卫测试
├── packaging/                 # winget / Scoop / Flatpak / AUR 清单模板 + render-manifests.mjs
├── tools/
│   ├── GenerateIpcContracts/  # 生成 preload.mjs / ipc.d.ts / snapshot
│   └── GenerateFakeArchive/   # 生成假档案（演示、压测、视觉回归）
├── third_party/
│   └── Electron.NET/          # submodule → Electron.NET fork 仓
└── SteamStat.slnx             # 解决方案清单
```

## 常用命令

```bash
dotnet restore SteamStat.slnx -p:ElectronSkipExecCommands=true   # 还原（跳过 Electron 打包命令）
dotnet build  SteamStat.slnx -p:ElectronSkipExecCommands=true    # 构建
dotnet test   SteamStat.slnx                                     # 全量测试
dotnet run --project tools/GenerateIpcContracts -- --check       # 校验 IPC 生成物是否过期
dotnet run --project tools/GenerateIpcContracts -- --write       # 重新生成
```

## IPC 契约

- 端点在 `backend/src/SteamStat.Contracts/IpcCatalog.cs`（或同名 catalog）注册；DTO 必须属于 `SteamStat.Contracts` 命名空间
- `IpcStringValues` 可以加在字符串集合上：生成 `('a' | 'b')[]`，`IpcRequestBinder` 逐项校验；新增端点后同步改 `IpcContractTests` 里的数量
- 生成物三个：`ElectronNet/ElectronNet/Resources/preload.mjs`、`ElectronNet/ElectronNet.Tests/Snapshots/ipc-contracts.snapshot.json`、前端 `ipc.d.ts`
- 前端输出参数化：`--frontend-types <file>` 显式指定；未指定时仅在仓库内存在 `apps/steam-stat` 才写默认路径（单独检出的后端仓自动跳过）；`--core-root <dir>` 覆盖后端输出根（供 UI 仓经 submodule 调用）

## 与 UI 仓的约定

- UI 仓（私有）通过 `vendor/core` submodule 引用本仓库；后端改动在本仓库提交推送后，再到 UI 仓更新指针
- Electron.NET 改动先在 Electron.NET fork 仓提交推送 main，再在本仓库 `git add third_party/Electron.NET` 提交
- 开发模式启动 Vite：`Program.cs` 读取环境变量 `STEAM_STAT_FRONTEND_DIR`（指向前端应用目录，其 package.json 的 dev 脚本即 Vite）；未设置时退回旧约定 `<repoRoot>/apps/steam-stat`
- 发布打包：csproj 的 `FrontendDistDir` 属性控制前端 dist 嵌入路径，默认 `..\..\apps\steam-stat\dist`，UI 仓的打包脚本 / CI 用 `-p:FrontendDistDir=...` 覆盖
- 修改契约/IPC 行为前同步考虑渲染层影响；`--check` 必须在 CI 保持绿色

## 开发规范

- 目标框架 net10.0，Windows-only 代码放 `SteamStat.Platform.Windows`，Linux-only 代码放 `SteamStat.Platform.Linux`；Host 在 `Program.AddPlatformServices` 按运行平台选择 `AddSteamStatWindows` / `AddSteamStatLinux`
- 路径比较：Windows 不区分大小写、Linux 区分；不要硬编码 `\\` 分隔符（`PathHelper.ToTrueCase` 在非 Windows 上原样返回）
- Linux 平台测试：`SteamStat.Platform.Linux.Tests` 用夹具目录模拟 home 与 /proc；`RealSystemSmokeTests`（`[Explicit]`，`--filter Category=RealSystem`）在开发机上读取真实 Steam / 密钥环 / X11
- 发行形态与更新：`Hosting/AppDistribution.cs`（程序目录的 `distribution.json` 或 `APPIMAGE` / `FLATPAK_ID`）决定数据目录（便携版放程序目录 `data/`）与更新方式（auto = electron-updater；notify / managed = 比较 releases.atom）；更新通道 stable / beta 见 `packaging/README.md`
- 窗口：`Hosting/WindowState.cs`（`{UserData}/window-state.json`，只属于本机，不进设置和同步）；独立窗口 `Hosting/PopoutWindowService.cs`，路由白名单 `PopoutRoutes`。窗口材质 `Hosting/WindowMaterials.cs`，低于 Windows 11 22H2 时按 none 处理。发给渲染层的事件只能从 `ElectronIpcEventForwarder` 发出（有架构测试），它同时发给独立窗口。Electron.NET 的 IPC 不带发送方窗口，需要定位窗口的接口要显式传 id
- 反馈与诊断：`Features/Diagnostics`（诊断包、预填 Issue、崩溃报告）；崩溃报告默认关闭，手动提交实现 `ICrashReportSink`（GitHub Issue），自动上报实现 `ICrashReportUploader`（Sentry，DSN 只在发行构建经 `-p:SentryDsn` 注入），所有输出先经 Core 的 `DiagnosticsRedactor` 脱敏
- IPC 失败：`Infrastructure/IpcFailurePolicy` 包住所有 invoke 处理器，异常分类成 `IpcFailureCodes` 后拒绝这次调用，异常文本不发给渲染层。只有取消和外部服务不可用（网络、Steam、同步存储）不进崩溃报告，其余一律按缺陷上报；同一接口的同类异常每个进程只上报一次，否则轮询持续失败会占满 `MaxRecordsPerProcess`。这些异常被接住后不再是未观察异常，新增外部服务异常类型时加进 `IsExternal`。`On`（send）类接口没有回复，异常仍走未观察异常
- EF 迁移：避免修改已有列的 `HasComment`，SQLite 会因此重建整表
- 数据导出：`Features/DataTransfer`（CSV / 最小 xlsx 写入器 / 按表原样的完整 JSON）+ `Services/DataExportService`；新增档案表会自动进入完整 JSON，含敏感数据的表要加进 `DatabaseJsonExporter.ExcludedTables`
- 手动备份 / 恢复：`Services/BackupService`（.ssbackup = zip，可选 `SteamStat.Core.Security.ChunkedEncryption` 口令加密）+ `Persistence/PendingRestoreApplier`（启动时、迁移前替换数据库，保留本机凭证与当前设备）。新增含凭证的表要同时加进 `BackupFormat.StrippedTables`；渲染层传来的口令用 `IpcSecret.Decode` 解码
- 多设备同步：Core `Sync/`（`ISyncStorage` + 本地文件夹 / WebDAV / S3、`SyncVault`、`SyncSegment`）；Host `Features/Sync`（`SyncSchema` 表规格与触发器、`SyncEngine` 导出 / 最后写入胜出合并、`SyncConfigStore`）+ `Services/SyncService`。新增需要同步的表要在 `SyncSchema.Tables` 登记（事件类数据加 `sync_uid` 列）；含凭证或设备本地状态的表不要加入。触发器运行时安装，迁移重建表后下次启动会补回
- 语义视图层：`Features/Semantic`（`SemanticSchema` 视图与配置表、`SemanticQueryGateway` 查询入口、`SemanticCatalog` / `MetricCatalog` 字典、`OpenArchiveExporter` 开放格式导出）+ `Services/SemanticLayerService`。视图运行时幂等安装，不放进 EF 迁移（EF 重建表时视图会让 RENAME 失败，`DatabaseMigrator` 迁移前先删）。视图列是公开契约：新增列要在 `SemanticCatalog` 写说明（有测试），删改已有列要加 `ContractVersion`。外部查询一律经网关，它靠词法扫描 + authorizer 两层隔离，不要只依赖 authorizer（「最内层视图」可以用 CTE 伪造）。不要把登录名、文件路径放进视图列
- Rich Presence：`Features/Friends/RichPresenceTracker`（纯状态机）+ `RichPresenceCollector`（每账号串行管线）。比较的是原始键值不是文案；推送和对账轮询走同一条路；原始键值先落库（`rich_presence_record`），文案之后再补。推送里的空值不算清空，清空要连续两次空的对账回复
- 数据可靠性：`Services/ArchiveHealthService`（规则在 `Features/Reliability/ArchiveHealthRules`）、`Services/StatsExplainService`、`Services/ArchiveSealService`（`{UserData}/Sealed`，已封存的年份绝不覆盖）、`Persistence/DatabaseSnapshotter`（新快照通过完整性检查才淘汰旧的）
- 新档案：解析和算法在 Core `Archives`（纯函数），采集在 `Services/Collectors`（`IArchiveCollector` + `ArchiveCollectionService` 串行调度，状态在 `collector_state`），读取在 `Services/ArchiveQueryService`。CM 和 HTTP 调用经 `ISteamArchiveGateway`。新增网络调用要接住 `Polly.ExecutionRejectedException`（熔断打开时抛的，不在 `HttpRequestException` 之下），否则断网时变成 internal 错误
- 多账号总账：「我」在设置 `accounts`（`mine` 是 SteamID64，`confirmed` 为假时 `AccountSettings.MineOf` 返回空，Host 不使用未确认的建议；恢复默认设置保留它）。周刊没单独选账号时覆盖「我」。成就合并在 Core `Features/Achievements/AchievementLedger`（纯函数，只在每个账号已判断好的档案口径上合并：并集、最早时间、DLC 任一账号拥有就计入、`Synthetic` 表示合并后才完成），读取在 `AchievementArchiveService.GetLedger`（先按 appId 分页再读这一页的集和解锁）。只有一个账号时结果必须等于该账号自己的口径
- 网页接入：只有 `SteamWebAllowlist` 列出的地址能带登录 Cookie，`SteamWebFetcher` 用的 `SteamWeb` 客户端不开 CookieContainer、不自动跳转，跳转手动处理并重新过白名单。原始页面经 `SteamWebRedactor` 抹掉令牌后存到 `{UserData}/WebArchive`。改解析逻辑要加 `ParserVersion`
- 回忆录：算法在 Core `Memoir`（纯函数：`AppPlaytimeTimeline` 累计时长推算、`TimeMachine`、`LifeChapters` 最优分段、`Prehistory`、`StaticSiteBuilder`、`YearReviewParser`），读档案经 `MemoirArchiveLoader`（只走语义视图）+ `IMemoirCatalog`（Host `HostMemoirCatalog`，隐藏游戏在这里剔除）。证据分级 recorded / official / inferred / unknown，合并取较弱的，未知只给上限不编数字。Host：`MemoirService`（读一次缓存两分钟，人生事件变化时 `Invalidate`）、`MemoirJournalService`（人生事件、时光胶囊、听 `UsageSessionStarted` 出「上回说到」，胶囊正文 `ready_at` 为空时绝不出现在 DTO 里）、`StaticSiteService`。采集 `yearInReview`（`SaleFeature.GetUserYearInReview`，自定义协议类全部 `IExtensible`，原始字节存 `year_in_review.payload`）和 `cloudTrace`（`remotecache.vdf` 写入时间，只追加，不存路径）。`life_event`、`time_capsule` 进同步；新视图 `v_life_event`、`v_cloud_save_trace`。渲染层传来的用户文字是 `s1:` 编码，用 `MemoirJournalService.UserText` 解码
- 好友与圈子：算法在 Core `Circle`（纯函数：`TogetherDetector` + `PartyEvidence` 一起玩严格判定、`Friendship` 友情时间线、`Compatibility` / `PresenceForecast` 契合度和在线概率、`PartyPlanner` 开黑、`Adoption` 入坑传播、`Leaderboard` 排行、`ExchangeCardCodec` / `ExchangeCardBuilder` 交换卡、`DuoReview`）。大厅和服务器由 `PersonaStateRichPresenceHandler` 解析 `CMsgClientPersonaState` 后发 `PersonaGroupCallback`，`PresenceGroupCollector` 只记账号本人和手动追踪的好友、值变化才写、键是哈希（`PresenceGroupKeys`）；只有带游戏信息的推送才处理，其余推送里的 0 不算离开。`SessionTagService` 写 `together_record` 和会话标签位 32（确认组队），判定规则改了加 `TogetherVersion` 会重算全部历史，补算时保留旧局已有的开黑标签。Host 读取在 `CircleService`（缓存两分钟）和 `ExchangeCardService`；`FriendObservationService` 记应用能看到好友状态的时间段（在线概率的分母）；采集项 `friendSince`（Web API `friend_since`，对全部好友）和 `friendPlaytime`（顺带写 `friend_library_item`、`friend_achievement_progress`）。交换卡导入的是不可信输入，存和读都过 `ExchangeCardCodec.Sanitize`；`exchange_card`、`together_record` 和好友表一样不进同步。新视图 `v_together`、`v_friend_since`、`v_friend_library`、`v_friend_achievement`
- AI、MCP 与本地接口（默认全部关闭）：Core `Assistant`（`IChatModel` + `AnthropicChatModel` / `OpenAiChatModel`、`AgentLoop` 工具循环、`Placeholders` 名字代号、`ModelOutput`、`McpProtocol` JSON-RPC、`PrometheusText`），分级 `ExternalScopes`（aggregate 恒含，主播模式去掉 friends）在 `Settings.cs`。Host：`Features/Assistant/ArchiveTools.cs` 是 AI、MCP、REST、CLI、隐藏终端唯一的数据路径，只经 `SemanticQueryGateway`，工具声明所需分级，没授权就不出现也不执行，新增外部可见的数据只能加工具，不要另开出口；`AuditLog`（`{UserData}/Audit`，按月，保留 12 个月，不进备份、同步和诊断包），每个发给模型的请求体和本地接口 / MCP 的响应都要记；`AssistantKeyStore`（经 `ISecretStore`，Key 不进设置）；`CommandRunner`（CLI 与终端共用）；`Services/AssistantService`（问答、四种写作，状态码是固定字符串，渲染层有对应文案，新增要同步）。周刊社论经 `WeeklyService.SetEditorialAsync` 只写一次。`Features/LocalApi`：只监听 127.0.0.1，校验 Host / Origin，token 在 `{UserData}/local-api.json`（非十六进制前缀，只有 GET 可以用 query），`/metrics`、`/overlay` 不记审计。命令行 `backend/src/SteamStat.Cli`（`steamstat`，只是本地接口的客户端，不读数据库；`mcp` 子命令做 stdio 转发），trimmed 发布到 Host 同目录，产品代码里只有它可以写控制台（架构测试有例外）
- Nintendo 游玩记录（实验功能，默认关闭）：设置 `nintendo`，`NintendoSettings.IsActive`（`enabled` 和 `riskAcknowledged` 都为真）为假时不发任何请求。Core `Nintendo`：`NintendoEndpoints`、`NintendoClient`（OAuth PKCE、令牌换取、Play Activity）、`PlayHistory` 解析、`NintendoPlaytime`（日级合并，`NintendoPlaytimeInference` 由累计值平台期推算「大约几点在玩」，窗口超过 4 小时或装不下就算放不上）。推算只用于时段分布，任何合计都以官方日级和累计值为准。Host：`Services/NintendoService`（绑定、读取、数据集、周刊一节；定时读取是它自己的计时器，`ApplicationStartupCoordinator` 调 `StartSchedule`）、`Features/Nintendo`（`NintendoArchiveWriter` 写库、`NintendoLoginWindow` 在独立的内存会话分区打开登录页，用 Electron.NET fork 的 `WebContents.InterceptNavigation` 截获 `npf…://auth`，不交给操作系统）。表：`nintendo_account`、`nintendo_title`、`nintendo_title_total`、`nintendo_playtime_snapshot`、`nintendo_daily_playtime` 进同步（账号不带本机读取状态）；`nintendo_credential`（session token 经 `ISecretStore` 加密，不能安全保存时只留在内存里）不同步、在 `BackupFormat.StrippedTables` 和 `DatabaseJsonExporter.ExcludedTables` 里，诊断经 `DiagnosticsRedactor` 脱敏。`played_day` 是任天堂那边的日期（距 1970-01-01 的天数），不跟随逻辑日分界点。隐藏游戏是 `nintendo.hiddenTitleIds`，周刊（`INintendoWeeklySource`）也按它过滤；「我」是 `mine` 的账号，一个都没标时算全部。真实账号冒烟测试 `NintendoRealAccountSmokeTests`（`[Explicit]`，`--filter Category=RealSystem`，需要环境变量 `STEAM_STAT_NINTENDO_SESSION_TOKEN`）
- 假档案：`Features/DemoArchive/FakeArchiveGenerator` + `tools/GenerateFakeArchive`，演示、压测、视觉回归共用，游戏和人名全部虚构（包括任天堂账号和游戏，标题 ID 以 `0100fa5e0000` 开头）。十年压测 `dotnet test --filter Category=Stress`
- 开发模式专用的环境变量：`STEAM_STAT_USER_DATA` 指定数据目录，`STEAM_STAT_E2E_DIALOG_PATH` 代替备份的系统文件对话框
- 公共数据层：生成器在**公共数据仓**，格式 v1 与 Core `PublicData/PublicData.cs` 保持一致，在线人数历史（`history` 分支，只追加）与 `PublicData/PlayerHistory.cs` 保持一致；Host `Services/PublicDataService`（镜像、分片校验、缓存 `{UserData}/PublicData`，`STEAM_STAT_PUBLIC_DATA` 可指向本地目录）。成就 schema 网关在 CM 失败且无缓存时经 `IPublicAchievementSchemaSource` 回退。线上数据的兼容性测试：`dotnet test --filter Category=RealNetwork`
- 架构边界由 `SteamStat.Architecture.Tests` 强制（Core 不依赖 Electron、渲染层只经契约通信等），新增跨层引用前先查相关测试
- 提交前运行 `dotnet test`；契约变更必须 `--write` 并一起提交生成物
