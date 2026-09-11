# Steam Stat Phase 2：Steam 接入层实施指南

> 状态：实施指导稿
> 基线分支：`develop`
> 基线提交：`9bc53f5`（2026-09-07 审查时）
> 上位规划：[plan-260823.md](./plan-260823.md)
> 前置阶段：[Phase 1 实施指南](./phase-1-implementation-guide.md)
> 相关文档：[架构说明](../ARCHITECTURE.md)、[贡献指南](../../CONTRIBUTING.md)、[Smoke 清单](./smoke-checklist.md)

---

## 0. 文档目的与结论先行

Phase 2 的目标不是把所有 SteamKit2 调用包进一个巨大的 `ISteamGateway`，也不是用更多接口重新包装现有代码，而是建立一个可以长期演进的 **Steam 防腐层（Anti-corruption Layer）**：

1. `SteamClient`、`CallbackManager`、认证、重连和账号状态由唯一的会话子系统管理。
2. Feature 不再自行选择 CM、Steam HTTP、CDN 或本地缓存，而是依赖按业务能力划分的 Gateway。
3. 缓存、请求合并、限流、重试、熔断、错误分类和来源追踪成为共享机制。
4. “没有数据”和“获取失败”必须成为两种不同结果；网络失败不能再被转换成空列表。
5. 断网或部分 Steam 域名不可达时，应用仍能返回带时间戳的本地数据，并明确说明数据来源和新鲜度。
6. 每个迁移里程碑都保持现有 IPC wire shape、数据库兼容性、可构建性和可发版性。

建议采用以下核心判断：

> **统一出口是统一架构边界，不是统一成一个万能接口。**

在代码上应使用 `ISteamAppCatalogGateway`、`ISteamLibraryGateway`、`ISteamProfileGateway` 等窄能力接口；这些接口及其共享管道共同构成计划中的 Steam Gateway。不要让 Feature 注入一个拥有几十个方法的 `ISteamGateway`。

---

## 1. 当前仓库基线审计

### 1.1 Phase 0 / Phase 1 已经提供的地基

当前代码已经具备 Phase 2 所需的大部分工程基础，不应重复建设：

- 根 `SteamStat.slnx` 是唯一 solution。
- `SteamStat.Core` 不引用 Electron、Host 或 Windows 实现。
- Generic Host、DI、`TimeProvider`、`ILogger<T>`、Serilog rolling file 已落地。
- `IEventBus` 已替代业务层直接 `Electron.IpcMain.Send`。
- `IDbContextFactory<AppDbContext>`、启动前备份迁移和分切片 EF configuration 已落地。
- typed IPC descriptor、C# → preload/TypeScript 生成器和架构测试已落地。
- 当前 `develop` 已从 `1.3.0-M7` 提升到 `1.3.0`；2026-09-07 实测完整 solution 为 116/116 通过（Core 16、Architecture 27、Host 73）。

因此，Phase 2 应是 **在现有边界内替换 Steam 接入实现**，而不是再次调整 solution、IPC 生成方式、日志体系或 Host 生命周期。

### 1.2 当前 Steam 接入的真实状态

| 主题 | 当前实现 | Phase 2 的问题 |
| --- | --- | --- |
| 会话入口 | `SteamLoginService` 同时实现登录、认证交互、会话表、callback pump、重连、token 管理和 UI 事件 | 约 900 行，职责过多，无法独立验证状态机 |
| Session 契约 | `ISteamSession` 直接暴露 `SteamClient` 和 `CallbackManager` | Feature 可以绕开统一管道，SteamKit2 继续向业务层扩散 |
| Callback pump | 登录和重连路径均使用 `Task.Run` + `RunWaitCallbacks(100ms)` | 每账号占用阻塞线程并以 100ms 轮询唤醒；`catch { break; }` 会令 pump 故障静默终止 |
| 重连 | 已有终止错误列表、指数退避、抖动和最大 10 次尝试 | 状态埋在 Login 内；网络判断只依赖网卡；异常分类不统一 |
| Library | Owned games、家庭共享、最近游玩和成就进度已走 Unified Messages | Feature 直接操作 SteamKit2；同账号并发刷新不合并；失败返回 `[]` |
| Wishlist | 直接请求 `api.steampowered.com/IWishlistService` | 缺少限流、重试、熔断、来源和 stale fallback |
| App metadata | Host 中 `SteamAppMetadataService` 先查 `steam_app`，miss 后访问 store appdetails | HTTP 在国内不稳定；缓存无 TTL；网络逻辑仍在 Host |
| User profile | Host 中 `SteamUserService` 访问 `steam-chat.com/miniprofile` | 核心资料依赖不稳定域名；HTTP 失败被笼统记录为 throttled |
| Rich Presence | CM 获取本地化，`ConcurrentDictionary<(appid, language), Task<...>>` 缓存 | 仅内存、无 TTL；与元数据各自实现一套 in-flight 合并；faulted task 不会被驱逐 |
| HTTP | `Download` / `SteamApi` 两个 named client | 只有 timeout、连接池和解压；无 resilience 与依赖隔离 |
| 持久缓存 | `steam_app` 兼作部分元数据缓存；Library/Friends 仅内存 | 重启后离线不可用，无法表达 Fresh/Stale/Expired |
| 网络态 | 只有重连时的 `NetworkInterface.GetIsNetworkAvailable()` | 网卡在线不代表 CM、Store、Web API 或 CDN 可达 |

### 1.3 Phase 2 最应优先消除的三个泄漏

1. **协议泄漏**：Library/Friends/Rich Presence 直接获得 `SteamClient` 或 `CallbackManager`。
2. **失败语义泄漏**：各 Feature 自行 catch 后返回 `null`、`[]` 或 `string.Empty`。
3. **数据源决策泄漏**：Feature 自行决定请求 CM、HTTP 或缓存，无法统一降级。

---

## 2. Phase 2 范围与非目标

### 2.1 必须完成

- 将 `SteamLoginService` 中的会话生命周期与重连拆到 `SteamSessionManager`。
- 使用 SteamKit2 3.4.0 已提供的异步 callback wait API，移除 100ms 忙轮询。
- Feature 不再公开依赖 `SteamClient` / `CallbackManager`；只有 Steam 接入内部适配器可接触它们。
- 建立按能力拆分的 Gateway，以及统一结果、错误分类、数据来源和 freshness 模型。
- 建立 keyed request coalescing，替代多处局部 `ConcurrentDictionary<K, Task<V>>`。
- HTTP 按外部依赖拆分 named client，并加入超时、重试、熔断、并发隔离和请求配额。
- 为 CM 请求建立按账号和 service/operation 分区的有界并发与节流机制。
- 将 app metadata 从 Store-first 迁移为 PICS/CM-first，HTTP 仅兜底。
- 去除用户核心资料对 `steam-chat.com` 的硬依赖；头像图片仍可走 Steam CDN。
- 把 wishlist 访问收进 Gateway；在 CM 方案验证前保留受治理的 HTTP fallback。
- 新增 SQLite 持久资源缓存，支持 Fresh/Stale/Expired、来源和 schema version。
- 建立细粒度依赖健康状态，并派生 `Online / Degraded / Offline` 用户态。
- 给 Library/Friends 等返回值附带来源、最后成功时间和 stale 信息。
- 为状态机、错误分类、缓存、限流、合并、降级和 migration 增加自动化测试。

### 2.2 明确不做

- 不在本阶段实现成就 schema、`schema_hash` 完整流程或成就页面；这些属于 Phase 3。
- 不在本阶段建设或发布 `steam-stat-data` Public Data CDN；只保留可插拔数据源位置。
- 不升级 TFM、SteamKit2、Electron 或 EF Core；依赖升级应使用独立 PR。
- 不把所有 SteamKit2 handler 包装成一套与 SteamKit2 等大的镜像 API。
- 不引入通用 Repository、MediatR、分布式缓存或远程服务。
- 不改变登录、好友、Library 的现有 IPC channel 和主要 wire shape，除非为 freshness/网络态新增向后兼容字段或 endpoint。
- 不把 refresh/access token 写入通用缓存、日志、事件、IPC 或云同步数据。
- 不以 Phase 2 为理由全面重构 Vue 页面；只做网络态和 stale 信息所需的最小 UI。
- 不声称 CM “无限流”或某个 Valve 限额是官方保证。

---

## 3. 对原计划的必要校正

### 3.1 “一个 `ISteamGateway`”改为“一个 Gateway 边界，多组窄能力”

一个包含 Library、好友、用户、PICS、成就、图片和认证全部方法的接口会迅速成为新的 God Object。推荐结构是：

```text
Features/*
  └─ 只依赖窄业务能力
       ├─ ISteamAppCatalogGateway
       ├─ ISteamLibraryGateway
       ├─ ISteamProfileGateway
       └─ ISteamPresenceFeed

Steam/Gateway/Internal
  ├─ Cm adapters
  ├─ HTTP adapters
  ├─ Cache pipeline
  ├─ Request coalescer
  ├─ Rate limiter
  └─ Error classifier
```

如果为了文档或 composition root 需要保留“`ISteamGateway`”名称，可把它视为架构概念，不必创建无业务价值的 marker interface，也不应让 Feature 通过 `gateway.Apps`、`gateway.Library` 形成 service locator 风格访问。

### 3.2 网络态不是一个真实的全局三态

`Online / Degraded / Offline` 适合 UI，但不足以驱动后端决策。后端应保存能力向量：

```text
CM          Available / Unavailable / Connecting / Unknown
Steam Web   Available / Throttled / CircuitOpen / Unavailable / Unknown
Store       Available / Throttled / CircuitOpen / Unavailable / Unknown
CDN         Available / Unavailable / Unknown
PublicData  Available / Unavailable / NotConfigured / Unknown
```

UI 三态由能力向量派生，不能反过来让一个枚举决定所有请求路径。

### 3.3 `NetworkInterface.GetIsNetworkAvailable()` 只能作为提示

它只能说明至少有网卡可用，不能说明 DNS、TLS、代理、CM 或 Steam 域名可达。正确做法是：

- 用真实请求结果被动更新各依赖健康状态；
- 网卡变化事件只用于提前唤醒或暂停重连，不作为成功判据；
- 不增加周期性 ping Steam 域名的健康检查，避免制造流量和误判；
- circuit open、429、DNS 失败和认证失败必须是不同状态。

### 3.4 SteamKit2 3.4.0 已经改变了两项前提

当前本机还原的 SteamKit2 3.4.0 包同时提供 `net8.0` 与 `net10.0` 资产，本仓库选择 `net10.0`。Phase 2 不调整 TFM；未来变更目标框架时必须以实际 NuGet 资产、完整 build/test 和 runtime smoke 为准。该版本已包含：

- `CallbackManager.RunWaitCallbackAsync` / `SteamClient.WaitForCallbackAsync`；
- WebSocket 作为默认启用的 CM protocol；
- `SteamConfiguration.HttpClientFactory` 可服务 WebSocket CM 连接。

因此：

- 应直接用异步 callback pump，不需要保留 100ms polling，也不应改成 `Task.Factory.StartNew(LongRunning)`；
- “国内优化模式 = 强制 WebSocket”不是正确默认设计，因为当前版本已经默认启用；
- 如需显式限制 protocol，只能作为诊断/兼容选项，并经 smoke 验证，不能成为地区标签下的魔法开关。

### 3.5 HTTP resilience 的 rate limiter 不等于时间窗口配额

`AddStandardResilienceHandler` 中的 rate limiter 主要承担并发隔离。它不能替代“每 5 分钟最多 N 次”的 token bucket。Phase 2 应分别处理：

- HTTP resilience pipeline：总超时、单次尝试超时、retry、circuit breaker、并发隔离；
- 请求配额：`System.Threading.RateLimiting` 的 partitioned token bucket/fixed window；
- CM：独立的按账号 + service/operation 分区策略。

### 3.6 Public Data 层不应阻塞 Phase 2

来源优先级可以预留为：

```text
SQLite → Public Data → CM → HTTP
```

但每种资源的实际来源不同，且 Public Data 当前尚不存在。Phase 2 应做到：

- 定义可选 `ISteamPublicDataSource` 边界，未配置时立即返回 miss；或暂不创建，等 Phase 3 出现第一个真实消费者再抽象；
- 不创建空项目、空接口或模拟 CDN；
- 不让 app metadata、Library 或登录重构依赖外部仓库先上线。

---

## 4. 目标架构

### 4.1 控制面与数据面分离

```text
                         ┌─────────────────────────────┐
IPC / Feature use case ─►│ Login / Library / Friends   │
                         └───────┬───────────┬─────────┘
                                 │           │
                     control     │           │ data/query/feed
                                 ▼           ▼
                    ┌────────────────┐   ┌───────────────────────┐
                    │ SessionManager │   │ Capability Gateways   │
                    │ Auth/Reconnect │   │ Apps/Library/Profile  │
                    └───────┬────────┘   └───────────┬───────────┘
                            │                        │
                            └────────┬───────────────┘
                                     ▼
                          ┌──────────────────────────┐
                          │ Steam access internals   │
                          │ CM / HTTP / cache / CDN  │
                          │ coalescing / resilience  │
                          └──────────────────────────┘
```

- **控制面**：认证、连接、登录、重连、登出、会话状态和 callback pump。
- **数据面**：获取 Library、元数据、profile、wishlist、Rich Presence 等资源。
- Feature 不得通过数据面发起登录，也不得通过控制面直接取得 `SteamClient` 后绕过 Gateway。

### 4.2 建议目录

不需要一次性创建所有目录；仅在放入真实类型时创建。

```text
backend/src/SteamStat.Core/
├─ Steam/
│  ├─ Session/
│  │  ├─ ISteamSessionManager.cs
│  │  ├─ SteamSessionManager.cs
│  │  ├─ SteamSessionState.cs
│  │  ├─ SteamConnection.cs                 # internal，唯一持有 client/callbacks
│  │  ├─ SteamReconnectPolicy.cs
│  │  └─ SteamResultClassifier.cs
│  ├─ Gateway/
│  │  ├─ SteamGatewayResult.cs
│  │  ├─ SteamRequestCoalescer.cs
│  │  ├─ SteamRequestScheduler.cs
│  │  ├─ SteamConnectivityMonitor.cs
│  │  └─ Internal/
│  │     ├─ CmAppCatalogSource.cs
│  │     ├─ CmLibrarySource.cs
│  │     ├─ HttpStoreSource.cs
│  │     └─ HttpWishlistSource.cs
│  └─ Cache/
│     ├─ ISteamResourceCacheStore.cs
│     ├─ SteamCacheKey.cs
│     └─ SteamCachePolicy.cs
├─ Features/
│  ├─ Login/
│  │  ├─ SteamLoginService.cs               # 保留现有应用入口，变薄
│  │  ├─ SteamAuthenticator.cs              # 仅在拆分后确有价值时建立
│  │  └─ SteamCredentialStore.cs
│  ├─ Apps/Contracts/ISteamAppCatalogGateway.cs
│  ├─ Library/Contracts/ISteamLibraryGateway.cs
│  ├─ Users/Contracts/ISteamProfileGateway.cs
│  └─ Friends/Contracts/ISteamPresenceFeed.cs

ElectronNet/ElectronNet/
├─ Features/SteamCache/Persistence/
│  ├─ SteamResourceCacheEntry.cs
│  ├─ SteamResourceCacheConfiguration.cs
│  └─ EfSteamResourceCacheStore.cs
└─ Migrations/<new cache migration>
```

当前 `AppDbContext`、实体和 migrations 仍在 Electron Host 程序集中。为避免 Phase 2 同时进行 persistence 项目迁移，建议：

- `ISteamResourceCacheStore` 和稳定 cache model 放 Core；
- EF 实现、entity、configuration 和 migration 暂留 Host；
- 将来若单独建立 `SteamStat.Persistence.Sqlite`，再整体移动实现；
- 不为使用 SQLite 而让 Gateway 依赖 Host 的 `AppDbContext`。

### 4.3 允许依赖

```text
Feature ─► Feature Contracts / Steam Gateway capability
Gateway capability implementation ─► cache port + CM/HTTP source
CM source ─► internal session lease/provider ─► SteamSessionManager
HTTP source ─► named IHttpClientFactory client
EF cache adapter ─► AppDbContext
Host composition root ─► 注册上述全部具体实现
```

禁止：

```text
Feature ─► SteamClient / CallbackManager / SteamUnifiedMessages
Feature ─► HttpClient / IHttpClientFactory
Core Gateway ─► AppDbContext / ElectronNet.Models
SessionManager ─► IPC DTO / Electron API
Cache payload ─► EF tracked entity / SteamKit2 callback object
```

---

## 5. 契约设计

### 5.1 会话状态模型

建议把用户可恢复错误和终止错误直接放进状态，而不是只发字符串事件：

```csharp
public enum SteamSessionState
{
    Disconnected,
    Connecting,
    Authenticating,
    LoggingOn,
    Ready,
    ReconnectWaiting,
    Reconnecting,
    ReauthenticationRequired,
    Failed,
    Stopping
}

public sealed record SteamSessionSnapshot(
    string AccountName,
    SteamSessionState State,
    int ReconnectAttempt,
    DateTimeOffset ChangedAt,
    SteamFailureKind? LastFailure = null);
```

状态应满足：

- 每账号独立；不能用一个全局 `_steamClient` 表示所有账号。
- `Ready` 只有在 CM connected 且 `LoggedOnCallback.Result == OK` 后进入。
- 用户主动 logout 必须禁止后续 reconnect callback 复活旧 session。
- session 使用 generation/instance identity；旧 session 的 disconnect 不能移除新 session。现有 identity-based removal 行为必须保留。
- 所有 transition 由一个入口执行并发布不可变 snapshot。
- 不在持有 `lock` 时执行 `await`、事件发布或外部调用。

### 5.2 Gateway 结果模型

现有 `[]` 同时表示“用户真的没有游戏”和“Steam 请求失败”，必须改为显式结果。示意契约：

```csharp
public enum SteamDataSource
{
    Memory,
    Sqlite,
    PublicData,
    Cm,
    Http
}

public enum SteamFreshness
{
    Fresh,
    Stale,
    Expired
}

public enum SteamFailureKind
{
    Offline,
    AuthenticationRequired,
    Forbidden,
    NotFound,
    RateLimited,
    Transient,
    Timeout,
    Protocol,
    InvalidData,
    Unknown
}

public sealed record SteamGatewayResult<T>(
    bool IsSuccess,
    T? Value,
    SteamDataSource? Source,
    SteamFreshness? Freshness,
    DateTimeOffset? FetchedAt,
    DateTimeOffset? RefreshAfter,
    SteamFailureKind? Failure,
    string? DiagnosticCode = null);
```

约束：

- cancellation 始终抛 `OperationCanceledException`，不能转换为失败结果。
- `DiagnosticCode` 是稳定、非敏感、可本地化映射的 code，不是原始 exception message。
- Gateway 内记录 exception；不得把 exception、HTTP body 或 Steam callback 传给 IPC。
- 成功的空集合是 `IsSuccess=true, Value=[]`。
- 上游失败但返回旧缓存是 `IsSuccess=true, Freshness=Stale/Expired`，并可附带非致命 failure/source diagnostics。
- `Expired` 表示已超过正常使用期限但被资源策略允许在上游失败时继续展示，UI 不得把它伪装成实时数据。
- 完全 miss 且上游失败才是 `IsSuccess=false`。

### 5.3 按能力拆分的接口

示意：

```csharp
public interface ISteamAppCatalogGateway
{
    Task<SteamGatewayResult<SteamAppMetadataSnapshot>> GetAppAsync(
        uint appId,
        string language,
        string? preferredAccountName = null,
        SteamRefreshMode refreshMode = SteamRefreshMode.PreferCache,
        CancellationToken cancellationToken = default);
}

public interface ISteamLibraryGateway
{
    Task<SteamGatewayResult<SteamLibrarySnapshot>> GetLibraryAsync(
        string accountName,
        bool includeFamilyShared,
        SteamRefreshMode refreshMode = SteamRefreshMode.PreferCache,
        CancellationToken cancellationToken = default);
}

public interface ISteamProfileGateway
{
    Task<SteamGatewayResult<SteamProfileSnapshot>> GetProfileAsync(
        string accountName,
        ulong steamId,
        CancellationToken cancellationToken = default);
}
```

接口参数使用领域值，不使用 `SteamClient`、`Player`、`CPlayer_*`、`KeyValue` 或 HTTP URL。App metadata 属于 public scope，`preferredAccountName` 只帮助选择可用 CM session，不参与 cache key；未指定时由 Gateway 选择任一 Ready session。Gateway 内部 adapter 负责协议映射。

### 5.4 Presence 是 feed，不应伪装成普通 request

好友 persona、游戏状态和 Rich Presence 主要来自 callback stream。建议把它建模为：

- `ISteamPresenceFeed`：订阅/取消账号 feed，提供当前不可变 snapshot；
- `ISteamPresenceLocalizationGateway`：按 `(appid, language)` 获取本地化 token；
- session ready 时由 Friends handler 启动 feed；session ended 时释放订阅；
- callback 中不执行长时间数据库或 HTTP 工作，只更新快照并把 I/O 放入可追踪队列。

不要为了“所有东西都叫 Gateway”而把 callback stream 变成轮询 API。

---

## 6. SessionManager 与认证重构

### 6.1 职责切分

#### `SteamLoginService`（保留现有应用入口）

- 接收 credentials / QR / saved token 登录用例。
- 管理 IPC 需要的 guard code/device confirmation 交互。
- 调用 credential store 和 session manager。
- 发布现有 `SteamLoginProgressChanged`，保持 wire compatibility。
- 不创建 `SteamClient`、`CallbackManager`、timer 或 callback loop。

#### `SteamSessionManager`

- 唯一创建、持有、替换和销毁 `SteamConnection`。
- 每账号维护 session state、generation、reconnect budget 和 cancellation。
- 负责 Connect、LogOn、LogOff、callback pump、重连和 shutdown。
- 发布 `SteamSessionReady/Disconnected/Reconnected/Ended` 与新增的 typed state event。
- 对内部 CM source 提供 ready session lease；不向 Feature 暴露 raw client。

#### `SteamCredentialStore`

- 在 Core 中组合现有 `ISteamLoginTokenStore` 与 `ISecretStore`。
- 对 Login 暴露 `Load/Save/Delete/List`，使调用方不再手工 protect/unprotect。
- 数据库仍只保存 DPAPI ciphertext；明文只在认证/LogOn 的最小作用域存在。
- reconnect state 不长期保存明文 refresh token；重连时按需读取并解密。

`SteamAuthenticator` 是否独立成类取决于拆分后的代码量。如果认证交互仍明显独立且可测试，则建立；如果只是几个调用和一个 `IAuthenticator` adapter，不应为了符合原计划机械拆类。

### 6.2 异步 callback pump

SteamKit2 3.4.0 已支持异步等待。目标形态：

```csharp
private async Task PumpCallbacksAsync(
    CallbackManager callbacks,
    Func<Exception, Task> reportFaultAsync,
    CancellationToken stoppingToken)
{
    try
    {
        while (!stoppingToken.IsCancellationRequested)
            await callbacks.RunWaitCallbackAsync(stoppingToken).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
    {
    }
    catch (Exception exception)
    {
        await reportFaultAsync(exception).ConfigureAwait(false);
    }
}
```

实施时以 3.4.0 本地 XML docs/编译器签名为准，并增加一个最小编译测试。要求：

- 不再使用 `Task.Run` 包住永久阻塞循环。
- callback handler 本身保持短小；耗时工作进入受管理队列。
- pump fault 必须立即报告给 session state machine，并被 manager 观察，不能只等到 shutdown 才发现 faulted task。
- `StopAsync` 依次解除 subscription、取消 pump token、断开 client，再有界等待 pump，避免 late callback 进入已停止的 handler。
- 同一个 stop 多次调用必须返回同一 stop task，保持幂等。

### 6.3 SteamConfiguration

由 `SteamSessionManager` 复用一个构建后不可变的 `SteamConfiguration`：

- 多账号共享 CM server list，避免每个 client 独立发现。
- 保持 SteamKit2 3.4.0 默认 WebSocket-enabled protocol 集合。
- 如为 CM WebSocket 提供自定义 HTTP 行为，通过 `WithHttpClientFactory` 按 `HttpClientPurpose` 配置，但要尊重 SteamKit2 对返回 client 的 dispose 约定。
- 不把普通 `IHttpClientFactory.CreateClient()` 返回对象交给会主动 dispose 的代码，除非用不会级联销毁共享 handler 的正确适配方式并有测试。
- protocol、directory fetch 和 connection timeout 通过 typed options 配置，不散落在 Feature。

### 6.4 重连策略

推荐退避采用 bounded exponential backoff + jitter。策略应是纯逻辑，可由 `TimeProvider` 驱动：

```text
Disconnected(unexpected)
  ├─ credential terminal / revoked ─► ReauthenticationRequired
  ├─ app stopping / user logout ────► Disconnected
  └─ transient ─► ReconnectWaiting ─► Reconnecting
                                      ├─ success ─► Ready
                                      ├─ terminal ─► ReauthenticationRequired
                                      └─ budget exhausted ─► Failed
```

分类建议：

| 类别 | 示例 | 行为 |
| --- | --- | --- |
| 终止凭据错误 | `InvalidPassword`、`AccessDenied`、`Expired`、`Revoked`、`InvalidSignature`、账号禁用/锁定 | 立即停止自动重连，提示重新认证 |
| 暂时性 CM/服务错误 | `Busy`、`ServiceUnavailable`、`TryAnotherCM`、连接断开、可识别 timeout | 计入重连预算，退避 + jitter |
| Rate limited | `RateLimitExceeded` 或明确的服务节流结果 | 使用更长 backoff，不立即重放 |
| 本地无网络提示 | 网卡不可用 | 暂停计数并等待网络变化或手动重试 |
| Unknown | 未分类 `EResult`/异常 | 有界重试少量次数，然后 Failed；绝不无限重试 |

注意：

- JWT `exp` 只能作为提前提示，Steam 服务返回才是权威结果。
- 网络恢复可提前唤醒等待，但不能同时启动第二个 reconnect。
- 每账号只允许一个 connect/logon/reconnect 操作；使用 `SemaphoreSlim` 或显式 command loop 串行化。
- 不要在 timer callback 里直接启动不可追踪 async work。推荐由 manager 自有任务循环使用 `Task.Delay(delay, timeProvider, token)`。
- 最大尝试次数耗尽后，用户手动 retry 应创建新一轮预算。

### 6.5 事件兼容

Phase 1 已建立这些事件及消费者，但当前意外断开路径以两个 tracked task 分别发布事件，严格顺序尚未被保证。Phase 2 应把下列顺序定义为确定性目标，并用测试固定事件集合、顺序与 IPC compatibility：

- 首次登录成功：`SteamSessionReady`。
- 意外断开：`SteamSessionDisconnected` → `SteamSessionEnded`。
- 重连成功：`SteamSessionReconnected` → `SteamSessionReady`。
- 主动 logout：只产生一次 `SteamSessionEnded`，且不能安排重连。
- 同账号新 session 替换旧 session：旧实例迟到的 disconnect 不能结束新实例。

---

## 7. Gateway 共享管道

### 7.1 每种资源拥有自己的 source chain

不要把固定顺序硬编码成一个全局泛型函数。不同资源的来源应由 descriptor/policy 明确声明：

| 资源 | 推荐来源顺序 |
| --- | --- |
| App metadata | SQLite →（未来 Public Data）→ PICS/CM → Store HTTP |
| Library | SQLite snapshot → Player/FamilyGroups CM |
| Wishlist | SQLite snapshot → Steam Web HTTP；只有独立 spike 验证后才增加 CM source |
| User core profile | SQLite → SteamFriends/Persona + Levels CM → 保留旧数据 |
| Avatar image | 本地文件 → Steam static CDN |
| Rich Presence localization | SQLite → Community Unified Message |
| Phase 3 achievement schema | SQLite hash/cache → Public Data → Player CM |

每个 source 只负责：

- 调用一个外部协议；
- 把响应映射为稳定 Core snapshot；
- 返回 typed success/failure；
- 不自行决定下一 fallback；
- 不直接更新 UI。

### 7.2 Keyed request coalescing

当前 app metadata 和 Rich Presence 已各自缓存 `Task`；Library 则完全没有合并。应抽出一个通用但小型的 `SteamRequestCoalescer<TKey>`。

正确取消语义：

- 底层共享请求绑定 application/session lifetime，不绑定第一个 caller 的 token。
- 每个 caller 使用 `sharedTask.WaitAsync(callerToken)`，某个 caller 取消只结束自己的等待，不取消其他等待者。
- 第一版不尝试在所有 caller 取消后停止底层请求；共享请求继续到完成、operation timeout 或 owner lifetime 取消。
- 如果后续确需“最后一个等待者取消即停止”，必须增加等待者引用计数和共享 CTS，不能把任一 caller token 直接 link 给底层请求。
- 完成后使用 key + task identity 原子移除，避免旧 continuation 删除同 key 的新任务。
- 失败 task 不得永久留在字典中。
- 后台 stale refresh 也必须经过 coalescer，避免 UI 手动刷新与自动刷新重复请求。

不要创建 linked CTS 到每个 caller 后把它用于共享请求；任何一个 token 取消都会错误终止所有调用者。

### 7.3 错误分类

统一 `SteamResultClassifier` 应同时理解：

- SteamKit2 `EResult`；
- `HttpRequestException.HttpRequestError`、inner socket/DNS/TLS；
- HTTP 408、429、5xx 和其他 4xx；
- resilience timeout、circuit open、rate limiter rejected；
- JSON/KeyValue/protobuf 数据损坏；
- cancellation（必须单独传播）。

分类器只产出稳定类别和 diagnostic code，不决定所有业务 fallback。具体资源 policy 决定：

- `NotFound` 是否允许 negative cache；
- `Forbidden` 是否回退到旧缓存；
- `AuthenticationRequired` 是否请求 session manager 进入重新认证态；
- `Protocol/InvalidData` 是否跳到 HTTP fallback。

### 7.4 可追踪后台工作

stale-while-revalidate、callback 后持久化和事件 publish 都可能产生后台任务。共享规则：

- 必须有 owner；owner 保存任务并在 dispose/stop 时 drain。
- 任务异常必须由 owner 观察并记录。
- shutdown 等待有上限，超时后取消。
- 不使用裸 `_ = SomeAsync()`，除非立即交给统一 tracker。
- 不让 HTTP/DB I/O 阻塞 Steam callback pump。

---

## 8. HTTP 治理

### 8.1 按依赖拆 named client

当前 `SteamApi` 同时访问 Store、Web API 和 `steam-chat.com`，会共享 timeout/circuit，无法正确降级。建议拆为：

```text
SteamStore       store.steampowered.com
SteamWebApi      api.steampowered.com
SteamCdn         avatars.akamai.steamstatic.com / steamstatic CDN
PublicData       未来公共数据源
Download         应用更新或一般文件下载，保持原职责
```

每个 client 设置固定 `BaseAddress`（域名固定时）、User-Agent、Accept、自动解压、连接池生命周期和自己的 resilience pipeline。禁止 Gateway 接受任意 URL 后使用高权限 client。

### 8.2 Resilience 策略

建议添加与当前 Microsoft.Extensions 家族一致的 `Microsoft.Extensions.Http.Resilience`。如果保持现有依赖基线，则使用相同 patch family；如要升级整个家族，使用独立依赖 PR 完成 restore、audit、build 和 tests。

对短小、幂等 JSON GET：

1. 外层请求配额；
2. total timeout；
3. retry；
4. circuit breaker；
5. attempt timeout / concurrency limiter（以实际 API 顺序为准）。

规则：

- 只自动 retry 幂等操作。
- retry `HttpRequestException`、408、429 和部分 5xx；不 retry 普通 4xx。
- 429/503 优先遵守合法 `Retry-After`。
- 使用 exponential backoff + jitter，通常只需少量重试；桌面交互不能因三层 fallback 叠加而等待数分钟。
- circuit 按外部依赖隔离；Store 熔断不能阻止 CDN 图片。
- 不堆叠多个 standard resilience handler。
- timeout 只保留一个权威来源。P2-M3 采用 resilience total/attempt timeout 时，必须同步移除 `AddSteamStatCore.ConfigureClient` 当前的 15/30 秒 `HttpClient.Timeout`，改为 infinite，避免双重 timeout 无法分类；现有 timeout characterization test 应在同一 PR 中有意更新。
- 所有 source API 都必须接收并传递 `CancellationToken`；当前 wishlist `GetAsync` 未传 token，迁移时必须修正。
- 日志不记录 query 中可能出现的 key/token，也不记录 response body。

图片/文件下载不能照搬 JSON API 策略：

- 保留临时文件 + 成功后原子替换；
- 没有 Range/校验机制时不要盲目 retry 大文件；
- 限制并发，校验 content length/type 和最大尺寸；
- 下载失败保留现有文件，不写空文件覆盖。

### 8.3 请求配额与并发隔离

原计划的 `200 req / 5min` 不能当作 Valve 官方 SLA。建议把初始值定义成可测试的 `SteamAccessOptions`，并注明是客户端保护值：

- Store/Web API：按 authority + operation 使用 token bucket，队列必须有界；
- CDN：主要限制并发，不与 Store 共用时间窗口；
- 队列满时快速返回 `RateLimited`，而不是无限等待；
- 记录等待时长和 reject 次数，用真实使用数据调参；
- 不提供允许用户无限提高限额的 UI。

第一版更重要的是 **分区、有界、可配置和可观测**，而不是猜出一个看似精确的数字。可以采用低并发和保守 burst 起步，再根据 1000+ Library/500+ Friends smoke 调整。

### 8.4 代理和“国内网络优化”

建议将设置设计成技术语义，而不是地区语义：

```text
Steam access mode: Automatic（默认） / CM preferred / Offline cache only
HTTP proxy: System（默认） / Disabled / Custom
```

- `Automatic` 本身已是 CM-first。
- Custom proxy URL 禁止 user-info；若未来支持代理凭据，必须放 `ISecretStore`，不能明文写 settings JSON。
- 设置变化应重建相关 handler/client 生命周期，而不是修改正在使用的 `SocketsHttpHandler`。
- SteamKit CM WebSocket 的代理行为要单独 smoke；普通 Steam HTTP 代理成功不代表 CM 成功。
- UI 可以使用“网络兼容性”说明，但不应承诺在所有地区可用。

---

## 9. CM 请求调度与 HTTP → CM 迁移

### 9.1 CM 调度

CM 不使用 `IHttpClientFactory`，但同样需要保护：

- 分区键至少包含 `(account, service/operation)`。
- request/response 类操作使用有界并发；subscription/callback 不消耗 request permit。
- 优先批量 API：成就进度已有 100 app chunk，PICS 应批量请求 app IDs。
- 不对所有非 OK `EResult` 自动 retry；先分类，再由 operation policy 决定。
- session 非 `Ready` 时快速返回 `AuthenticationRequired`/`Offline`，不要在 Gateway 内偷偷触发登录。
- session generation 变化时，旧请求结果不得写入新 session/account 的缓存。
- 对 job callback 设置 operation timeout；超时只取消等待，不假设 SteamKit 已取消服务端工作。

第一版可采用每账号每 service 的低并发限制，而不是武断声明 CM “不限流”或固定每秒 N 次。真实限制必须通过日志指标和 smoke 调整。

### 9.2 App metadata：Store → PICS

迁移目标：

```text
SteamAppMetadataService HTTP fetch
  → ISteamAppCatalogGateway
      → local resource cache
      → SteamApps.PICSGetAccessTokens（需要时）
      → SteamApps.PICSGetProductInfo
      → store appdetails fallback
```

注意事项：

- PICS 结果可能分批 callback，必须等待 complete result，不能只取第一批；整个收集过程必须有 operation timeout，超时后返回 typed transient/timeout failure，不能永久等待。
- 对需要 access token 的 app 先批量请求 token；不要为每个 app 单独 round trip。
- P2-M0 使用公开 app、受限 app 和不存在 app 的脱敏 fixture/受控实测固定 access-token 与结果语义，区分 `NoPermission`/missing token、not found 和 invalid response。
- 解析 `KeyValue` 时只映射当前稳定模型需要的字段，如 name/type/is_free；原始树不进入 Feature/IPC/cache contract。
- PICS access denied、missing token、protocol parse failure 与 app not found 必须区分。
- HTTP fallback 成功后同样写入统一 cache，并标记 `Source=Http`。
- 现有 `steam_app` 是业务投影，统一 cache 是外部资源快照；Gateway 成功后通过现有 `IAppMetadataWriter` 更新投影，不能把 EF entity 当 cache payload。
- 多 app 同步必须 batch + coalesce，避免 Friends 中每个未知 app 都产生独立 PICS 请求。

### 9.3 User profile：移除 `steam-chat.com` 硬依赖

`steam-chat.com/miniprofile` 当前同时提供 persona、level、头像和装饰信息。Phase 2 应拆分需求：

| 数据 | 首选来源 | 失败行为 |
| --- | --- | --- |
| Persona name/state | SteamFriends persona state / callback | 使用本地 VDF/SQLite 旧值 |
| Level | 现有 `SteamLevelsHandler` / CM | 使用旧值或未知 |
| Avatar hash | SteamFriends callback/cache | 由 hash 构造官方 Steam static CDN URL |
| Avatar image | Steam static CDN | 保留旧文件/默认头像 |
| Animated avatar/frame/level class | 非核心装饰数据 | 可保留旧缓存；不要为它阻塞用户同步 |

迁移完成标准不是所有装饰字段都永远有值，而是核心用户资料不再因 `steam-chat.com` 不可达而整体失败。

### 9.4 Wishlist

Phase 2 的承诺是先把当前 HTTP endpoint 收口到 `ISteamLibraryGateway`/独立 source，并获得缓存、限流、熔断和 typed failure；**CM-first 不作为 Phase 2 完成条件**。如果后续开展独立 spike，必须：

1. 确认 SteamKit2 3.4.0 protobuf/service 名称和真实响应。
2. 用脱敏响应 fixture 固定 mapper。
3. 验证分页、隐私设置、空 wishlist 和 rate limit。
4. 通过后再用单独 PR 把顺序改为 CM → HTTP。

在验证完成前，保留受治理的 HTTP source 比手写未经验证的 proto 更可靠。不要让 wishlist 的失败导致整个 owned/family Library 返回空；应返回 Library 成功 + wishlist 子资源 degraded。

### 9.5 已经走 CM 的能力也要收口

Library 的 owned/family/last played/achievement progress 和 Friends 的 Rich Presence 已经使用 CM，但仍需迁移到 Gateway/source adapter，原因是：

- 统一 timeout、错误分类和 session generation；
- 避免 Feature 直接依赖 generated protobuf 类型；
- 统一缓存和离线语义；
- 为 Phase 3 achievement gateway 提供可复用范例。

迁移时不得改变现有 owned/family/wishlist 合并结果；先写 characterization tests，再移动实现。

---

## 10. SQLite 持久缓存

### 10.1 不使用 `IMemoryCache` 替代持久缓存

Phase 2 的关键价值是重启后仍可离线读取。内存层可以作为热点优化，但 SQLite 必须是权威的本地资源 cache。

### 10.2 建议 schema

通用表可以成立，但必须是 **有严格 resource descriptor 和 typed codec 的资源缓存**，不能成为任意 JSON 垃圾桶：

```text
steam_resource_cache
- id                    INTEGER PK
- resource_kind         TEXT NOT NULL
- scope_id              TEXT NOT NULL      # public 或稳定 SteamID；个人资源不用 accountName
- resource_id           TEXT NOT NULL      # appid、library、profile 等
- language              TEXT NOT NULL DEFAULT ''
- variant               TEXT NOT NULL DEFAULT ''
- schema_version        INTEGER NOT NULL
- payload_format        TEXT NOT NULL       # 第一版固定 json-v1
- payload               TEXT NOT NULL       # UTF-8 JSON，限制最大字符/字节数
- source                 TEXT NOT NULL
- etag                   TEXT NULL
- content_hash           TEXT NULL
- fetched_at             INTEGER NOT NULL
- refresh_after          INTEGER NOT NULL
- retain_until           INTEGER NULL
- last_accessed_at       INTEGER NOT NULL
```

唯一索引：

```text
(resource_kind, scope_id, resource_id, language, variant, schema_version)
```

附加要求：

- payload 有最大尺寸；写入前验证。
- codec 按 `resource_kind + schema_version` 注册，禁止任意 `Type` 反序列化。
- 反序列化失败记录 `InvalidData` 并视为 miss；不能令应用启动失败。
- upsert 原子执行；失败不能删除上一份成功值。
- cache 中禁止 token、password、guard data、Authorization、QR secret。
- 个人资源由删除账号/清理数据用例显式清理。
- `last_accessed_at` 不在每次 cache hit 时同步写库；采用采样、批量刷新或仅在已有写操作中更新，避免把读取变成写放大。
- 清理任务按 `retain_until/last_accessed_at` 有界分批，不能每次启动全表扫描。

### 10.3 Fresh / Stale / Expired 语义

建议区分：

- **Fresh**：`now < refresh_after`，直接返回。
- **Stale**：超过 refresh time，但仍允许显示；返回旧值并尝试后台刷新。
- **Expired for normal use**：强制刷新优先；刷新失败时仍可按资源政策 serve stale，并明确标记。
- **Retained**：`retain_until` 只控制清理，不代表 UI 可以伪装成实时数据。

Local-first 产品不应因 TTL 到期立即删除唯一一份离线数据。`refresh_after` 与 `retain_until` 必须分离。

可作为第一版起点的产品策略：

| 资源 | 建议 refresh-after | 建议保留窗口 | 备注 |
| --- | --- | --- | --- |
| Friends snapshot | 30 秒 | 7 天 | stale 必须显示最后更新时间；不能伪装在线状态 |
| Library snapshot | 15 分钟 | 30 天 | 手动刷新走 RequireRefresh |
| Wishlist | 15 分钟 | 30 天 | 子资源失败不清空 Library |
| App metadata | 7 天 | 180 天 | Valve 名称变化低频 |
| Rich Presence localization | 7 天 | 180 天 | 以 appid + language 为 key |
| Profile core fields | 1 小时 | 30 天 | 图片文件有独立缓存策略 |

这些是客户端策略，不是 Steam SLA，应放 typed options 并通过测试固定。

### 10.4 Stale-while-revalidate

默认读取流程：

```text
fresh hit
  └─ 直接返回

stale hit
  ├─ 立即返回 stale snapshot
  └─ 经 coalescer 启动受追踪 refresh

miss / RequireRefresh
  └─ 执行 source chain
       ├─ success → 原子更新 cache → 返回 fresh
       └─ all failed
            ├─ 有 stale → 返回 stale + failure metadata
            └─ 无 stale → 返回 failure
```

手动刷新不能先清空 cache。任何失败都不得把成功缓存覆盖成空数组。stale 后台刷新必须同时经过 coalescer、每资源的 `MinStaleRefreshInterval`/失败退避和 dependency circuit；上游持续失败或 circuit open 时直接返回 stale，不能让每次读取都再次发起 refresh。

### 10.5 Negative cache

只对明确、稳定的 `NotFound` 使用短时 negative cache。禁止缓存：

- 认证失败；
- 403/隐私限制（权限未来可能变化）；
- timeout、DNS、TLS、circuit open；
- 429；
- parser/protocol error；
- cancellation。

---

## 11. 网络降级状态与 UI 语义

### 11.1 依赖健康快照

建议建立不可变状态：

```csharp
public sealed record SteamConnectivitySnapshot(
    DependencyHealth CmTransport,
    DependencyHealth SteamWebApi,
    DependencyHealth Store,
    DependencyHealth Cdn,
    DependencyHealth PublicData,
    DateTimeOffset ChangedAt);
```

`CmTransport` 只表示 CM 目录/传输层的全局摘要；每个账号的 Ready、认证、重连和 rate-limit 状态保留在对应 `SteamSessionSnapshot`，不能把多账号状态折叠成一个全局 CM 值。每个 `DependencyHealth` 至少包含状态、last success、last failure category 和 circuit/rate-limit 信息。更新规则：

- 由真实 operation report success/failure；
- 相同状态不重复发事件；
- 使用 `TimeProvider`；
- 状态有衰减/Unknown 语义，不能一次失败后永久标红；
- 不在 metrics label 中放 accountName、SteamID、appid 等高基数字段。

### 11.2 UI 三态派生

建议定义：

- `Online`：当前功能所需首选上游可用，且最近有成功证据。
- `Degraded`：至少一个关键上游不可用/熔断/节流，但 CM、其他来源或 stale cache 仍能提供功能。
- `Offline`：没有上游可用证据，当前请求只能依赖本地缓存。

这不是单个永久全局值。Library 可能在 CM 正常时 Online，而图片 CDN 暂时 Degraded。全局 badge 可显示最严重摘要，具体页面应显示资源自己的 source/freshness。

### 11.3 最小 UI 交付

Phase 2 只需：

- 全局网络摘要 badge；
- Library/Friends 显示“来自本地缓存，最后更新于 …”；
- `ReauthenticationRequired` 给出重新登录入口；
- 手动刷新时保留旧数据并显示 refresh failure；
- 不把 technical exception message 直接 toast 给用户。

新增事件/查询应先修改 `SteamStat.Contracts.Ipc` descriptor 和 DTO，再运行 generator；禁止手改 `preload.mjs` 或 `ipc.d.ts`。

---

## 12. DI、配置与生命周期

### 12.1 注册归属

`AddSteamStatCore` 应逐步成为 Steam Core 能力的注册入口。当前 Login/Library/Friends 具体类型仍在 `AddSteamStatElectron` 注册；P2-M2 必须把下列 Core-owned registration 迁入 `AddSteamStatCore`：

- session manager、credential coordinator；
- Gateway capability implementations；
- error classifier、coalescer、request scheduler、connectivity monitor；
- HTTP named clients 和 typed options；
- Login/Library/Friends 等 Core service 及其 Core event handler 映射。

Host 的 `AddSteamStatElectron` 保留：

- `AppDbContext` factory 和 EF cache/token adapter；
- Electron IPC、window、tray、updater；
- 本地 VDF/ACF 与现有 Host services（直到它们后续迁移）；
- 把 Host adapter 注入 Core port。

如果 `AddSteamStatCore` 需要 Host 才能提供的 port，注册接口消费者没有问题；最终 `Build()` 的 `ValidateOnBuild` 会验证完整 composition。测试可提供 fake adapter。

### 12.2 生命周期

| 类型 | 生命周期 |
| --- | --- |
| `SteamSessionManager` | Singleton + `IAsyncDisposable`，与 Host 同生命周期 |
| Gateway | Singleton，无 caller 可变状态 |
| Request coalescer/scheduler/connectivity | Singleton + 可释放 |
| EF cache adapter | Singleton，内部每次用 `IDbContextFactory` 建 Context |
| `DbContext` | 每操作一个，不跨 await 链共享到其他任务 |
| HTTP handlers/limiters | 由 DI/HttpClientFactory 管理，不在请求中 new |
| Presence subscription | 每账号 session-owned，session ended 时释放 |

### 12.3 Settings

如增加 access mode/proxy：

- 扩展 `AppSettings`、default factory、两处 merge、typed IPC DTO、validator 和设置 UI。
- 设置 side effect 由 coordinator/controller 应用，不能让 Gateway 每次读取 JSON。
- proxy URI 做 scheme、host、port、userinfo 校验。
- 设置变更与进行中的请求有清晰语义：旧请求完成，新请求使用新 generation/client。

---

## 13. 测试策略

### 13.1 先建立可测试 seam

SteamKit2 的 concrete client/handler 不适合全量 mock。不要包装整个 SteamKit2；只为真实边界建立 seam：

- `ISteamConnectionFactory`：测试 manager 创建/断开 connection。
- internal `ISteamConnection`：提供 connect/logon/callback pump 和 generation，不泄漏给 Feature。
- 每个 source adapter 有小型 transport seam 或纯 mapper。
- `SteamReconnectPolicy`、`SteamResultClassifier`、cache policy、source selection 保持纯逻辑。
- 时间全部来自 `TimeProvider`；测试使用可控 time provider。

真实 Steam 集成测试必须显式 opt-in，不进默认 CI，不包含仓库凭据。

### 13.2 Core 单元测试清单

#### Session / reconnect

- 每个合法 transition 与非法 transition。
- connected 但 logon 未 OK 不能进入 Ready。
- terminal `EResult` 一次进入 `ReauthenticationRequired`，不再安排 timer。
- transient 错误按 backoff 重试，jitter 在边界内。
- 网卡不可用不消耗尝试次数；恢复只唤醒一个 reconnect。
- budget exhausted 进入 Failed；手动 retry 重置预算。
- 主动 logout 不重连。
- 旧 generation disconnect 不影响新 session。
- stop 幂等，解除订阅并有界 drain。
- callback pump cancellation 不记为故障，非 cancellation fault 会转状态。

#### Gateway / source selection

- fresh cache 不调用上游。
- stale cache 立即返回且只触发一次后台 refresh。
- 两个相同 key 并发请求只调用 source 一次。
- 一个 caller 取消不取消共享请求和其他 caller。
- CM 成功不调用 HTTP。
- CM transient/protocol failure 按 resource policy fallback。
- success empty 与 failure 明确区分。
- wishlist failure 不清空 owned/family games。
- session generation 变化后旧响应不写 cache。

#### Error classifier

- 现有 terminal `EResult` 全部表驱动测试。
- transient/rate-limit/unknown 分类。
- HTTP 408/429/5xx、普通 4xx。
- DNS/TLS/socket/timeout/circuit-open/rate-limit rejection。
- cancellation 永不被分类器吞掉。

#### Cache

- key normalization 和 schema version。
- Fresh/Stale/Expired/retain 边界时刻。
- 不同 account/language/variant 不串数据。
- 原子 upsert；写失败保留旧值。
- malformed/oversized payload 安全 miss。
- negative cache 只用于 NotFound。
- cleanup 分批且不删除仍需离线展示的数据。

#### Rate limiting / resilience

- 分区隔离：Store 饱和不影响 CDN。
- queue bounded 和 cancellation。
- 429 使用 Retry-After。
- 普通 400/401/403 不 retry。
- retry 次数、总耗时和 attempt timeout 有界。
- circuit open 后快速失败，恢复后 half-open 探测符合预期。

### 13.3 SQLite / Host 集成测试

- 新 migration 从最早 fixture 升级，既有六张表关键数据不变。
- 临时 migration model diff 的 `Up/Down` 为空后再删除。
- `steam_resource_cache` 唯一索引、字段约束和 upsert。
- DatabaseMigrator 仍先 backup 后 migrate。
- 文件型 SQLite 并发读写不会共享 Context。
- 如启用 WAL，必须补 backup/checkpoint/恢复测试；未完成这些测试前不要顺手启用 WAL。
- SQLite busy/locked 使用 provider timeout 和短事务处理；不要在一个事务中等待网络。

### 13.4 架构测试新增门禁

- `Features/**` 不出现 `SteamClient`、`CallbackManager`、`SteamUnifiedMessages` 和 `IHttpClientFactory`；允许列表只包含 `Steam/Gateway/Internal`、`Steam/Session/Internal` 等明确目录。
- `SteamClient` 构造只允许 `SteamSessionManager`/factory。
- 产品代码不出现 `RunWaitCallbacks(TimeSpan.FromMilliseconds(100))`。
- HTTP Steam 域名只允许 source adapter/HTTP client registration；Feature 不出现 URL。
- cache contract 不引用 EF entity、SteamKit callback 或 IPC DTO。
- `SteamGatewayResult`/session event 不含 secret 字段。
- Core 继续无 Electron、Console、Serilog static、service locator 和 mutable static service state。

不要把架构测试写成精确构造器参数列表，Phase 2 会频繁演进依赖；应测试依赖方向和禁止类型，而不是冻结偶然实现细节。

### 13.5 手工 smoke

至少覆盖：

1. credentials、QR、saved token 三种登录。
2. 同账号重复登录替换旧 session。
3. 多账号同时 Ready、分别断开和重连。
4. 拔网线：停止空转；页面保留 cache 并显示 Offline/stale。
5. 网络恢复：只有一个 reconnect，成功恢复 Friends/Library。
6. 过期/撤销 token：立即进入重新认证，不进行 10 次无效重试。
7. hosts/代理阻断 Store、Web API、`steam-chat.com`：CM Library/Friends 和 PICS metadata 仍工作，状态为 Degraded。
8. CM 不可用但 HTTP/cache 可用：资源按自己的 source policy 降级。
9. 1000+ Library、500+ Friends：请求数量有界，无逐项 HTTP 风暴。
10. 快速关闭应用：session、callback、refresh、DB work 在期限内停止，无未观察异常。
11. 普通/静默启动、窗口关闭、托盘退出保持 Phase 1 smoke 行为。

---

## 14. 推荐实施里程碑

每个里程碑使用独立、可回滚 PR；不要同时改协议来源、数据库 schema、IPC 和 UI。

### P2-M0：固定行为与调用清单

内容：

- 为现有 Library 合并算法、session event 顺序、登录 terminal 分类写 characterization tests。
- 记录所有 CM/HTTP URL、operation、账号作用域、现有 timeout 和失败返回。
- 为 app metadata、wishlist、profile HTTP 注入 fake handler 测试 seam。
- 确认 SteamKit2 3.4.0 `RunWaitCallbackAsync`、PICS API 和 SteamConfiguration 签名能在当前项目编译；用公开、受限和不存在 app 的受控响应固定 PICS complete/access-token 语义。

出口：

- 不改变产品行为。
- 现有 success/empty/failure 行为被测试固定，为后续有意修改提供对照。

#### P2-M0 完成记录（2026-09-07）

M0 已以不改变产品结果的方式完成：只提取了 Library 合并、session event 发布序列、profile HTTP 解析和 PICS 受控响应判定等可测试边界。新增 characterization tests 固定了 Library owned/family/wishlist 合并顺序、登录/reconnect terminal `EResult` 集合、首次登录/断开/重连事件顺序，以及 app metadata、wishlist、profile HTTP 的 success/empty/failure。`SteamKit2` 仍为 `3.4.0`，没有迁移数据源、调整 timeout、增加 retry 或改变 IPC wire shape。

当前 CM 调用清单：

| 能力 | SteamKit2 / CM operation | 账号作用域 | 当前 timeout | 当前失败/空返回 |
| --- | --- | --- | --- | --- |
| 连接与认证 | `SteamClient.Connect`；credentials/QR auth；`SteamUser.LogOn/LogOff` | 每个登录/重连账号独立 client/session | connect 30 秒；saved-token logon 与 reconnect logon 30 秒；credentials/QR polling 无独立 operation timeout，只受登录 CTS 控制 | 登录返回 `SteamLoginResult(false, ErrorCode)`；connect message 映射 `connectionFailed`，timeout 映射 `timeout` |
| 自动重连分类 | `SteamUser.LogOn` 的 `LoggedOnCallback.Result` | 当前 reconnect account | 每次 logon 30 秒；最多 10 次指数退避 | terminal 集合固定为 `InvalidPassword`、`AccessDenied`、`Expired`、`Revoked`、`InvalidSignature`、`AccountDisabled`、`AccountLockedDown`、`AccountLogonDenied`、`AccountLoginDeniedNeedTwoFactor`、`Banned`、`AccountNotFound`；其余结果进入有界重试 |
| Owned Library | `Player.GetOwnedGames`，英文/默认结果后可追加一次本地化语言请求 | request `steamid`，即当前 session 账号 | 无显式 operation timeout/cancellation | 主请求非 `OK`/异常为 owned `[]`；本地化失败保留基础名称 |
| Family Library | `FamilyGroups.GetFamilyGroupForUser`、`FamilyGroups.GetSharedLibraryApps` | request `steamid` + 当前账号 family group | 无显式 operation timeout/cancellation | 非 `OK`、无 group、异常均为 shared `[]`/owners `[]`；不会令 owned games 失败 |
| 最近游玩 | `Player.ClientGetLastPlayedTimes` | 隐式为当前认证账号 | 无显式 operation timeout/cancellation | 非 `OK`/异常为 `[]` map；shared app 回退 response 自带 `rt_last_played`，playtime 为 0 |
| 成就进度 | `Player.GetAchievementsProgress`，每 100 app 一批 | request `steamid`，当前账号 Library | 无显式 operation timeout/cancellation | 单批非 `OK` 跳过，异常停止后续 enrichment；既有 game 保留且进度字段为默认值 |
| Friends/persona | `SteamFriends` callback state、`RequestFriendInfo`、`SetPersonaState` | 当前 session；friend 请求按目标 SteamID | fire-and-forget，无 operation timeout | session/handler 缺失或任意异常返回 `null`/`false`；缓存读取保持既有行为 |
| Rich Presence | `EMsg.ClientRichPresenceRequest`；`Community.GetAppRichPresenceLocalization#1` | 当前 session；目标 friend SteamID；localization key 为 `(appid, language)` | 无显式 operation timeout；只受 resolver lifetime 控制 | localization 非 `OK`/异常返回空 token map，展示回退原始 status/空字符串 |
| Steam Levels | `EMsg.ClientFSGetFriendsSteamLevels` | 当前 session；目标 account IDs | fire-and-forget，无 operation timeout | 缺 handler 或响应解析异常不更新 level，保留缓存/default |
| PICS（M0 编译契约，尚未接产品流） | `SteamApps.PICSGetAccessTokens(IEnumerable<uint>, IEnumerable<uint>)`；`PICSGetProductInfo(IEnumerable<PICSRequest>, IEnumerable<PICSRequest>, bool)` | 计划按当前 session/account；app ID 批量 | 尚无产品调用；SteamKit2 `AsyncJob.Timeout` 默认 10 秒，M4 必须显式制定 operation timeout | 受控语义固定：token map 命中（包括 public app 的合法 token `0`）为 success；denied set 为 access denied；两者均无为 incomplete；product 仅在 `ResultSet.Complete=true`、`Failed=false` 且所有 callback `ResponsePending=false` 时 complete；complete + `MissingToken` 为 access denied；complete + `UnknownApps` 为 not found |

当前 HTTP 调用清单：

| URL / operation | named client | 账号作用域 | 当前 timeout | 当前失败/空返回 |
| --- | --- | --- | --- | --- |
| `GET https://api.steampowered.com/IWishlistService/GetWishlist/v1/?steamid={steamId}` | `SteamApi` | 显式 SteamID；每账号 | `HttpClient.Timeout=15s`；当前调用未传 caller cancellation | 非 2xx、JSON 缺失/畸形、异常均为 wishlist `[]`；owned/family games 保留，只是不标 wishlist/不追加 wishlist-only app |
| `GET https://store.steampowered.com/api/appdetails?appids={appId}&filters=basic` | `SteamApi` | app 全局，不含账号 | `HttpClient.Timeout=15s`；受 service lifetime CTS 控制 | 非 2xx、`success=false`、缺 data/name、异常均返回 `null`；不写 `steam_app` |
| `GET https://steam-chat.com/miniprofile/{accountId}/json` | `SteamApi` | 本地 Steam user/account ID | `HttpClient.Timeout=15s`；传 caller cancellation | 非 2xx 抛 `HttpRequestException` 后由同步边界记录并保留旧 profile；JSON null 不更新；其他异常同样不更新 |
| `GET https://avatars.akamai.steamstatic.com/{hash}[_{size}].jpg` 及 miniprofile 返回的 avatar/frame/animated URL | `Download` | 默认头像为全局；其余按 Steam user | `HttpClient.Timeout=30s`；传 caller cancellation | 无效 URL 返回 `null`；HTTP/IO 等失败返回 `string.Empty`，调用方保留旧文件字段；caller cancellation 继续传播 |

编译契约测试直接以强类型引用 `CallbackManager.RunWaitCallbackAsync(CancellationToken)`、`SteamClient.WaitForCallbackAsync(CancellationToken)`、`AsyncJob<PICSTokensCallback>`、`AsyncJobMultiple<PICSProductInfoCallback>.ResultSet`、`SteamApps.PICSRequest(uint, ulong)` 和 `SteamConfiguration.Create(...WithHttpClientFactory(HttpClientPurpose => HttpClient))`。PICS fixture 不访问真实账号或网络：公开 app 使用 app 730、合法 token `0` 和脱敏受控批次；受限 app 和不存在 app 使用合成 ID，分别固定 denied/`MissingToken` 与 complete `UnknownApps` 语义；`Complete=false`、`Failed=true` 或任一 `ResponsePending=true` 均固定为 incomplete。这样后续 M1/M4 可以有意更新 typed result，而不会把 incomplete、access denied 和 not found 再次合并为空结果。

验证基线：完整 `SteamStat.slnx` 为 142/142（`SteamStat.Core.Tests` 37、`SteamStat.Architecture.Tests` 27、`ElectronNet.Tests` 78）。

### P2-M1：结果模型、错误分类与缓存端口

内容：

- 新增 Gateway result/failure/source/freshness 类型。
- 新增 `SteamResultClassifier`、resource policy 和 coalescer。
- 新增 `ISteamResourceCacheStore` + Host EF adapter + migration。
- 先用 app metadata 或 Rich Presence localization 作为第一个 cache consumer。

出口：

- cache 重启后可读。
- failure 不再等同 success empty。
- migration/backup/旧库 fixture 全部通过。

#### P2-M1 完成记录（2026-09-08）

M1 已新增 `SteamGatewayResult<T>`、`SteamDataSource`、`SteamFreshness`、`SteamFailureKind` 和 `SteamRefreshMode`，并以稳定、非敏感 diagnostic code 明确区分成功空值、上游失败及带 failure metadata 的 stale/expired 成功。`SteamResultClassifier` 已覆盖当前登录 terminal/transient/rate-limit/timeout `EResult`、HTTP 408/401/403/404/429/5xx/其他 4xx、DNS/socket/TLS、JSON/格式损坏，以及 resilience timeout/circuit-open/rate-limiter rejection 的稳定分类；caller cancellation 继续抛出 `OperationCanceledException`。

新增的 `SteamRequestCoalescer<TKey>` 使用 key + result type 合并共享请求；底层 operation 只绑定 owner lifetime，每个 caller 通过 `WaitAsync(callerToken)` 独立取消等待。完成、失败和取消的 task 均通过 key + lazy task identity 原子移除，避免 caller cancellation 终止其他等待者或 faulted task 永久驻留。

Core 中新增规范化 `SteamCacheKey`、`SteamCachePolicy`、`SteamResourceCacheEntry` 和 `ISteamResourceCacheStore`。App metadata 策略采用 7 天 refresh、30 天 stale/expired 分界、180 天 retain、64 KiB typed JSON payload 上限；只有 `NotFound` 可写入 1 小时 typed negative cache，其他 failure 和 cancellation 均不缓存。Host 新增 `EfSteamResourceCacheStore`，每次操作创建独立 `AppDbContext`，使用 SQLite `ON CONFLICT` 原子 upsert，通用 adapter 另设 1 MiB 防御上限，并提供精确删除和最多 1000 条的 retain cleanup；读取不为更新 `last_accessed_at` 产生同步写放大。

第一个 consumer 为 app metadata：新增 `ISteamAppCatalogGateway` 与 `SteamAppMetadataSnapshot`，当前 source chain 为 SQLite → Store HTTP；PICS-first 仍留到 M4。fresh 命中不访问上游，stale 命中立即返回并经 coalescer 启动受追踪刷新，`RequireRefresh` 失败时保留旧值并附带 failure，完全 miss 才返回 typed failure。`SteamAppMetadataService` 保留现有 `IAppNameResolver`/`IAppMetadataWriter` 和 IPC 行为，只负责兼容入口及 `steam_app` 业务投影；资源快照不复用 EF entity。已验证 gateway 重建后仍能读取 cache、畸形/过大 payload 安全 miss、Store `success=false` 为 `NotFound` 而 HTTP 503 为 `Transient`，失败不会覆盖旧成功值。

Host migration `20260908085358_AddSteamResourceCache` 新增 `steam_resource_cache`，包含 schema version、payload format、source、fetched/refresh/retain/last-access 时间及 `(resource_kind, scope_id, resource_id, language, variant, schema_version)` 唯一索引。最早 `20260118024506_Initial` fixture 已通过 backup-first 升级且既有关键数据保持不变；文件型 SQLite adapter 重建读取、原子 upsert、key 维度隔离、过大写入保留旧值和有界 cleanup 均已覆盖。临时 `VerifyM1Model` migration 的 `Up/Down` 均为空，验证后已移除并还原 model snapshot。

验证结果：`SteamStat.Core.Tests` 66、`SteamStat.Architecture.Tests` 29、`ElectronNet.Tests` 83，完整解决方案共 178/178；`dotnet restore/build`、IPC generator check、`pnpm run lint:ci`、`pnpm run build` 和 NuGet transitive vulnerability audit 全部通过。M1 未改变 IPC wire shape、SteamKit2 版本、现有 15/30 秒 HTTP timeout，也未提前实施 M2 session 拆分、M3 resilience/scheduler 或 M4 PICS 数据源迁移。

### P2-M2：SessionManager 拆分

内容：

- 提取 connection/session、状态机和 reconnect policy。
- Login 变为薄用例编排。
- 使用 async callback pump。
- credential protection 统一收口。
- 维持现有 event/IPC compatibility。

出口：

- `SteamLoginService` 不再持有 `SteamClient`、CallbackManager、timer、session dictionary 或 reconnect state。
- terminal token、断网暂停、恢复重连、多账号和 shutdown 测试通过。

#### P2-M2 完成记录（2026-09-11）

M2 已新增 `ISteamSessionManager`、`SteamSessionManager`、`SteamConnection`、`SteamSessionStateMachine` 与 `SteamReconnectPolicy`。`SteamConnection` 是产品代码中唯一构造并持有 `SteamClient`/`CallbackManager` 的位置，并复用一份构建后不可变的默认 `SteamConfiguration`；callback pump 已改为 `RunWaitCallbackAsync(CancellationToken)`，不再使用 `Task.Run` 包裹永久阻塞循环或 100 ms polling。`StopAsync` 先解除 subscription，再取消 pump、断开 client 并有界等待，且重复调用返回同一 stop task；非 cancellation pump fault 会立即交给 manager 的受追踪工作处理。

`SteamSessionManager` 现统一负责连接、LogOn/LogOff、session 安装和替换、每账号 state/generation/reconnect budget/cancellation、事件顺序及 shutdown。每账号 `CommandGate` 串行化 connect/logon/reconnect/logout，同账号新 session 通过 connection identity 与 epoch 隔离旧 generation 的迟到 disconnect/reconnect；意外断开按 `SteamSessionDisconnected` → `SteamSessionEnded` 发布，重连成功按现有 `userReconnected` progress → `SteamSessionReconnected` → `SteamSessionReady` 发布，主动 logout 只发布一次 `SteamSessionEnded`。重连策略采用最多 10 次的 bounded exponential backoff + 0.8–1.2 jitter，rate limit 使用更长 delay；本地无网络只暂停等待并不消耗预算，恢复只唤醒该账号唯一的 reconnect loop，terminal `EResult` 直接进入 `ReauthenticationRequired`。

凭据保护已统一收口到 `SteamCredentialStore`：数据库仍只接收 `ISecretStore.Protect` 后的 access/refresh token 与 guard data；manager 的 reconnect state 只保留账号标识，重连时按需读取并解密，非持久 session 也只缓存受保护文本。`SteamLoginService` 已缩减为 credentials/QR/saved-token、guard/device confirmation 与现有 progress IPC 的薄用例编排，不再实现 `ISteamSessionAccessor`，也不再持有 `SteamClient`、`CallbackManager`、timer、session dictionary 或 reconnect state。Host 的 `ISteamSessionAccessor` 现在映射到 `SteamSessionManager`，现有 IPC descriptor/channel/payload 未改变；session replacement/reconnect 时 Friends callback 会针对新 connection 重新绑定。

新增 M2 architecture/Core tests 固定 Login ownership 边界、session event 无 secret 字段、合法/非法状态转换、terminal token、不联网暂停与恢复单次重连、断开/结束及重连/Ready 顺序、多账号隔离、旧 generation、主动 logout、凭据保护、callback pump cancellation、connection stop 和 manager shutdown 幂等。版本已提升为 `1.4.0-M2`。验证结果：`SteamStat.Core.Tests` 73、`SteamStat.Architecture.Tests` 31、`ElectronNet.Tests` 83，完整解决方案共 187/187；`dotnet restore/build/test`、IPC generator check、`pnpm run lint:ci`、`pnpm run build` 和 NuGet transitive vulnerability audit 全部通过。M2 未改变 SteamKit2 3.4.0、现有 HTTP timeout/cache/gateway 行为，也未提前实施 M3 resilience/CM scheduler 或 M4 source 迁移。

### P2-M3：HTTP resilience 与 CM scheduler

内容：

- 拆 named client，并让所有 source 显式传递 `CancellationToken`。
- 移除旧 15/30 秒 `HttpClient.Timeout`，由 HTTP resilience total/attempt timeout 统一负责。
- 加入 HTTP resilience 和分区请求配额。
- 建立 CM operation scheduler。
- 接入 dependency health reporting 和结构化观测。

出口：

- Store/Web API/CDN 的 circuit、quota 和 timeout 互不影响。
- 同 key/同账号批量请求有界且可取消。
- 没有双重 timeout 和重复 resilience handler。

### P2-M4：数据源迁移

建议顺序：

1. App metadata：PICS-first + Store fallback。
2. User profile：Persona/Levels/Avatar CDN，去除 `steam-chat.com` 硬依赖。
3. Wishlist：完成 HTTP source 收口；CM spike/CM-first 不是 Phase 2 阻塞项。
4. Library owned/family/progress 移入 CM source adapter。
5. Rich Presence localization 接入持久 cache。

出口：

- Feature 不含 Steam HTTP URL。
- Feature 不直接操作 generated protobuf/SteamKit handler。
- 阻断 HTTP 域名时核心 Steam 能力仍可用。

### P2-M5：Library/Friends 持久 snapshot 与降级 UI

内容：

- Library/Friends cache 持久化。
- IPC 增加 source/freshness/last successful update 或独立 resource status endpoint。
- 全局 connectivity summary 和重新认证提示。
- 页面刷新保留旧数据。

出口：

- 重启后断网仍能展示 Library/Friends 快照。
- UI 明确显示 stale 和最后更新时间。
- 不再以空白页表示网络错误。

### P2-M6：收口与硬化

内容：

- 删除旧 HTTP/局部 task cache/session accessor 旁路。
- 增加架构门禁和完整 smoke。
- 更新 `docs/ARCHITECTURE.md`、`CONTRIBUTING.md`、smoke checklist。
- 做 package audit、Release build 和安装包 smoke。

出口：

- 下文 Definition of Done 全部满足。

---

## 15. 可观测性

Phase 2 首先使用现有 `ILogger<T>`；可同时使用 BCL `ActivitySource`/`Meter`，不必立刻引入远程 telemetry 或 OpenTelemetry exporter。

建议日志字段：

```text
Operation
Dependency
Transport=CM|HTTP|Cache
ResourceKind
Source
Freshness
Attempt
ElapsedMs
FailureKind
DiagnosticCode
SessionGeneration
```

建议低基数 metrics：

```text
steam.gateway.requests
steam.gateway.failures
steam.gateway.retries
steam.cache.hits
steam.cache.misses
steam.cache.stale_served
steam.rate_limit.rejected
steam.rate_limit.wait_duration
steam.session.transitions
steam.session.reconnect_attempts
```

约束：

- metrics 不包含 accountName、SteamID、appid、URL 等高基数/个人标签。
- 日志不包含 password、token、guard data、Authorization、QR URL/secret、proxy password。
- 不记录整个 PICS KeyValue、HTTP body、protobuf 或 Library payload。
- retry 每次可记 Debug，最终失败记 Warning/Error，避免日志风暴。
- cache hit 默认 Debug/metric，不逐条 Information。

---

## 16. 常见错误与禁止做法

- 创建一个包含所有 Steam 功能的 50 方法 `ISteamGateway`。
- Feature 为了“特殊情况”继续取得 raw `SteamClient`。
- catch `Exception` 后返回 `[]`，让 UI 无法区分失败。
- 在 refresh 前删除成功 cache。
- 把第一个 caller 的 cancellation token 绑定给共享 in-flight request。
- 把 faulted `Task` 永久缓存。
- 使用无界 `Task.WhenAll` 刷新所有账号/所有 app。
- 在 Steam callback 中同步 `SaveChanges()`、下载图片或调用 HTTP。
- 把 HTTP retry、业务 retry 和 UI retry 叠加，形成请求放大。
- 对非幂等操作自动 retry。
- Store、Web API 和 CDN 共用同一个 circuit breaker。
- 用 `NetworkInterface.GetIsNetworkAvailable()` 宣布系统 Online。
- 每隔几秒 ping Steam 域名做健康检查。
- 把 WebSocket 包装成“国内模式”的唯一开关；SteamKit2 3.4.0 已默认启用。
- 未验证 service/proto 就手写 wishlist Unified Message。
- 在 Phase 2 顺手建设 Public Data CDN 或成就 schema。
- 将个人 Library/Friends cache 当作公共数据上传。
- 为 SQLite cache 创建 `IGenericRepository<T>`。
- 开启 WAL 却不更新在线备份、恢复和打包测试。
- 为通过 CI 删除架构测试、关闭 audit 或隐藏 warning。

---

## 17. Definition of Done

### 架构

- [ ] Steam Session 子系统是唯一允许创建/持有 `SteamClient` 的边界；内部 factory 只负责构造，所有权归 `SteamSessionManager`。
- [ ] Feature 不直接引用 `SteamClient`、`CallbackManager`、Unified Message protobuf 或 `IHttpClientFactory`。
- [ ] Gateway 按能力拆分，Feature 只注入需要的接口。
- [ ] Core 继续不引用 Electron、Host 和 Windows 实现。
- [ ] token/secret 未进入 cache、event、IPC、log 或 metrics。

### 会话

- [ ] callback pump 使用异步 wait，无 100ms 忙轮询。
- [ ] 每账号状态机、generation、重连预算和 shutdown 可测试。
- [ ] terminal token 立即停止重连并进入重新认证态。
- [ ] 暂时性错误有 bounded backoff + jitter；unknown 不无限重试。
- [ ] 主动 logout、session replacement 和 stop 均幂等且无迟到 callback 竞态。

### Gateway / 网络

- [ ] success empty、failure 和 stale success 明确区分。
- [ ] keyed coalescing、HTTP quota/resilience 和 CM scheduler 已统一。
- [ ] Store/Web API/CDN 的 timeout、circuit 和 limiter 隔离。
- [ ] app metadata 为 PICS-first；用户核心资料不硬依赖 `steam-chat.com`。
- [ ] wishlist 失败不会使完整 Library 失败。
- [ ] 依赖健康向量可派生 Online/Degraded/Offline，无主动 ping 风暴。

### 缓存

- [ ] SQLite cache 有 schema version、source、fetched/refresh/retain 时间。
- [ ] 重启断网仍可读取 Library/Friends 等已成功快照。
- [ ] stale 数据有最后更新时间，失败不覆盖旧缓存。
- [ ] migration 在最早 fixture、现有 schema 和 backup 流程上验证。
- [ ] cache payload 有类型白名单、大小限制和损坏容错。

### 测试与交付

- [ ] Core tests 覆盖状态机、分类器、coalescer、cache、limiter 和 source fallback。
- [ ] Architecture tests 阻止 raw SteamKit/HTTP 重新泄漏到 Feature。
- [ ] `dotnet build`、`dotnet test`、generator check、前端 lint/build、NuGet audit 全部通过。
- [ ] 断网、HTTP 全挂、过期 token、多账号、1000+ Library、500+ Friends 和 shutdown smoke 通过。
- [ ] `docs/ARCHITECTURE.md`、`CONTRIBUTING.md`、smoke checklist 与实现同步更新。
- [ ] Phase 结束时可生成并运行 Windows 安装包。

---

## 18. 验证命令

从仓库根目录执行：

```bash
pnpm run lint:ci
pnpm run build

dotnet restore SteamStat.slnx -p:ElectronSkipExecCommands=true
dotnet build SteamStat.slnx -c Debug --no-restore -p:ElectronSkipExecCommands=true
dotnet test SteamStat.slnx -c Debug --no-build -p:ElectronSkipExecCommands=true

dotnet run --project tools/GenerateIpcContracts -- --check
dotnet list ElectronNet/ElectronNet/ElectronNet.csproj package --vulnerable --include-transitive
```

Core 快速反馈：

```bash
dotnet test backend/tests/SteamStat.Core.Tests/SteamStat.Core.Tests.csproj -c Debug
dotnet test backend/tests/SteamStat.Architecture.Tests/SteamStat.Architecture.Tests.csproj -c Debug -p:ElectronSkipExecCommands=true
```

涉及 migration 时还要运行现有 database migration/schema/backup tests，并使用 fixture/copy；不得对真实用户数据库试迁移。Release 前执行：

```bash
pnpm run build:win
```

并记录 [Smoke 清单](./smoke-checklist.md) 中与登录、IPC、Steam 功能、网络和关闭相关的项目。

---

## 19. 外部技术依据

- [.NET HTTP resilience](https://learn.microsoft.com/en-us/dotnet/core/resilience/http-resilience)：standard handler 的 timeout、retry、circuit breaker 和 rate limiter 组合，以及不要重复堆叠 handler 的建议。
- [.NET client-side rate limiting](https://learn.microsoft.com/en-us/dotnet/core/extensions/http-ratelimiter)：`System.Threading.RateLimiting` 与 HTTP handler 的组合方式。
- [Microsoft.Data.Sqlite locking, retries and timeouts](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/database-errors)：并发连接、busy/locked 自动重试和 command timeout。
- [SteamKit2 3.4.0 release](https://github.com/SteamRE/SteamKit/releases/tag/3.4.0)：当前锁定版本与 breaking changes。
- [SteamKit changes](https://github.com/SteamRE/SteamKit/blob/master/SteamKit2/SteamKit2/changes.txt)：异步 callback wait、WebSocket default-enabled 和 configuration HTTP factory。
- [SteamApps / PICS source](https://github.com/SteamRE/SteamKit/blob/master/SteamKit2/SteamKit2/Steam/Handlers/SteamApps/SteamApps.cs)：`PICSRequest`、access token 和 product info API。
- [SteamConfiguration source](https://github.com/SteamRE/SteamKit/blob/master/SteamKit2/SteamKit2/Steam/SteamClient/Configuration/SteamConfiguration.cs)：protocol、server list 与 `HttpClientPurpose`。

SteamKit2 的 CM/Unified Message 属于非官方协议，最终实现必须以仓库锁定版本的编译签名、真实脱敏 fixture 和 smoke 结果为准，不能仅依赖在线 master 文档。
