# 架构说明

本文描述 Steam Stat 在 Phase 1 完成后的实际架构、边界与工程约束。代码与本文不一致时，应在同一个实现 PR 中修正代码、架构测试和本文。

---

## 1. 运行时进程

Steam Stat 是 DotNet-First 的 Electron.NET 桌面应用：

```text
.NET Host（ElectronNet/ElectronNet）
  ├─ 启动 Electron runtime
  ├─ Electron ready 后取得 userData / locale
  ├─ 构建 Generic Host 与最终 Serilog logger
  ├─ 执行数据库迁移、数据初始化、设置加载
  ├─ 创建 BrowserWindow、托盘并注册 IPC
  └─ 停止时取消并等待 hosted services，再执行一次 cleanup
             │
             │ generated preload + typed IPC
             ▼
Electron renderer（Vue 3 + Vite，src/）
```

开发模式由 .NET 进程自动启动 Vite；生产模式加载打包后的 `dist/index.html`。`ApplicationStartupCoordinator` 固定迁移、初始化、窗口和 IPC 的顺序，`ApplicationCleanupService` 用幂等门保证 cleanup 只执行一次。

---

## 2. 项目与依赖方向

根目录 `SteamStat.slnx` 是唯一主 solution，包含全部一方 .NET 产品、测试和工具项目。

```text
ElectronNet/ElectronNet (Host)
  ├─ SteamStat.Core
  ├─ SteamStat.Platform.Windows ──> SteamStat.Core
  ├─ SteamStat.Contracts
  └─ ElectronNET.API

SteamStat.Core                 不引用 Host、Electron 或 Windows 实现
SteamStat.Contracts            不引用 Core、EF Core、SteamKit2 或 Electron
tools/GenerateIpcContracts     只引用 SteamStat.Contracts
```

物理目录：

```text
SteamStat.slnx
backend/
  src/
    SteamStat.Contracts/       IPC descriptor、request/response/event DTO
    SteamStat.Core/            业务用例、Steam session、事件和平台抽象
    SteamStat.Platform.Windows/注册表、进程控制、DPAPI
  tests/
    SteamStat.Core.Tests/      纯单元测试，不启动 Electron
    SteamStat.Architecture.Tests/依赖与安全边界门禁
ElectronNet/
  ElectronNet/                 Electron Host、EF adapter、IPC、后台任务
  ElectronNet.Tests/           Host adapter 与兼容性测试
tools/GenerateIpcContracts/    preload、TypeScript 声明和 snapshot 生成器
src/                           Vue renderer
third_party/Electron.NET/      固定版本 submodule
```

依赖规则由 `SteamStat.Architecture.Tests` 强制执行：Core 不可引用 Electron/Host/Windows 实现，不可使用 Console 或 Serilog static logger；Contracts 必须保持独立；Electron API 只允许 Host 使用；Feature 不可依赖其他 Feature 的 Internal/Persistence 实现；业务 UI 推送只能经过统一 forwarder。

---

## 3. Composition Root 与依赖注入

唯一 composition root 位于 `Program.Main` 和 `AddSteamStatCore`、`AddSteamStatWindows`、`AddSteamStatElectron` 三个注册入口。

- `AddSteamStatCore`：注册 `TimeProvider`、设置服务和 `IHttpClientFactory` named clients。
- `AddSteamStatWindows`：注册 `ISecretStore`、`ISteamInstallLocator`、`IProcessController` 的 Windows 实现。
- `AddSteamStatElectron`：注册 EF factory、Host adapter、事件转发、IPC registrar、后台任务和 Electron 服务。
- Service provider 始终启用 `ValidateOnBuild` 与 `ValidateScopes`。
- 不允许 service locator、业务全局 `IServiceProvider` 或用 static facade 转发到 DI。
- 持有 session、cache、subscription、timer 或并发状态的服务必须是 DI 管理的实例并有释放路径。

`AppEnvironment` 与 `IAppPaths` 在 Electron ready 后创建，随后保持不可变。数据库、设置、日志和临时目录均从 Electron `userData` 派生，不从安装目录派生。

---

## 4. Core、平台与 Steam 会话

`SteamStat.Core` 包含：

- `Features/Login`：登录、重连、callback loop、token 摘要和 session 生命周期。
- `Features/Friends`：好友缓存、callback 订阅、富文本状态解析和事件发布。
- `Features/Library`：游戏库同步、不可变缓存快照和 metadata 端口。
- `Settings`：默认值、合并、原子 JSON 写入和副作用协调。
- `Events`：`IEventBus` / `IEventHandler<T>` 及 Core 事件。
- `PlatformAbstractions`：秘密存储、Steam 安装发现和进程控制的窄接口。

Login 不直接调用 Friends；`SteamSessionReady` / `SteamSessionEnded` 负责生命周期通知。Core service 使用实例状态、`TimeProvider`、可取消任务和显式 `Dispose`/`DisposeAsync`，不暴露 `CancellationTokenSource`。

`SteamStat.Platform.Windows` 只实现 Core 平台抽象：

- `DpapiSecretStore`：DPAPI 凭据保护。
- `SteamInstallLocator`：Steam 注册表与本地安装信息。
- `WindowsProcessController`：进程和 Windows service 控制。

---

## 5. 数据库与持久化

Host 使用 `IDbContextFactory<AppDbContext>`；每个工作单元创建并释放短生命周期 Context，不存在 `AppDbContext.Instance/Create`。

六个实体配置位于 `Features/*/Persistence`，由 `ApplyConfigurationsFromAssembly` 自动扫描。`DatabaseMigrator` 在启动 worker 前：

1. 查询 pending migrations；
2. 在数据库同目录创建 SQLite 临时备份；
3. 原子替换 `steam-stat.bak`；
4. 执行 migration；
5. 失败时不继续启动写数据的任务。

`AppDbContextDesignTimeFactory` 支持独立 EF CLI 操作，不连接真实用户数据库。既有 migration history、schema 和 UserData 下数据库路径保持兼容。

---

## 6. IPC 与 renderer 安全边界

`SteamStat.Contracts.Ipc.IpcCatalog` 是 channel、JS API method、方向和 wire DTO 的唯一来源。生成器以稳定顺序生成：

- `ElectronNet/ElectronNet/Resources/preload.mjs`
- `src/types/ipc.d.ts`
- `ipc-contracts.snapshot.json`

`IpcMainService` 只引用 descriptor，不手写 channel。`IpcRequestBinder` 在 Host 边界执行 camelCase binding、未知字段拒绝、必填/长度/范围/string union/集合上限校验；Core 不接收 IPC `object`、`dynamic` 或 `Dictionary<string, object>`。

Host-to-renderer 通知先发布 Core/Host typed event，再由 `ElectronIpcEventForwarder` 唯一调用 `Electron.IpcMain.Send`。Core event 与 IPC DTO 分离，事件和日志禁止携带凭据。

Renderer 配置固定为：

- `NodeIntegration`、worker/subframe Node integration 关闭；
- `ContextIsolation`、`WebSecurity`、sandbox 开启；
- insecure content 关闭；
- preload 只暴露生成的最小 API；
- `shell:openExternal` 仅接受无 user-info 的 HTTP(S) URL；
- `shell:openPath` 在 Host 重新规范化，并限制为已知 Steam 目录。

---

## 7. 日志

产品代码只通过 `Microsoft.Extensions.Logging.ILogger<T>` 记录日志。Serilog 只在 Electron Host composition root 配置：

- bootstrap logger 在 Electron ready 和 UserData 可用前输出启动故障；
- final logger 写入 `<UserData>/Logs/steam-stat-.log`；
- 按天及 10 MiB 大小 rolling，最多保留 14 个文件；
- Debug 开发模式同时输出 console；
- scope 和 message-template properties 保留结构化字段，异常保留 stack trace；
- Host 释放 logger 时完成 flush。

禁止 `Console.WriteLine`、Serilog static `Log.*`，也禁止记录 password、access/refresh token、guard data、Authorization header 或 QR secret。日志模板应使用稳定字段，如 `SourceContext`、`Feature`、`Operation`、`CorrelationId`、`AppId` 和必要时经过处理的账号标识。

---

## 8. 后台任务与关闭

`UpdateService` 与 `UpdateAppRunningStatusJob` 都是 DI singleton，并以同一实例注册为 `IHostedService`：

- 继承 `BackgroundService`；
- 使用 `PeriodicTimer` 和注入的 `TimeProvider`；
- 设置变化会取消当前 schedule 并按新状态/间隔重建；
- Electron updater callback 在停止时解除注册；
- 手动检查、下载和事件发布任务被显式跟踪；
- `StopAsync` 取消循环并等待已跟踪工作。

Login/Friends 的 callback/session/cache 也各自由对应实例管理。Host 停止后台服务后再执行应用 cleanup；窗口关闭、托盘退出和 Electron 主进程退出均走同一关闭路径。

---

## 9. 数据来源与 HTTP

| 来源 | 用途 | 边界 |
| --- | --- | --- |
| VDF / ACF | 登录用户、库目录、已安装应用 | Host `LocalFileService` |
| Windows 注册表 | Steam 路径、当前用户、运行状态 | `ISteamInstallLocator` |
| 进程 / service | Steam 启停与切换用户 | `IProcessController` |
| SteamKit2 CM | 登录、好友、库、富文本状态 | Core Steam session/features |
| Steam Web / Store HTTP | 头像、应用 metadata 兜底 | named `IHttpClientFactory` clients |

HTTP client 统一由 `IHttpClientFactory` 创建。`Download` 与 `SteamApi` client 使用 5 分钟连接池生命周期、自动解压和分别为 30/15 秒的超时。能通过 SteamKit2 CM 获取的数据不应无必要改走受限的 Web API。

---

## 10. 测试、CI 与交付

- `SteamStat.Core.Tests`：不联网、不要求 Steam、不初始化 submodule/Electron runtime。
- `ElectronNet.Tests`：数据库、Host adapter、IPC compatibility、后台服务和安全策略。
- `SteamStat.Architecture.Tests`：每个 PR 强制执行依赖、日志、静态状态、IPC 和生成边界。
- `GenerateIpcContracts --check`：Windows 与 Ubuntu 使用同一无写入检查。
- 根构建启用 NuGet audit，`NU1903`/`NU1904` 作为 error，禁止用 `NoWarn` 绕过。
- 前端 CI 保留 `pnpm run lint:ci` 与 `pnpm run build`。

本地与 CI 命令见根 `CONTRIBUTING.md`；桌面验证步骤见 `docs/dev/smoke-checklist.md`。

---

## 11. 实验性功能

Steam 登录、好友和游戏库等尚未稳定的页面由 `meta.experimental` 控制。`src/main.ts` 必须在安装 router 前加载设置并写入实验性开关，路由守卫随后过滤动态路由。新增实验性页面需同步更新路由、两种语言文案、typed IPC 契约与测试。
