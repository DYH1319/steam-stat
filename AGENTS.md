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
│   │   └── SteamStat.Platform.Windows/  # Windows 平台实现（进程探测/安装定位/DPAPI）
│   └── tests/
│       ├── SteamStat.Core.Tests/
│       └── SteamStat.Architecture.Tests/   # 架构边界守卫测试
├── tools/
│   └── GenerateIpcContracts/  # 生成 preload.mjs / ipc.d.ts / snapshot
├── third_party/
│   └── Electron.NET/          # submodule（DYH1319/Electron.NET fork）
├── resources/                 # 图标等资源
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

- 目标框架 net10.0，Windows-only 代码放 `SteamStat.Platform.Windows`（`PLATFORM_WINDOWS` 常量）
- EF 迁移：避免修改已有列的 `HasComment`，SQLite 会因此重建整表
- 架构边界由 `SteamStat.Architecture.Tests` 强制（Core 不依赖 Electron、渲染层只经契约通信等），新增跨层引用前先查相关测试
- 提交前运行 `dotnet test`；契约变更必须 `--write` 并一起提交生成物
