# Steam Stat Phase 1：Core 剥离与地基实施指南

> 状态：实施指导稿
> 基线分支：`develop`
> 基线日期：2026-08-28
> 上位规划：[plan-260823.md](./plan-260823.md)
> 相关文档：[架构说明](../ARCHITECTURE.md)、[贡献指南](../../CONTRIBUTING.md)

---

## 0. 文档目的

Phase 1 的目标不是把现有文件机械地移动到新目录，也不是一次性“重写后端”，而是建立一组以后很难被无意破坏的边界：

1. **业务能力可以在没有 Electron、没有真实 Steam、没有网络的情况下构建和测试。**
2. **Electron 只负责进程、窗口、托盘、更新和 IPC 适配，不再进入业务实现。**
3. **对象的创建、生命周期和释放由 Generic Host + DI 统一管理。**
4. **业务事件、持久化、日志和 IPC 契约都有唯一、明确的入口。**
5. **每一个迁移里程碑结束时，应用仍然可以构建、测试、运行和发版。**

本指南针对当前仓库的真实结构细化原计划，并给出边界设计、迁移次序、测试策略、风险控制和完成标准。

---

## 1. 先明确 Phase 1 做什么、不做什么

### 1.1 Phase 1 必须完成

- 建立 .NET solution 和 `SteamStat.Core`、`SteamStat.Platform.Windows`、`SteamStat.Contracts`、测试项目。
- `SteamStat.Core` 对 `ElectronNET.API` 保持零引用。
- 引入 `Microsoft.Extensions.Hosting`、内置 DI 和统一生命周期。
- 消除业务代码中的 4 处 `Electron.IpcMain.Send`。
- 将有状态、I/O、数据库、网络、计时器类从静态类迁移为 DI 管理的实例。
- 将 `AppDbContext` 改为 `DbContextOptions` + `IDbContextFactory<AppDbContext>`。
- 将 6 个实体配置拆为独立 `IEntityTypeConfiguration<T>`。
- 用结构化 `ILogger<T>` + Serilog 替代现有 Console 日志体系。
- 用 C# 单一来源生成 `preload.mjs` 和 `ipc.d.ts`。
- 用架构测试和 CI 固化依赖规则。

### 1.2 Phase 1 明确不做

这些工作属于后续 Phase，不能借“打地基”无限扩大范围：

- 不在本阶段完整实现 `ISteamGateway`、限流、熔断、持久缓存和 HTTP → CM 全量迁移。
- 不在本阶段彻底重写 Steam 登录/重连状态机；这里只建立可替换边界和实例生命周期。
- 不在本阶段新增成就、同步、存档解析等产品功能。
- 不在本阶段重构 Vue 页面、Pinia store 和通用 composable。
- 不同时改数据库 schema、IPC wire shape 和业务行为；结构迁移必须尽量保持行为等价。
- 不为了“纯净架构”预先拆出大量只有一个实现、没有真实边界价值的接口或项目。

### 1.3 对原计划的三点校正

#### 校正一：不是所有实例类都需要接口

应转换为实例类的是**持有状态或执行 I/O 的组件**；接口只放在下列边界：

- 平台边界：`IAppPaths`、`ISecretStore`、`ISteamInstallLocator`、`IProcessWatcher`。
- 外部系统边界：会话访问、HTTP、文件系统、Electron 窗口/IPC。
- 跨 Feature 的稳定只读能力：例如 `IAppNameResolver`。
- 测试确实需要替换的协作者。

`SteamIdHelper`、无状态的 VDF 值转换、纯扩展方法可以继续保持静态。为每个类机械创建 `IXxxService` 只会制造一对一样板代码。

#### 校正二：“Feature 之间不直接 using”应改为可执行规则

同一程序集内，只要引用另一个 Feature 的类型，代码就必然存在 `using` 或完整类型名。真正应禁止的是：

> **Feature A 不得依赖 Feature B 的实现、实体内部结构或持久化细节；只允许依赖 B 明确发布的 `Contracts`/只读接口，或消费事件。**

例如 Friends 可以依赖 `Features.Apps.Contracts.IAppNameResolver`，但不能依赖 `AppsService`、`SteamAppConfiguration` 或 Apps 的内部缓存。

#### 校正三：Phase 1 不过度抽象 SteamKit2

目标是隔离 Electron 和平台细节，而不是假装 SteamKit2 不存在。`SteamStat.Core/Steam` 可以引用 SteamKit2。Phase 1 应提供最小的 `ISteamSessionAccessor` 边界，停止暴露 `(SteamClient, CallbackManager, CancellationTokenSource)` 元组；不要提前发明一套覆盖 SteamKit2 全部 API 的通用包装层。真正的 `SteamSessionManager` 和会话状态机在 Phase 2 完成。

---

## 2. 当前基线审计

### 2.1 已具备的 Phase 0 基础

- Electron.NET 已通过 `third_party/Electron.NET` submodule 和根目录 `Directory.Build.props` 使用相对路径。
- PR/push CI 已包含前端 lint/build 与后端 build/test。
- 已有 38 个不联网的 NUnit 测试，覆盖 VDF/ACF、本地文件布局、SteamID 和设置。
- `docs/ARCHITECTURE.md`、`CONTRIBUTING.md` 和实验性功能开关已经存在。
- 共享 `HttpClientProvider` 已临时消除逐请求 `new HttpClient()` 的主要问题。

### 2.2 2026-08-28 实测结果

| 检查 | 结果 |
| --- | --- |
| `pnpm run lint:ci` | 通过 |
| `dotnet test ElectronNet/ElectronNet.Tests/ElectronNet.Tests.csproj -c Debug` | 38/38 通过，约 223 ms |
| Git 工作区 | `develop` 与 `origin/develop` 同步，检查时无未提交改动 |
| NuGet audit | 存在高危传递依赖告警，见下文 |

### 2.3 2026-08-29 M0 固定基线

| 检查 | 结果 |
| --- | --- |
| `pnpm run lint:ci` | 通过 |
| `pnpm run build` | 通过 |
| `dotnet test ElectronNet/ElectronNet.Tests/ElectronNet.Tests.csproj -c Debug` | 43/43 通过，包含 SQLite migration/schema/runtime、Settings merge 与 IPC contract characterization tests |
| `dotnet list ElectronNet/ElectronNet/ElectronNet.csproj package --vulnerable --include-transitive` | 无易受攻击的包 |
| 干净 Electron Host Debug build | 通过，runtime `43.4.1`、Node `24.18.1`；Electron 43 官方 EOL 为 2027-01-05 |
| `pnpm run build:win` | 通过；electron-builder `26.0.20` 使用 Electron `43.4.1` 生成 NSIS 安装器、block map、`latest.yml` 与 unpacked app，打包 runtime 实测为 Electron `43.4.1` / Node `24.18.1` |
| Electron target 输出 | 删除不可达且会误报的 `Electron setup failed!` / `Electron installation failed!` 文案；跳过时保持静默，真实失败由 `Exec` 直接终止并报告 |

Electron 42+ 改为按需下载 runtime 后，原 target 的固定 10 分钟超时在当前网络环境下会终止约 150 MB 的下载。M0 将该步骤超时与打包步骤统一为 30 分钟；本地首次验证使用 Electron 官方安装文档列出的 CDN mirror，mirror 不写入仓库配置。`build-win.ps1` 在 publish 阶段显式设置 `ElectronSkipExecCommands=true`，由随后的 electron-builder 按锁定版本获取打包 runtime；普通 Debug build 则安装并验证匹配版本的开发 runtime。

### 2.4 2026-08-29 M1 固定基线

- 根目录 `SteamStat.slnx` 成为唯一权威 solution，纳入 Contracts、Core、Platform.Windows、两个新测试项目及迁移期 Electron Host/Tests 共 7 个项目；原局部 `ElectronNet.slnx` 已删除。
- 建立 `SteamStat.Contracts`、`SteamStat.Core`、`SteamStat.Platform.Windows`、`SteamStat.Core.Tests`、`SteamStat.Architecture.Tests`，引用方向为 Host → Core/Platform/Contracts、Platform → Core。
- `SteamIdHelper` 与 3 个 Steam 本地文件模型进入 Core；8 个纯 SteamID 测试进入 Core.Tests，不再构建 Electron Host。
- Architecture.Tests 固定 Core 不引用 Electron、Console 或 Serilog；Host 保持原启动方式和行为。

### 2.5 2026-08-30 M2 固定基线

| 检查 | 结果 |
| --- | --- |
| `dotnet list SteamStat.slnx package --vulnerable --include-transitive`（变更前、变更后） | 7 个项目均无已报告的易受攻击包 |
| `dotnet test SteamStat.slnx -c Debug -p:ElectronSkipExecCommands=true` | 49/49 通过：Core 10、Architecture 2、Electron Host 37 |
| `dotnet build ElectronNet/ElectronNet/ElectronNet.csproj -c Debug` | 通过，0 warning / 0 error |
| `pnpm run lint:ci`、`pnpm run build` | 通过 |
| 普通开发启动与窗口关闭 smoke | Electron ready 后依次读取 `userData`、启动 Host、初始化窗口并注册 IPC；标准窗口关闭后 Host/Vite 正常退出，`Cleanup completed` 仅 1 次 |
| `--silent-start` 与托盘退出 smoke | 无可见 Steam Stat/DevTools 窗口，保留 1 个托盘图标；实际点击托盘 Exit 后 Host/Vite 正常退出，`Cleanup completed` 仅 1 次 |

- Core 锁定 `Microsoft.Extensions.Http 10.0.2`，Electron Host 锁定 `Microsoft.Extensions.Hosting 10.0.2`；Platform.Windows 仅因公开 `IServiceCollection` API 直接锁定 `Microsoft.Extensions.DependencyInjection.Abstractions 10.0.2`，未重复添加 Hosting 已提供且无需直接引用的包。
- `AppEnvironment`、`AppPaths/IAppPaths` 均为构造后不可变的 singleton；路径由 Electron ready 后取得的 `userData` 一次性派生。`AddSteamStatCore/AddSteamStatWindows/AddSteamStatElectron` 集中注册并始终启用 scope/build validation。
- `ApplicationStartupCoordinator` 显式维持 migration/data/settings/window/listener/tray/IPC 顺序；`IpcMainService` 已成为 DI singleton 实例 registrar，暂时继续调用旧 static feature services。
- 删除 async `ProcessExit` 和 `WillQuit` 重复清理；`ApplicationCleanupService.StopAsync` 用原子门保证正常退出只执行一次清理。Windows 开发模式使用完整进程树终止和有界等待，避免 Vite 包装进程持有输出管道导致 shutdown 卡死。
- smoke 所在 IDE 会向子进程注入 `ELECTRON_RUN_AS_NODE=1`；验证命令仅在自身进程作用域移除该变量，未写入仓库或用户配置。当前机器的 `loginusers.vdf` 仍会触发现有的非阻塞用户同步错误，Host 随后可完成窗口、IPC 和退出流程；该解析问题不属于 M2 生命周期改造。

### 2.6 Phase 1 开始前必须处理或登记的风险

1. **高危传递依赖不能忽略**
   M0 前 restore 报 `NU1903`：`SQLitePCLRaw.lib.e_sqlite3 2.1.11` 命中高危公告 `GHSA-2m69-gcr7-jv3q`。M0 已显式覆盖到 `3.53.3`，保留 EF Core `10.0.2`，并用 migration/schema/runtime characterization test 回归；根构建配置已将 `NU1903`/`NU1904` 提升为 error，未通过 `NoWarn` 隐藏。

2. **纯测试仍会构建 Electron Host**
   M1 已解决：`SteamStat.Core.Tests` 只引用 Core，纯 SteamID 测试不再触发 Electron 构建目标；`ElectronNet.Tests` 只保留 Host/兼容 characterization tests，因此继续构建 Host 符合其职责。

3. **Electron 构建输出有误导性失败文案**
   M0 前本地测试通过且退出码为 0，但 imported target 输出了 `Electron setup failed!` / `Electron installation failed!`。原因是命令未执行时仍读取未赋值的 `ExecExitCode`，且命令真实失败时 `ContinueOnError=false` 会先终止构建，使后置失败文案不可达。M0 已在 submodule 中删除这些文案并让运行提示与 skip 条件一致：跳过时保持静默，真实失败由 `Exec` 直接报告。

4. **已有局部 solution，但缺少覆盖目标架构的根 solution**
   M1 已解决：根 `SteamStat.slnx` 纳入全部 7 个新旧项目，原 `ElectronNet/ElectronNet.slnx` 已删除，只保留一个权威主 solution。

5. **Electron 32 已停止安全支持**
   M0 前 `ElectronVersion` 为 `32.0.0`，Electron 32 已于 2025-03-04 EOL，不再接收 Chromium/Node 安全修复。M0 已独立升级并锁定到 Electron `43.4.1`（官方 EOL 2027-01-05），干净 Debug build 已验证实际 runtime 与配置一致，因此无需登记安全例外。

### 2.7 Phase 1 启动时耦合的量化快照

以下为 M1/M2 前记录的数字，用于后续验收，不应只凭主观判断“重构好了”；M1/M2 的增量状态见上文：

- `Services/` 下有 17 个 `public static class`，另有静态 Job、Helper 和 `Program`。
- `Console.WriteLine/Write` 共 211 处。
- 业务服务中有 4 处 `Electron.IpcMain.Send`：
  - `SteamUserService.cs:140`
  - `SteamLoginService.cs:1027`
  - `SteamFriendsService.cs:602`
  - `UpdateService.cs:135`
- `AppDbContext.Create()/Instance` 有 37 个业务调用点（35 次 `Create`、2 次 `Instance`）。
- `Program.UserDataPath/Locale/IsDev/ElectronMainWindow` 被多个业务类直接读取。
- `IpcMainService.cs` 集中手写所有通道；`preload.mjs` 90 行、`ipc.d.ts` 352 行，三处人工同步。
- `SteamLoginService` 与 `SteamFriendsService` 存在双向调用：Login 在断线/重连时清理或刷新 Friends，Friends 又向 Login 获取会话。
- `SteamLoginService.GetSessionByAccountName` 直接暴露 SteamKit2 client、callback manager 和 CTS 元组。

---

## 3. 适配当前仓库的目标结构

原计划把所有 .NET 项目放到根 `src/`，但本仓库的 `src/` 已经是 Vue 前端。为避免前后端源码混杂和大规模移动，建议采用以下物理结构：

```text
steam-stat/
├─ SteamStat.slnx                         # .NET 10 默认；工具链不支持时改用 .sln
├─ backend/
│  ├─ src/
│  │  ├─ SteamStat.Core/
│  │  ├─ SteamStat.Platform.Windows/
│  │  └─ SteamStat.Contracts/
│  └─ tests/
│     ├─ SteamStat.Core.Tests/
│     └─ SteamStat.Architecture.Tests/
├─ ElectronNet/
│  ├─ ElectronNet/                        # Phase 1 保留物理路径，逻辑角色是 Host.Electron
│  └─ ElectronNet.Tests/                  # 迁移期保留的 Host/兼容测试
├─ tools/
│  └─ GenerateIpcContracts/
├─ src/                                   # 现有 Vue 前端，不移动
└─ third_party/Electron.NET/
```

### 3.1 为什么暂时不移动 Electron Host

`ElectronNet/ElectronNet` 与 Electron.NET imported targets、资源路径、打包脚本、安装器和发布工作流高度相关。Phase 1 一开始就重命名目录、项目、命名空间和程序集，会把架构迁移与打包迁移混在一起，降低可回滚性。

Phase 1 先让它在**逻辑上**成为薄 Host。待 solution、引用方向、构建和发布全部稳定后，再用单独 PR 决定是否移动到 `backend/src/SteamStat.Host.Electron`。物理目录名称不是 Phase 1 完成条件，程序集依赖方向才是。

### 3.2 项目职责与允许依赖

| 项目 | 职责 | 允许引用 | 禁止引用 |
| --- | --- | --- | --- |
| `SteamStat.Core` | 应用逻辑、Steam 能力、Feature、EF 模型与配置 | BCL、SteamKit2、ValveKeyValue、EF Core 抽象/SQLite | ElectronNET、Windows Registry、ServiceController、DPAPI、Host 项目 |
| `SteamStat.Platform.Windows` | 注册表、进程、Windows Service、DPAPI 的实现 | Core、Windows 专属包 | ElectronNET、Feature 实现 |
| `SteamStat.Contracts` | IPC channel、方向、方法名、请求/响应/事件 DTO | 尽量只用 BCL | Core 实体、EF Core、SteamKit2、ElectronNET |
| Electron Host | Composition Root、Electron 生命周期、窗口/托盘/更新、IPC adapter | Core、Platform.Windows、Contracts、ElectronNET.API | 被 Core/Platform/Contracts 反向引用 |
| `Core.Tests` | Core 单元与 SQLite 集成测试 | Core、测试包 | Electron Host、ElectronNET.API |
| `Architecture.Tests` | 依赖规则、命名空间规则、禁用 API 规则 | 被检查程序集、架构测试库 | 产品逻辑 |
| Generator | 读取 Contracts 元数据并生成 JS/TS | Contracts、必要的代码生成库 | Core、Host、ElectronNET |

### 3.3 最终引用图

```text
                       ┌───────────────────────────────┐
                       │ Electron Host / Composition   │
                       └───────┬─────────┬─────────┬───┘
                               │         │         │
                               ▼         ▼         ▼
                         Core      Platform.Win  Contracts
                           ▲             │
                           └─────────────┘

Generator ───────────────► Contracts
Core.Tests ──────────────► Core
Architecture.Tests ──────► inspected assemblies

禁止：Core ─► Host / Electron / Platform.Windows / Contracts
禁止：Contracts ─► Core / Host / Electron
```

Core 不应依赖 IPC Contracts。Core 发布领域/应用事件，Host 负责把事件映射为 IPC DTO。这可以防止前端传输格式反向塑造领域模型。

### 3.4 Feature 内部建议结构

```text
Features/Friends/
├─ Contracts/                  # 供其他 Core Feature 使用的窄接口/公开事件
├─ Models/                     # Friends 自己的模型
├─ Persistence/                # Entity 与 IEntityTypeConfiguration
├─ FriendsService.cs           # 用例编排
└─ Internal/                   # 不允许其他 Feature 依赖
```

不是每个 Feature 都必须有全部目录；没有内容时不要创建空目录。

### 3.5 命名规范

- 程序集/命名空间统一使用 `SteamStat.*`，不再新增 `ElectronNet.*` 业务命名空间。
- `Service` 表示有明确应用能力的对象；纯查询可用 `Reader/Resolver`，持久化可用 `Store`，外部访问用 `Gateway/Client`，后台循环用 `Worker`。
- 异步方法统一以 `Async` 结尾并接受 `CancellationToken`。
- DTO 用不可变 `sealed record`；EF Entity 保持适合 EF 的可变 class，不直接跨 IPC。

---

## 4. Composition Root 与生命周期

### 4.1 只有 Host 可以组装对象

只有 Electron Host 的启动代码可以：

- 调用 `Host.CreateApplicationBuilder`。
- 注册具体实现。
- 读取 Electron 静态 API。
- 选择 Windows 平台实现。
- 配置数据库连接、日志 sink 和 IPC adapter。

禁止在 Core 中调用 `BuildServiceProvider()`、保存全局 `IServiceProvider` 或通过 service locator 取服务。

### 4.2 使用“两阶段启动”，不要照搬 Web Host 模板

当前 `UserDataPath`、Locale 和环境类型只有 Electron Runtime ready 后才能可靠获得，而数据库和文件日志配置需要这些值。推荐启动顺序：

```text
1. 建立最小 bootstrap logger
2. Electron runtime Start
3. await WaitReadyTask
4. 设置/读取 Electron userData、Locale、IsDevelopment
5. 创建不可变 AppEnvironment 与 AppPaths
6. 创建 HostApplicationBuilder，注册 Core/Platform/Contracts/Host
7. Build + StartAsync(IHost)
8. DatabaseMigrator：备份并迁移
9. ApplicationInitializer：同步本地数据、升级旧 token、加载设置
10. 创建主窗口并注册 Electron listener
11. 注册 IPC handlers 和 IpcEventForwarder
12. 启动 BackgroundService
13. await Electron runtime WaitStoppedTask
14. StopAsync + DisposeAsync(IHost)
15. 必要时 Stop Electron runtime，关闭 bootstrap logger
```

这比让多个 `IHostedService` 竞争“谁先拿到 UserDataPath”更简单，也避免注册一个尚未初始化的可变 `IAppPaths`。

### 4.3 启动编排应显式，而不是依赖注册顺序的副作用

建议使用一个 Host 内的 `ElectronApplication` 或 `ApplicationStartupCoordinator` 显式执行步骤 8–12。数据库迁移完成前不能启动会访问数据库的 worker；主窗口创建完成前不能激活事件转发器。

不要把所有初始化都塞进多个 `IHostedService.StartAsync` 并假设注册顺序永远不会变化。

### 4.4 Electron.NET 的特殊约束

- `IpcMain.Handle/On` 通过 Socket bridge 注册，必须在 `WaitReadyTask` 完成后执行。
- `IpcMain.Send` 返回 `void`，属于 best-effort 发送，Host adapter 必须自行检查窗口是否存在/已销毁并记录失败。
- `WaitStoppedTask` 才是桌面进程的主要等待信号，不能只依赖 Generic Host 默认的 Console lifetime。
- 当前 `ProcessExit` 上的 async lambda 无法保证被 await，应删除；正常清理由 `try/finally + host.StopAsync()` 负责，`ProcessExit` 最多做极短的同步兜底。
- 不建议为了复用 `ElectronNET.AspNet.AddElectron()` 而给桌面 Host 引入整个 AspNet 项目；对实际使用的 Electron 单例建立很薄的 Host adapter 即可。

### 4.5 服务生命周期

| 类型 | 推荐生命周期 | 原因 |
| --- | --- | --- |
| `IEventBus` | Singleton | 进程内事件总线 |
| Steam 会话/缓存管理器 | Singleton | 与桌面进程和账号会话同生命周期 |
| `IAppPaths`、AppEnvironment | Singleton，不可变 | Electron ready 后一次确定 |
| Windows watcher/secret store | Singleton | 持有 OS 资源或无状态 |
| `IDbContextFactory<AppDbContext>` | Singleton（框架默认） | 每个工作单元创建短生命周期 Context |
| Feature service | 通常 Singleton 或 Transient | 无可变请求状态时可 Singleton；有临时状态时 Transient |
| `DbContext` | 每次操作新建 | 不是线程安全对象 |
| Background worker | Hosted Singleton | 由 Host 启停并接收 CancellationToken |

IPC 不天然形成类似 HTTP request 的 DI scope。如果某个 handler 使用 Scoped service，Host adapter 必须为每次调用显式创建 scope；否则优先采用无 request state 的实例服务 + `IDbContextFactory`。

### 4.6 注册入口

每个项目暴露一个内聚的注册方法，Host 不应列出几十个散乱注册：

```csharp
builder.Services
    .AddSteamStatCore()
    .AddSteamStatWindows()
    .AddSteamStatElectron(appEnvironment);
```

同时启用 DI 校验：开发/CI 环境至少设置 `ValidateScopes = true` 和 `ValidateOnBuild = true`，让循环依赖和 singleton 捕获 scoped service 在启动时失败。

---

## 5. 去静态化策略

### 5.1 分类处理，不做全局替换

| 当前类型 | 处理方式 |
| --- | --- |
| `SteamIdHelper`、纯 VDF 转换、扩展方法 | 可保留 static，保证纯函数测试 |
| `LocalFileService` | I/O 边界，转实例并抽窄接口 |
| `LocalRegService`、`LocalProcessService` | 移到 Platform.Windows，实现 Core 抽象 |
| `TokenProtectionService` | 变为 `DpapiSecretStore : ISecretStore`，移到 Platform.Windows |
| `HttpClientProvider` | 用 `IHttpClientFactory` named/typed clients 替代 |
| `UpdateAppRunningStatusJob`、Update timer | 用 `BackgroundService` + `PeriodicTimer` + CancellationToken |
| DB/网络/缓存/Steam 服务 | 转为实例，构造器注入依赖 |
| `IpcMainService` | 转为 Host 的 IPC registrar/adapter |
| `Program` | 只保留真正入口和 composition root |

### 5.2 优先打断真实依赖环

当前最关键的环是：

```text
SteamLoginService ─► SteamFriendsService
SteamFriendsService ─► SteamLoginService
```

处理方式：

1. 建立最小 `ISteamSessionAccessor`，供 Friends/Library 查询可用会话。
2. Login 不再直接调用 Friends。
3. Login 发布 `SteamSessionReady`、`SteamSessionEnded` 事件。
4. Friends 订阅事件并自行加载/清理缓存。
5. Library 如需清理账号缓存，也独立订阅结束事件。

Phase 1 的 session abstraction 只应覆盖现在真实需要的能力。禁止把 CTS 暴露给 Feature；回调订阅的取消由 Session 自己拥有。

### 5.3 Friends 的目标依赖

```text
FriendsService
├─ ISteamSessionAccessor
├─ IAppNameResolver
├─ IRichPresenceResolver
├─ IFriendStatusRecorder
├─ IEventBus
├─ TimeProvider
└─ ILogger<FriendsService>
```

- `_userFriendsData`、回调注册表成为 singleton 实例字段。
- 对缓存读写要么使用 `ConcurrentDictionary`，要么用明确的锁保护；普通 `Dictionary` 不能同时被 Steam callback 和 IPC handler 无锁访问。
- `Task.Run` fire-and-forget 改为受 Host 管理的队列或可追踪任务。
- 事件 payload 使用不可变快照，不能把仍在修改的 `List` 引用发给 UI。

### 5.4 Library 的目标依赖

```text
LibraryService
├─ ISteamSessionAccessor
├─ IAppNameResolver / IAppMetadataWriter
├─ ILanguageProvider
├─ IHttpClientFactory          # HTTP 仅作当前行为兼容，Phase 2 再治理管道
├─ TimeProvider
└─ ILogger<LibraryService>
```

- `_userLibraryCache` 成为实例状态并保证并发安全。
- `EnsureAppsCachedAsync` 不再通过裸 `Task.Run` 丢弃异常；通过后台工作队列执行，或在当前调用中 await。
- 不在 Phase 1 改写 owned/family/wishlist/achievement 的数据来源和合并算法。

### 5.5 Login 的目标依赖

```text
LoginService / LegacySteamSessionManager
├─ IDbContextFactory<AppDbContext>
├─ ISecretStore
├─ IEventBus
├─ TimeProvider
├─ ILogger<...>
└─ SteamKit2
```

Phase 1 先完成：

- 静态字段转为 singleton 实例字段。
- 对外不再返回包含 CTS 的元组。
- Electron IPC 改为事件。
- Login → Friends 直接调用改为会话生命周期事件。
- `LogoutAllUsersAsync`/Dispose 与 Host stopping token 对齐。

以下保留给 Phase 2：认证器、SessionManager、TokenStore 的完整三拆分；错误分类状态机；网络变化和重连策略重写；callback loop 优化。

### 5.6 Settings 需要拆“存储”和“副作用”

当前 `SettingService` 同时：

- 读写 JSON；
- 修改开机启动；
- 启停 Job；
- 控制自动更新；
- 修改 Electron window zoom。

目标拆分：

- `ISettingsStore`：只负责读取、合并、校验、原子写文件。
- `SettingsCoordinator`：编排设置变更。
- `IAutoStartManager`、`IWindowPreferences`、`IUpdaterController`、Job controller：Host/Platform 实现副作用。

`AppSettings.DefaultSettings` 不能再读取 `Program.Locale`；默认值由 `AppEnvironment`/factory 在运行时创建，避免模型静态初始化依赖 Electron。

### 5.7 后台任务

`System.Threading.Timer(_ => Task.Run(...))` 会造成重入、异常丢失和停机不可控。目标形态：

```csharp
while (await timer.WaitForNextTickAsync(stoppingToken))
{
    await UpdateAsync(stoppingToken);
}
```

要求：

- 同一个 worker 不并发重入。
- 所有 I/O 接收 `CancellationToken`。
- shutdown 等待当前操作完成或超时取消。
- worker 状态通过线程安全快照提供给 IPC，不暴露可写 public field。

---

## 6. IEventBus：可测试性的第一道分水岭

### 6.1 边界定义

不引入 MediatR 等额外框架，先实现小型、强类型、进程内事件总线：

```csharp
public interface IEventBus
{
    Task PublishAsync<TEvent>(TEvent message, CancellationToken cancellationToken = default)
        where TEvent : notnull;
}

public interface IEventHandler<in TEvent>
{
    Task HandleAsync(TEvent message, CancellationToken cancellationToken);
}
```

这里使用 `Task` 而不是 `ValueTask`：事件总线需要组合多个 handler，`Task` 的等待和聚合语义更不易被误用，当前事件频率也不足以证明 `ValueTask` 的复杂度有收益。

`IEventBus` 放在 Core abstraction；基于 DI 的 `InProcessEventBus` 实现放在 Host infrastructure 或将来独立 Infrastructure 项目，避免 Core 依赖 `IServiceProvider`。

### 6.2 明确事件语义

Phase 1 的 event bus 是：

- 仅进程内；
- 不持久化；
- 不保证跨进程投递；
- 用于通知和解耦，不用于数据库事务一致性；
- 同一事件的 handler 顺序不应成为业务前提；
- 单个 UI forwarder 失败只记录日志，不能让登录或同步操作回滚；
- 事件中禁止包含 password、access token、refresh token、guard data、QR challenge URL 等秘密。

如果未来出现必须可靠投递的同步事件，应单独设计 outbox，不能偷偷改变此总线语义。

### 6.3 四处 IPC 推送的替换映射

| 当前发送点 | Core/Host 事件 | IPC Forwarder 输出 |
| --- | --- | --- |
| `SteamUserService` | `LoginUsersChanged` | `steam:loginUsers:updated` |
| `SteamLoginService` | `SteamLoginProgressChanged` | `steamLogin:event` |
| `SteamFriendsService` | `FriendsChanged` | `steamFriends:update` |
| `UpdateService` | `UpdaterStateChanged`（Host 事件） | `updater:event` |

Updater 本身属于 Electron Host，不应为了“所有事件都在 Core”而移动进 Core。它可以复用同一个事件机制，但事件类型留在 Host。

### 6.4 Domain/Application event 与 IPC DTO 分离

```text
FriendsService
  └─ Publish FriendsChanged(core event)
       └─ FriendsIpcEventForwarder(host handler)
            ├─ map to FriendsUpdatedEventDto(contracts)
            └─ Electron.IpcMain.Send(...)
```

不要让 Core 直接发布 `SteamFriendsUpdateEventDto`，否则 Core 会反向依赖 transport contract。

### 6.5 Window accessor

`ElectronIpcEventForwarder` 不读取 `Program.ElectronMainWindow`，而依赖 Host 内的 `IMainWindowAccessor`。该 accessor：

- 只能由 window lifecycle service 设置/清空；
- 读取时返回稳定快照；
- 统一检查 null/destroyed；
- 不放进 Core。

### 6.6 必要测试

- 发布一个事件会调用对应 handler 一次。
- 多个 handler 都能收到事件。
- cancellation 能终止未执行 handler。
- forwarder 在没有窗口、窗口已销毁时安全跳过并记录日志。
- forwarder 序列化后的 property casing 和当前 camelCase wire shape 一致。
- handler 异常不会导致核心用例被错误标记为失败。

---

## 7. 持久化重构

### 7.1 目标 `AppDbContext`

```csharp
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<GlobalStatus> GlobalStatuses => Set<GlobalStatus>();
    // ...

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
}
```

必须删除：

- `_sqliteConnection` 字段；
- `Instance` 单例；
- `Create()` 静态方法；
- 读取 `Program.UserDataPath` 的 `OnConfiguring`；
- DbContext 内的备份/迁移职责。

### 7.2 Host 注册

连接字符串在 Host composition root 中由不可变 `IAppPaths` 构建：

```csharp
services.AddDbContextFactory<AppDbContext>((sp, options) =>
{
    var paths = sp.GetRequiredService<IAppPaths>();
    options.UseSqlite(CreateConnectionString(paths.DatabaseFile));
});
```

服务中使用：

```csharp
await using var db = await factory.CreateDbContextAsync(cancellationToken);
```

DbContext 不是线程安全对象。禁止 singleton service 缓存 Context，也禁止把 Entity 的 tracked instance 长期存入内存缓存。

### 7.3 配置按切片拆分

| 实体 | 建议归属 |
| --- | --- |
| `GlobalStatus` | `Features/LocalStatus/Persistence` |
| `SteamUser` | `Features/Users/Persistence` |
| `SteamApp` | `Features/Apps/Persistence` |
| `UseAppRecord` | `Features/UsageTracking/Persistence` |
| `SteamLoginToken` | `Features/Login/Persistence` |
| `FriendStatusRecord` | `Features/Friends/Persistence` |

每个 configuration 使用无参构造器。`ApplyConfigurationsFromAssembly` 的应用顺序未定义，因此配置之间不得依赖扫描顺序。

### 7.4 不添加通用 Repository

EF Core 的 `DbContext/DbSet` 已经承担 Unit of Work/Repository 的主要职责。不要创建 `IGenericRepository<T>` 再把 `IQueryable` 包一层。

只有在以下情况才增加 Feature-specific store/query interface：

- 需要隔离跨 Feature 读取；
- 查询本身是稳定的业务能力；
- 测试确实需要替换外部持久化；
- 需要集中事务或并发规则。

例如 `IFriendStatusRecorder` 有意义，`IRepository<FriendStatusRecord>` 没有表达业务意图。

### 7.5 迁移与备份独立成服务

`DatabaseMigrator` 在任何后台 worker 启动前执行：

1. 用 factory 创建短生命周期 Context。
2. 查询 pending migrations。
3. 如果有迁移，使用 SQLite Backup API 备份到临时文件。
4. 备份成功后原子替换 `steam-stat.bak`。
5. 执行 `MigrateAsync`。
6. 记录 migration id、耗时和结果，不记录用户数据。

如果未来启用 WAL，不能用普通 `File.Copy` 代替 SQLite 在线备份，否则可能漏掉 WAL 中尚未 checkpoint 的事务。

### 7.6 保持现有数据库兼容

移动 Context、Entity、configuration 和 migrations 时：

- 保持数据库路径 `{UserDataPath}/Database/steam-stat.db` 不变。
- 保持表、列、索引、约束和 migration id 不变。
- 将现有 migration 与 model snapshot 一起移动到目标 migration assembly。
- 不删除或重建 `__EFMigrationsHistory`。
- 结构重构完成后生成一次临时 migration；其 `Up/Down` 必须为空。验证后删除临时 migration。
- 用脱敏 fixture 数据库从最早支持版本逐步 migrate 到最新，比较 schema 和关键行数。
- 所有迁移验证只对 fixture/copy 操作，绝不直接拿用户生产数据库试验。

### 7.7 设计时工厂

提供 `IDesignTimeDbContextFactory<AppDbContext>`，因为 `dotnet ef` 不会启动 Electron 来提供 UserDataPath。设计时路径应来自显式参数/环境变量，缺省使用仓库下被 gitignore 的临时目录；禁止默认连接真实用户数据库。

M1 创建目标项目、M4 完成 design-time factory 后，文档化并固定命令，例如：

```bash
dotnet ef migrations add <Name> \
  --project backend/src/SteamStat.Core \
  --startup-project ElectronNet/ElectronNet
```

上面是**目标结构命令**，当前仓库在相应项目创建前不能直接执行。Windows PowerShell 中可写成一行；CI 应至少运行 `dotnet ef migrations list` 或对应的模型一致性测试。

### 7.8 SQLite 测试方式

不要使用 EF InMemory provider 验证 SQLite 行为。它不具备 SQLite 的约束、类型、SQL 和锁语义。

- 纯查询/配置测试使用 SQLite in-memory，但必须在整个测试 Context 生命周期保持同一个 connection 打开。
- 并发、备份、迁移测试使用临时 `.db` 文件。
- 每个测试独立数据库，允许并行执行时不能共享文件。
- 测试 `DefaultTimeout`、唯一索引、check constraint、migration history 和备份恢复。

---

## 8. IPC 契约与代码生成

### 8.1 单一事实来源

`SteamStat.Contracts` 应同时描述：

- channel 字符串；
- JS API method 名；
- 方向：Invoke、Send、HostToRendererEvent；
- request、response、event payload 类型；
- 是否允许空 request；
- 稳定的 camelCase 序列化规则。

示意：

```csharp
public static class SteamFriendsIpc
{
    public static readonly IpcInvoke<GetFriendsRequest, SteamFriendData?> GetForUser =
        new("steamFriends:getForUser", "steamFriendsGetForUser");
}
```

DTO 使用 C# record，但不能引用 EF Entity、SteamKit2 callback 或 Electron 类型。

### 8.2 Generator 选择

这里需要生成 C# 之外的仓库文件，优先使用确定性的 .NET console tool，而不是 T4 或复杂 Roslyn source generator：

```text
tools/GenerateIpcContracts
├─ 读取 SteamStat.Contracts 元数据
├─ 生成 ElectronNet/ElectronNet/Resources/preload.mjs
└─ 生成 src/types/ipc.d.ts
```

M6 创建 generator 项目后，工具必须支持以下**目标结构命令**：

```bash
dotnet run --project tools/GenerateIpcContracts -- --write
dotnet run --project tools/GenerateIpcContracts -- --check
```

项目创建前上述命令不可执行。`--check` 只比较内存生成结果与工作区文件，不修改文件；不一致时返回非 0，供 CI 使用。

### 8.3 生成规则

- endpoint 按稳定键排序，输出与反射发现顺序无关。
- 固定 UTF-8、LF 和文件头，跨 Windows/Linux 生成无 diff。
- C# nullable → TS `| null`，是否 optional 由契约显式决定，不能混为一谈。
- `long/ulong` 不能无条件映射 TS `number`；超过 `Number.MAX_SAFE_INTEGER` 的 SteamID 必须映射为 `string`。
- enum 生成 string union 或明确 enum，不生成 `any`。
- 遇到未知泛型、循环类型、字典非 string key 等不支持结构时直接失败，不能悄悄输出 `any`。
- Electron.NET 当前发送序列化为 camelCase、忽略 null、enum camelCase；生成类型和契约测试必须与此一致。

### 8.4 Host handler 保持手写编排，先只生成“契约”

Phase 1 不建议直接生成全部业务 handler 实现。生成器负责通道、preload bridge 和 TS 类型；Host 中的 registrar 仍负责把 endpoint 连接到具体 use case。原因是鉴权、校验、scope、异常映射和日志是运行时职责，不应藏在模板里。

Host registrar 必须引用 channel descriptor，不再写字符串字面量。

### 8.5 从 `Dictionary<string, object>` 到 typed request

当前 Electron.NET 将请求参数解析成 boxed primitives/dictionary。Host boundary 可以统一通过受控的 `IpcRequestBinder` 转为 Contracts request record：

- camelCase、case-insensitive；
- 对必填字段、长度、范围和 enum 做校验；
- 错误统一记录 endpoint 名和 correlation id；
- 日志不输出完整 request，尤其禁止记录登录密码和 token；
- Core 接收强类型参数，不接收 `object`、`dynamic` 或 `Dictionary<string, object>`。

`Dictionary<string, object>` 在迁移期只允许短暂存在于 Electron Host 的 binding 边界；即使 registrar 仍调用未迁移的旧 static service，也不能把该弱类型参数继续传播到已经迁入 Core 的 public API。

Phase 1 保持现有前端响应 shape，暂不全局引入新的 `Result<T>` envelope，避免把架构迁移变成前后端协议破坏性升级。

### 8.6 事件监听 API

当前 preload 的 `removeAllListeners(channel)` 会移除该 channel 上的全部监听者。生成器长期应返回精确 unsubscribe function，只移除本次注册的 callback。为保持 Phase 1 行为兼容，可以先生成现有 API，再以独立兼容 PR 升级监听接口。

### 8.7 Channel 命名

现有 channel 混用 `steam:*` 与 `steamFriends:*`。Phase 1 的首要目标是单一来源，不要同时大规模改名。旧 channel 先原样登记；新 endpoint 统一采用小写、冒号分段的规则。旧 channel 改名必须提供兼容期或与前端同一原子提交完成。

### 8.8 Electron renderer 安全边界

当前主窗口设置了 `ContextIsolation = true`、`WebSecurity = true`，但同时设置了 `NodeIntegration = true`；后者会扩大 renderer 被注入脚本利用后的能力，并自动削弱 sandbox。仓库前端源码目前没有直接使用 Node API，因此 Phase 1 应用单独的兼容 PR 完成：

- 将 `NodeIntegration`、`NodeIntegrationInWorker`、`NodeIntegrationInSubFrames` 明确设为 `false`。
- 保持 `ContextIsolation = true`、`WebSecurity = true`、`AllowRunningInsecureContent = false`。
- 在 preload 兼容测试通过后启用 `Sandbox = true`；若 Electron.NET bridge 暂不兼容，应记录阻塞原因，不能无说明地永久关闭。
- 只通过 `contextBridge` 暴露最小、生成的 API，绝不向 renderer 暴露原始 `ipcRenderer`、文件系统或进程执行能力。
- `shell:openExternal` 只允许经过校验的协议（通常是 `https`/`http`，确有产品需求时再允许其他协议），拒绝 `javascript:`、`data:`、`file:` 等危险 scheme。
- `shell:openPath` 不能接受 renderer 提供的任意路径；应限定为应用产生/用户明确选择的路径，并在 Host 重新规范化和授权。
- 所有 IPC request 即使来自本地 renderer 也按不可信输入处理，执行类型、长度、范围和路径校验。

这些修改必须配合启动和核心页面 smoke test，不能只翻转配置后假设 preload 一定兼容。

---

## 9. 日志体系

### 9.1 目标

- 产品代码只依赖 `Microsoft.Extensions.Logging.ILogger<T>`。
- Serilog 只在 Host composition root 配置。
- 文件日志位于 UserData 下，而不是安装目录。
- 支持 rolling、保留期限、结构化字段和异常 stack trace。
- 密码、token、guard data、Authorization header、QR challenge 永不进入日志。

### 9.2 依赖和配置原则

Host 使用与 .NET 10 匹配并锁定版本的：

- `Serilog.Extensions.Hosting`
- `Serilog.Sinks.File`
- 开发环境需要时使用 `Serilog.Sinks.Console`

不要一次性添加不必要的 sink/enricher。包版本先经过 NuGet audit，并遵守仓库依赖版本策略。

推荐字段：

- `SourceContext`
- `Feature`
- `AccountName`（必要时脱敏/哈希）
- `AppId`
- `Operation`
- `CorrelationId`
- `ElapsedMs`

### 9.3 `ConsoleLogPrefix` 不应直接“转成 scope”

更准确的迁移方式：

- 类别来自 `ILogger<T>` 的 `SourceContext`。
- 临时上下文使用 `BeginScope`，例如 AccountName/AppId。
- `ConsoleLogPrefix.DB/IPC/...` 的语义映射为 logger category 或结构化 `Feature` 字段。
- 不再把 `[Steam Stat XXX]` 拼进 message template。

示例：

```csharp
using var scope = logger.BeginScope(new Dictionary<string, object?>
{
    ["AccountName"] = accountName,
    ["Operation"] = "LoadFriends"
});

logger.LogInformation("Loaded {FriendCount} friends", friends.Count);
logger.LogError(exception, "Failed to load friends");
```

### 9.4 日志级别

| Level | 用途 |
| --- | --- |
| Trace | 高频 callback 细节，默认关闭 |
| Debug | 缓存命中、状态转换、开发诊断 |
| Information | 启动完成、迁移完成、登录/退出等正常里程碑 |
| Warning | 可恢复降级、缺失可选文件、即将重试 |
| Error | 当前操作失败但进程仍可继续 |
| Critical | 数据库无法迁移、Host 无法启动等进程级失败 |

“返回空集合”的失败不能只留一条普通文本；至少记录结构化 Error，并由用例明确区分“真实为空”和“获取失败”。用户可见的统一错误模型可在后续切片重构继续完善。

### 9.5 渐进迁移

1. 先配置 bootstrap/final logger 和 `ILogger<T>`。
2. 按实例化顺序迁移 Program/Host、数据库、Login、Friends、Library、其他 Feature。
3. 未迁移且仍留在旧 Electron Host 的静态代码暂时保留 `ConsoleHelper`；任何代码迁入 Core 时必须在同一 PR 改用 `ILogger<T>`，不把 Console 调用带入 Core。
4. 当全部产品代码 `Console.WriteLine` 计数归零后，删除 `ConsoleHelper` 和 `ConsoleLogPrefix`。
5. 从 M1 起架构测试禁止 Core 出现 `Console.WriteLine` 和 Serilog 静态 `Log.*`；迁移期只允许旧 Host 保留存量调用，并禁止新增。

### 9.6 文件策略

建议初始策略：按天或大小 rolling、保留有限天数/文件数、单进程写入、进程退出 flush。具体容量应通过真实日志量确定，不要无限保留。日志目录创建失败时回退到 bootstrap logger，并向用户暴露可诊断错误，不能静默吞掉。

---

## 10. 测试与架构守护

### 10.1 测试金字塔

1. **纯单元测试**：解析、映射、事件、状态变化、设置合并。
2. **SQLite 集成测试**：configuration、查询、迁移、备份、并发。
3. **Host adapter 测试**：IPC binding、DTO mapping、event forwarding，Electron API 用窄 fake 替换。
4. **少量手工桌面 smoke**：真实 Electron 启动、页面调用、退出清理。

Core.Tests 必须满足现有贡献约定：不联网、不要求本机安装 Steam、不启动 Electron、毫秒级完成。

### 10.2 Phase 1 开始前补的 characterization tests

- 当前 38 个测试迁移到 Core 后结果不变。
- 记录现有 IPC channel、JS method、invoke/send/event 方向的 snapshot。
- 对四种 host-to-renderer event 记录当前 JSON shape。
- Settings 默认值和 merge 行为。
- 现有数据库 migration history 与 schema fixture。
- Steam Login 对 terminal/non-terminal EResult 的当前分类。
- Friends/Library 合并算法中无需联网的纯映射部分。

### 10.3 架构测试的强制规则

至少覆盖：

1. `SteamStat.Core` 的程序集引用不包含 `ElectronNET.API`。
2. Core 源码/IL 不依赖 `ElectronNet.Program`、`Microsoft.Win32.Registry`、`ServiceController`、`ProtectedData`。
3. `SteamStat.Contracts` 不依赖 Core、EF Core、SteamKit2 或 Electron。
4. `SteamStat.Platform.Windows` 只实现 Core abstraction，不依赖 Feature internal implementation。
5. Feature 只可依赖其他 Feature 的 `.Contracts` 命名空间，不可依赖 `.Internal`/`.Persistence`/具体 service。
6. `Electron.IpcMain.Send` 只允许出现在 Host 的 `ElectronIpcEventForwarder`。
7. Core 不允许 `Console.WriteLine`、Serilog 静态 API、`Program.*`。
8. Core 不允许新增有可变静态字段的 service。
9. 生成文件执行 `--check` 后无 diff。

程序集引用规则可用反射完成；命名空间/类型依赖规则可选用经审计、锁定版本的 `NetArchTest.Rules`，或实现小型 IL/source 检查。不要仅用 grep 代替所有架构测试，但 grep 可作为额外快速门禁。

### 10.4 时间与随机性

项目当前有多处 `DateTimeOffset.UtcNow`。在 .NET 10 中优先注入 BCL 的 `TimeProvider`，而不是再造 `IClock`；只有出现 BCL 无法表达的时钟能力时再增加自定义接口。随机抖动同样通过可替换策略或受控 Random 注入，以便测试。

### 10.5 CI 目标形态

当前迁移前可执行的 solution/audit 入口是：

```bash
dotnet build ElectronNet/ElectronNet.slnx -c Debug
dotnet test ElectronNet/ElectronNet.slnx -c Debug --no-build
dotnet list ElectronNet/ElectronNet/ElectronNet.csproj package --vulnerable --include-transitive
```

M1 建立根 solution、M6 建立 generator 后，后端 CI 改为以下**目标结构命令**：

```bash
dotnet restore SteamStat.slnx
dotnet build SteamStat.slnx -c Debug --no-restore
dotnet test SteamStat.slnx -c Debug --no-build
dotnet run --project tools/GenerateIpcContracts -- --check
dotnet list ElectronNet/ElectronNet/ElectronNet.csproj package --vulnerable --include-transitive
```

`dotnet list package --vulnerable` 用于输出审计清单，但不能单独假定它会让 CI 失败。应在根构建配置中启用 NuGet audit，并至少将 High/Critical 对应的 `NU1903`/`NU1904` 设为 error；禁止加入 `NoWarn`。引入该门禁前先解决当前已知的 `NU1903`。

要求：

- Core.Tests 在不初始化 submodule/Electron runtime 的条件下可独立运行。
- Architecture.Tests 每个 PR 必跑。
- Generator 最好能在 Ubuntu job 独立执行，以证明 Contracts/tool 不依赖 Windows/Electron。
- 高危/严重 NuGet audit 告警阻断 CI，不通过 `NoWarn` 绕过。
- 当前前端 `pnpm run lint:ci` 和 `pnpm run build` 保留。

---

## 11. 推荐实施里程碑

以下按依赖关系排序，不按日历周排序。每个里程碑应拆成一个或少量职责单一的 PR，合并后应用仍可运行。

### M0：准入基线与安全门槛

**工作：**

- 以最小兼容变更解决 `SQLitePCLRaw.lib.e_sqlite3` 高危依赖，优先安全补丁/受支持的传递依赖覆盖，不在此处顺带升级 EF Core 主版本。
- 对 Electron 32 → 受支持版本做独立兼容升级；若被 fork/bridge 阻塞，登记明确的安全例外和消除条件。
- 固定当前 build/test/lint 结果。
- 增加必要 characterization tests。
- 记录 Electron target 误导性失败消息并修复或隔离。

**出口：**

- `pnpm run lint:ci`、前端 build、后端 test 全绿。
- NuGet 无 High/Critical vulnerability。
- Electron 使用仍受支持的版本，或安全例外已被显式接受并有确定的消除里程碑。
- 不改变用户可见行为。

### M1：Solution、项目骨架与依赖铁律

**工作：**

- 创建 `SteamStat.slnx`（或明确选择 `.sln`）。
- 创建 Core、Platform.Windows、Contracts、Core.Tests、Architecture.Tests。
- 添加项目引用并立即加入“Core 不引用 Electron”的首条测试。
- 迁移最纯的 Helper/LocalFiles model 和现有纯测试到 Core。

**出口：**

- `dotnet test SteamStat.slnx` 通过。
- Core.Tests 不引用 Host，运行时不触发 Electron 构建目标。
- Host 继续按原方式运行。

### M2：AppEnvironment、Generic Host 与 Composition Root

**工作：**

- 添加并锁定与 .NET 10 对齐的 `Microsoft.Extensions.Hosting`、`Microsoft.Extensions.Http`；先执行 NuGet audit，不重复显式添加已由 Hosting 提供且无需直接锁定的包。
- 实现两阶段启动。
- 建立不可变 `AppEnvironment/IAppPaths`。
- 添加 `AddSteamStatCore/AddSteamStatWindows/AddSteamStatElectron`。
- 将 `IpcMainService` 转为实例 registrar，但可暂时调用旧 static service。
- 用 Host shutdown 替换 async `ProcessExit` 清理。

**出口：**

- Electron ready 前不会注册 IPC/访问 userData。
- 正常退出只执行一次 cleanup。
- 开发启动、静默启动、窗口关闭、托盘退出均 smoke 通过。

### M3：Event Bus 与四个 UI 推送解耦

**工作：**

- 实现 `IEventBus/IEventHandler<T>`。
- 建立 `IMainWindowAccessor` 和 `ElectronIpcEventForwarder`。
- 将四处直接 Send 逐个替换为 typed event。
- Login → Friends 的直接生命周期调用改为 session event。

**出口：**

- 业务项目内搜索不到 `Electron.IpcMain.Send`。
- 唯一允许发送点在 Host forwarder。
- 对应单元测试和事件 JSON compatibility tests 通过。

### M4：Persistence 工厂化

**工作：**

- AppDbContext 接受 options。
- 拆 6 个 entity configuration。
- 引入 `IDbContextFactory`。
- 提取 DatabaseMigrator 和 design-time factory。
- 按 Feature 批次替换 37 个调用点。

**建议批次：**

1. FriendStatusRecord、GlobalStatus 等较小路径。
2. SteamUser、UseAppRecord。
3. SteamApp。
4. SteamLogin/token。
5. Program 中 migration/dispose。

**出口：**

- `AppDbContext.Create/Instance` 为 0。
- 不存在无参生产构造和 `OnConfiguring` 中的 Program 路径。
- 临时 schema migration 为空。
- fixture 老库可以备份并升级，数据校验通过。

### M5：平台边界与三个实验性 Feature 实例化

**工作：**

- LocalReg/LocalProcess/DPAPI 移入 Platform.Windows。
- `HttpClientProvider` 迁为 `IHttpClientFactory`，只保持现有行为。
- 按“Session 最小边界 → Login → Library → Friends”的依赖顺序实例化。
- Friends/Library/Login 的 cache、timer、callback subscription 纳入对象生命周期。
- 在业务代码迁入 Core 时，将 `DateTimeOffset.UtcNow` 改为注入 `TimeProvider` 并用 `GetUtcNow()`，不把硬编码时间源带入新层。
- Settings 拆存储与 Electron 副作用。

**出口：**

- Core 不出现 Windows/Electron API。
- Login/Friends 不再互相构造或直接调用实现。
- singleton 可变状态都有并发策略和明确清理路径。
- 登录、好友、库的现有前端功能保持兼容。

### M6：IPC Contracts 与生成器

**工作：**

- 把现有 endpoint 全量登记为 C# 契约。
- 引入 typed request/response/event DTO。
- 生成 preload 和 TypeScript declaration。
- Host registrar 只引用 descriptor，不写 channel 字符串。
- CI 加 `--check`。
- 用独立兼容 PR 关闭 renderer Node integration、验证 sandbox，并收紧 shell/path IPC。

**出口：**

- 三处手抄变为一个事实来源。
- 生成结果可重复、跨平台无 diff。
- 现有所有前端 API 的名称、方向和 wire shape 通过 snapshot。
- Renderer 仅能访问 contextBridge 暴露的最小 API，不能直接获得 Node 或原始 ipcRenderer 能力。

### M7：结构化日志、后台任务与最终收口

**工作：**

- 配置 bootstrap/final Serilog。
- 全量迁移 211 处 Console 输出。
- Timer job 改为 BackgroundService/PeriodicTimer。
- 完善全部架构规则、CI 和 smoke checklist。
- 更新 `docs/ARCHITECTURE.md` 与 `CONTRIBUTING.md` 中已过时的路径/命令（在实际实现 PR 中完成）。

**出口：**

- 产品代码 `Console.WriteLine` 为 0。
- `ConsoleHelper/ConsoleLogPrefix` 可删除。
- shutdown 能取消并等待后台任务。
- Phase 1 完成定义全部满足。

---

## 12. 每个迁移 PR 的标准步骤

1. **先写或补 characterization test**，固定当前行为。
2. **引入新接口/实例实现**，旧调用路径仍可工作。
3. **只迁移一个调用链/Feature**，不要同时全局重命名。
4. **将 composition root 切到新实现**。
5. **删除旧路径**，不长期保留两套实现和静态 facade。
6. **运行 Core test、architecture test、Host build 和相关 smoke**。
7. **检查依赖方向、日志秘密、生成物 diff 和数据库兼容。**
8. **确保提交本身可发布**，再进入下一批。

### 12.1 迁移期允许的过渡手段

- 新 `IpcRegistrar` 可以同时调用已迁移实例服务和未迁移 static service。
- `ISteamSessionAccessor` 可以短期适配现有 Login 的内部 session 字典。
- 旧测试项目可以保留 Host 集成测试，纯测试逐步移入 Core.Tests。

### 12.2 禁止的过渡手段

- `GlobalServiceProvider.Instance.GetService<T>()`。
- 在 static facade 中永久转发到 DI。
- Core 通过 callback/delegate 间接调用 Electron，借此绕过架构测试。
- 为通过测试而把 `ElectronNET.API` 标记成可选引用。
- 同时保留旧/新数据库配置并靠运行时分支选择。
- 捕获所有异常后返回空集合，导致“失败”和“没有数据”不可区分。
- 在事件或日志中放入凭据。

---

## 13. 风险清单与控制

| 风险 | 触发点 | 控制措施 |
| --- | --- | --- |
| 重构范围失控 | 顺手实现 Phase 2/3 功能 | 每个 PR 写明 In scope/Out of scope，以行为等价为主 |
| 打包流程被目录移动破坏 | 过早重命名 Host | Phase 1 保留 `ElectronNet/ElectronNet` 物理路径 |
| Electron ready 顺序错误 | Host 启动即注册 IPC | 两阶段启动；ready 后显式 registrar |
| 清理执行两次 | finally、ProcessExit、Electron event 都调用 | 单一 shutdown coordinator + idempotent guard |
| DbContext 并发错误 | singleton 缓存 Context | 只注入 factory，每个操作创建并释放 |
| SQLite locked | 多 worker 同时长事务写 | 短事务、async、合理 timeout；必要时按业务写队列，不全局大锁 |
| 老数据库损坏 | 移 migration/context 时产生 schema diff | SQLite backup、fixture 升级、空 migration 验证 |
| 登录凭据泄露 | typed DTO/log scope/event | Contracts 审查、日志脱敏测试、事件禁止秘密 |
| Friends/Login 环继续存在 | 仅把 static 改实例 | session lifecycle event + architecture rule |
| Event bus 变成隐藏流程 | 大量业务逻辑依赖 handler 顺序 | 只用于通知/解耦；关键同步流程显式调用 |
| Generator 输出不稳定 | 反射顺序/换行差异 | 排序、UTF-8/LF、golden tests、Linux `--check` |
| IPC 破坏前端 | 改名/改 shape 与重构一起做 | snapshot + 保留 channel/shape；改名独立 PR |
| Electron 32 EOL | Chromium/Node 漏洞不再回补 | 独立升级到受支持版本；阻塞时登记安全例外和消除条件 |
| 日志迁移中丢日志 | 过早删 ConsoleHelper | 最后一个 Console 迁完后再删除 |
| 无价值抽象膨胀 | 每个类一个接口/项目 | 只为真实边界、替换点、跨切片契约抽象 |
| 静态缓存并发竞态 | callback 与 IPC 并发访问 Dictionary | ConcurrentDictionary/锁/不可变快照 + 并发测试 |

---

## 14. Phase 1 完成定义（Definition of Done）

### 14.1 架构

- [ ] 存在唯一主 solution，包含所有 .NET 产品和测试项目。
- [ ] Core 编译期不引用 ElectronNET、Windows 专属实现或 Host。
- [ ] Contracts 不引用 Core Entity/SteamKit2/EF/Electron。
- [ ] ElectronNET.API 只出现在 Host 项目。
- [ ] Feature 不依赖其他 Feature 的 Internal/Persistence/具体实现。
- [ ] Login/Friends 双向依赖已消除。

### 14.2 生命周期与 DI

- [ ] Host 是唯一 composition root。
- [ ] 没有 service locator 和业务全局 service provider。
- [ ] 有状态业务 service 不再是 static。
- [ ] 所有 timer/callback/session/cache 有明确 owner 和释放路径。
- [ ] 正常关闭会取消并等待后台任务；cleanup 只执行一次。

### 14.3 Event/UI 边界

- [ ] 4 处业务 `IpcMain.Send` 全部替换为 typed event。
- [ ] Host 中只有一个 Ipc event forwarding 区域。
- [ ] Core event 与 IPC DTO 分离。
- [ ] 事件中无秘密数据。

### 14.4 数据库

- [ ] `AppDbContext.Instance/Create` 调用为 0。
- [ ] 所有数据库操作使用 `IDbContextFactory` 创建短生命周期 Context。
- [ ] 6 个 entity configuration 自动扫描装配。
- [ ] design-time factory 可独立执行。
- [ ] 现有 migration history、数据库路径和 schema 保持兼容。
- [ ] 迁移前 SQLite backup 有自动化测试。

### 14.5 IPC 契约

- [ ] channel、API method、方向和 DTO 以 C# 为唯一来源。
- [ ] preload 和 `ipc.d.ts` 全量生成。
- [ ] CI 的 generator `--check` 通过。
- [ ] 不再向 Core 传入 `object/dynamic/Dictionary<string, object>`。
- [ ] SteamID 等 64 位值不会错误映射为不安全的 TS number。

### 14.6 日志与安全

- [ ] 产品代码不再使用 `Console.WriteLine` 和 Serilog static logger。
- [ ] 日志经 `ILogger<T>` 写入 rolling file。
- [ ] 日志文件位于 UserData，存在保留上限。
- [ ] password/token/guard/Authorization/QR secret 有防泄漏测试或审查门禁。
- [ ] NuGet audit 无 High/Critical 告警。
- [ ] Electron runtime 处于官方支持期；如存在临时安全例外，已被明确接受并绑定消除里程碑。
- [ ] Renderer 的 `NodeIntegration` 关闭，`ContextIsolation`/`WebSecurity` 保持开启。
- [ ] Sandbox 已开启；若受 Electron.NET bridge 阻塞，存在明确的兼容性记录与后续门禁。
- [ ] Shell/路径 IPC 在 Host 侧执行协议、规范化和授权校验。

### 14.7 测试与交付

- [ ] `pnpm run lint:ci` 通过。
- [ ] `pnpm run build` 通过。
- [ ] `dotnet build <solution> -c Debug` 通过。
- [ ] `dotnet test <solution> -c Debug` 通过。
- [ ] Core.Tests 不启动 Electron、不联网、不依赖真实 Steam。
- [ ] Architecture.Tests 覆盖本指南中的依赖铁律。
- [ ] 登录、退出、好友刷新、库刷新、设置、自动更新事件、使用记录完成手工 smoke。
- [ ] Debug、Release/安装包至少各完成一次构建验证。
- [ ] 每个里程碑结束时均可从 `develop` 产出可运行版本。

---

## 15. 建议的手工 Smoke Checklist

### 启动与退出

- 普通启动能显示窗口。
- `--silent-start` 不显示窗口但托盘和后台任务正常。
- Vite dev server 启动失败时有明确日志且不空转 CPU。
- 关闭窗口、托盘退出、Electron 主进程退出都不会留下 dotnet/Vite 子进程。
- cleanup 日志只出现一次。

### 数据库

- 新用户首次启动创建数据库。
- 已有数据库无 pending migration 时正常启动。
- 有 pending migration 时先备份后升级。
- 模拟迁移失败时保留原库和可用备份，不继续启动写数据的 worker。

### IPC

- 每个 invoke/send endpoint 可调用。
- 参数缺失/类型错误不会令 Host 崩溃。
- renderer reload 后 listener 不重复注册。
- 登录、好友、用户、更新事件 payload 与当前前端兼容。

### Steam 实验性功能

- 密码、二维码、已保存 token 登录路径可进入原有状态。
- 用户主动退出不会触发自动重连。
- 账号断开时 Friends/Library cache 按事件清理。
- 多账号登录时各自 callback/cache 不串号。
- 500+ 好友、1000+ 游戏的数据处理不因新增 mapping/event 产生明显阻塞。

### 日志与秘密

- 日志 rolling 和保留策略生效。
- 异常包含 stack trace 和 Feature/Operation。
- 搜索日志确认没有 password、access token、refresh token、guard data。

---

## 16. 最关键的决策总结

1. **先建立可执行的依赖规则，再迁文件。**
2. **保留现有 Electron Host 物理路径，先让它逻辑变薄。**
3. **Electron ready 后取得不可变环境，再构建完整 Host。**
4. **Core event 与 IPC DTO 分离，Host 是唯一翻译层。**
5. **接口服务于边界，不服务于形式；纯函数可以保持 static。**
6. **Feature 可以依赖对方公开 Contracts，不能依赖实现。**
7. **`IDbContextFactory` 直接使用，不叠加通用 Repository。**
8. **使用内置 `TimeProvider`，避免无必要的 `IClock`。**
9. **Phase 1 只建立最小 Steam session 边界，不抢跑 Phase 2。**
10. **每个 PR 行为等价、可测试、可运行、可回滚。**

如果只能优先确保三件事，应依次确保：

1. `SteamStat.Core` 永远无法引用 Electron；
2. 所有业务 → UI 通知都经过 event bus + Host forwarder；
3. 所有生命周期、数据库 Context 和后台任务都由 Host/DI 管理。

这三条一旦由架构测试和 CI 固化，后续 Steam Gateway、成就、同步和存档解析才会建立在可靠地基上，而不是继续扩大当前静态耦合。

---

## 17. 参考资料

- [.NET Generic Host](https://learn.microsoft.com/en-us/dotnet/core/extensions/generic-host)
- [.NET Dependency Injection](https://learn.microsoft.com/en-us/dotnet/core/extensions/dependency-injection/overview)
- [EF Core DbContext configuration / factory](https://learn.microsoft.com/en-us/ef/core/dbcontext-configuration/)
- [EF Core design-time DbContext creation](https://learn.microsoft.com/en-us/ef/core/cli/dbcontext-creation)
- [EF Core ApplyConfigurationsFromAssembly](https://learn.microsoft.com/en-us/dotnet/api/microsoft.entityframeworkcore.modelbuilder.applyconfigurationsfromassembly?view=efcore-10.0)
- [Serilog.Extensions.Hosting](https://github.com/serilog/serilog-extensions-hosting)
- [.NET 10 默认 SLNX solution 格式](https://learn.microsoft.com/en-us/dotnet/core/compatibility/sdk/10.0/dotnet-new-sln-slnx-default)
- [Electron Release Schedule](https://releases.electronjs.org/schedule)
- [Electron Security Checklist](https://www.electronjs.org/docs/latest/tutorial/security)
