# Steam Stat Core

## 项目概述

Steam Stat 的开源数据层 / 桌面后端仓库。应用 UI（Vue 渲染层）位于独立私有仓库，不在本仓库中；本仓库可独立 `dotnet build` / `dotnet test`。

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
│   └── GenerateIpcContracts/  # 生成 preload.mjs / ipc.d.ts / snapshot
├── third_party/
│   └── Electron.NET/          # submodule（DYH1319/Electron.NET fork）
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
- 生成物三个：`ElectronNet/ElectronNet/Resources/preload.mjs`、`ElectronNet/ElectronNet.Tests/Snapshots/ipc-contracts.snapshot.json`、前端 `ipc.d.ts`
- 前端输出参数化：`--frontend-types <file>` 显式指定；未指定时仅在仓库内存在 `apps/steam-stat` 才写默认路径（纯后端仓自动跳过）；`--core-root <dir>` 覆盖后端输出根（供私有 UI 仓经 submodule 调用）

## 与前端仓库的约定

- 渲染层仓库私有，通过 `vendor/core` submodule 引用本仓库
- 开发模式启动 Vite：`Program.cs` 读取环境变量 `STEAM_STAT_FRONTEND_DIR`（指向前端应用目录，其 package.json 的 dev 脚本即 Vite）；未设置时退回旧约定 `<repoRoot>/apps/steam-stat`
- 发布打包：csproj 的 `FrontendDistDir` 属性控制前端 dist 嵌入路径，默认 `..\..\apps\steam-stat\dist`，CI/私有仓用 `-p:FrontendDistDir=...` 覆盖
- 修改契约/IPC 行为前同步考虑渲染层影响；`--check` 必须在 CI 保持绿色

## 开发规范

- 目标框架 net10.0，Windows-only 代码放 `SteamStat.Platform.Windows`，Linux-only 代码放 `SteamStat.Platform.Linux`；Host 在 `Program.AddPlatformServices` 按运行平台选择 `AddSteamStatWindows` / `AddSteamStatLinux`
- 路径比较：Windows 不区分大小写、Linux 区分；不要硬编码 `\\` 分隔符（`PathHelper.ToTrueCase` 在非 Windows 上原样返回）
- Linux 平台测试：`SteamStat.Platform.Linux.Tests` 用夹具目录模拟 home 与 /proc；`RealSystemSmokeTests`（`[Explicit]`，`--filter Category=RealSystem`）在开发机上读取真实 Steam / 密钥环 / X11
- 发行形态与更新：`Hosting/AppDistribution.cs`（程序目录的 `distribution.json` 或 `APPIMAGE` / `FLATPAK_ID`）决定数据目录（便携版放程序目录 `data/`）与更新方式（auto = electron-updater；notify / managed = 比较 releases.atom）；更新通道 stable / beta 见 `packaging/README.md`
- 反馈与诊断：`Features/Diagnostics`（诊断包、预填 Issue、崩溃报告）；崩溃报告默认关闭，手动提交实现 `ICrashReportSink`（GitHub Issue），自动上报实现 `ICrashReportUploader`（Sentry，DSN 只在发行构建经 `-p:SentryDsn` 注入），所有输出先经 Core 的 `DiagnosticsRedactor` 脱敏
- EF 迁移：避免修改已有列的 `HasComment`，SQLite 会因此重建整表
- 数据导出：`Features/DataTransfer`（CSV / 最小 xlsx 写入器 / 按表原样的完整 JSON）+ `Services/DataExportService`；新增档案表会自动进入完整 JSON，含敏感数据的表要加进 `DatabaseJsonExporter.ExcludedTables`
- 手动备份 / 恢复：`Services/BackupService`（.ssbackup = zip，可选 `SteamStat.Core.Security.ChunkedEncryption` 口令加密）+ `Persistence/PendingRestoreApplier`（启动时、迁移前替换数据库，保留本机凭证与当前设备）。新增含凭证的表要同时加进 `BackupFormat.StrippedTables`；渲染层传来的口令用 `IpcSecret.Decode` 解码
- 多设备同步：Core `Sync/`（`ISyncStorage` + 本地文件夹 / WebDAV / S3、`SyncVault`、`SyncSegment`）；Host `Features/Sync`（`SyncSchema` 表规格与触发器、`SyncEngine` 导出 / 最后写入胜出合并、`SyncConfigStore`）+ `Services/SyncService`。新增需要同步的表要在 `SyncSchema.Tables` 登记（事件类数据加 `sync_uid` 列）；含凭证或设备本地状态的表不要加入。触发器运行时安装，迁移重建表后下次启动会补回
- 公共数据层：生成器在独立公开仓库 `DYH1319/steam-stat-public-data`（本地检出在 `../steam-stat-public-data`），格式 v1 与 Core `PublicData/PublicData.cs` 保持一致；Host `Services/PublicDataService`（镜像、分片校验、缓存 `{UserData}/PublicData`，`STEAM_STAT_PUBLIC_DATA` 可指向本地目录）。成就 schema 网关在 CM 失败且无缓存时经 `IPublicAchievementSchemaSource` 回退。线上数据的兼容性测试：`dotnet test --filter Category=RealNetwork`
- 架构边界由 `SteamStat.Architecture.Tests` 强制（Core 不依赖 Electron、渲染层只经契约通信等），新增跨层引用前先查相关测试
- 提交前运行 `dotnet test`；契约变更必须 `--write` 并一起提交生成物
