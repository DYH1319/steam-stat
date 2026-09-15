# Steam Stat Phase 3：成就垂直切片实施指南

> 状态：实施指导稿
> 基线分支：`develop`
> 基线提交：`50dd70e`（2026-09-14 审查时）
> 上位规划：[plan-260823.md](./plan-260823.md)
> 前置阶段：[Phase 2 实施指南](./phase-2-implementation-guide.md)
> 相关文档：[架构说明](../ARCHITECTURE.md)、[贡献指南](../../CONTRIBUTING.md)、[Smoke 清单](./smoke-checklist.md)

---

## 0. 文档目的与结论先行

Phase 3 的目标不是“再加一个成就页面”，而是用一个规模可控、数据边界清晰的功能，验证 Phase 0–2 建立的架构能否真正支持端到端交付：

```text
Steam CM
  → typed source
  → Gateway / cache / failure semantics
  → Feature use case
  → generated IPC contract
  → Host adapter
  → typed renderer adapter
  → Pinia / async resource
  → Vue page
```

本阶段应交付以下结果：

1. **成就成为第一个完整的新架构垂直切片**，而不是继续把逻辑塞进 Library。
2. **公共 schema 与个人进度严格分离**：前者可共享，后者只能按 SteamID 本地保存。
3. **`schema_hash` 真正用于低成本再验证**：缓存新鲜时不访问 CM；缓存过期时先取 hash，变化后才拉完整 schema。
4. **页面按需加载**：初始列表只使用批量摘要，用户打开某个游戏时才获取该游戏的完整 schema 和解锁详情，禁止对 1000 个游戏做 N+1 预取。
5. **IPC、异步状态和 Steam 共享状态各有唯一入口**：新增 `useIpc.ts`、`useAsyncResource.ts`、`store/modules/steam.ts`。
6. **Friends / Library 回填为相同的页面调用形态**，并移除当前 Library 刷新可能触发的重复请求。
7. 每个里程碑结束时仍能构建、测试、启动和发版。

建议用一句话约束实现：

> **先把一个资源从协议到 UI 做完整，再抽象已被至少两个切片验证的共性；不要先建设“垂直切片框架”。**

---

## 1. 当前仓库基线审计

### 1.1 Phase 0–2 已提供的地基

当前 `develop` 已具备 Phase 3 所需的主要后端基础，不应重复建设：

- 根目录 `SteamStat.slnx` 是唯一 solution。
- `SteamStat.Core` 不引用 Electron、Host 或 Windows 实现。
- Generic Host、DI、`TimeProvider`、`ILogger<T>`、Serilog 和可等待关闭流程已落地。
- `IEventBus` 已替代业务层直接调用 `Electron.IpcMain.Send`。
- IPC descriptor 以 `SteamStat.Contracts` 为单一来源，并生成 preload、`ipc.d.ts` 和 snapshot。
- SessionManager、错误分类、CM scheduler、HTTP resilience、请求配额、coalescer、依赖健康状态已完成。
- `steam_resource_cache` 已支持稳定 key、schema version、source、freshness 时间、`ContentHash`、payload 大小限制和原子 upsert。
- Library / Friends 已能持久化快照，并在重启断网后显示 stale 数据。
- App metadata 已是 SQLite → PICS/CM → Store fallback。
- Phase 2 完成记录为完整 solution 215/215，前端 lint/build、IPC generator、NuGet audit 和 Windows 打包通过。

因此，Phase 3 应主要**消费并验证现有基础设施**，而不是再次改造 solution、SessionManager、日志体系或数据库上下文生命周期。

### 1.2 当前与成就有关的实际实现

仓库目前只有“游戏级成就完成摘要”，还没有独立成就功能：

- `CmLibrarySource.ApplyAchievementsProgressAsync` 调用 `Player.GetAchievementsProgress`，每 100 个 app 一批。
- `SteamLibraryGameSnapshot`、`SteamOwnedGame` 和 IPC DTO 只保存 `total / unlocked / percentage`。
- `library.vue` 可以按完成度排序并展示进度条。
- 尚无 `Features/Achievements`、成就 Gateway、schema cache、成就 IPC、路由或页面。
- 尚无个人逐项解锁状态、解锁时间、隐藏成就、分组或本地化描述。
- `schema_hash` 尚未进入产品代码。

这意味着 Phase 3 不能把现有 Library 进度条误认为“成就功能已完成一半”。现有数据只适合作为游戏列表的轻量摘要。

### 1.3 当前前端的真实状态

原计划中列出的三个前端基础设施均不存在：

- 没有 `src/composables/` 目录和 `useIpc.ts`。
- 没有 `useAsyncResource.ts`。
- 没有 `src/store/modules/steam.ts`。
- 7 个 Steam 页面均直接取得 `window.electron`。
- Library、Login、User 分别拉取账号数据；共享 Steam 账号状态尚未收口。
- 多个页面各自管理 loading、error toast、刷新和最后更新时间。
- `package.json` 没有前端 test script，也没有直接安装 Vitest、Vue Test Utils 或 DOM 测试环境。

此外，当前 `library.vue` 在手动同步时先调用 `steamLibrarySyncForAllUsers()`，随后调用 `steamLibraryGetForAllUsers()`；而后者在后端会再次刷新所有 Ready session，因此一次点击可能产生两轮 Library/achievement-progress CM 请求。Phase 3 回填 Library 时必须消除这个请求放大点。

### 1.4 当前缓存能力与限制

现有缓存足以承载第一版 achievement schema，但有两个必须显式处理的限制：

1. `SteamCacheKey` 已有 `resourceKind / scopeId / resourceId / language / variant / schemaVersion`，缓存 entry 另有 `ContentHash`。不需要新建一张 schema 表。
2. `EfSteamResourceCacheStore` 当前对所有 payload 设置 **1 MiB** 硬上限，并只接受 `json-v1`。实施前必须用受控 fixture 测量真实大 schema；若超过上限，应同时调整 Core policy、Host adapter 和测试，不能只改一处，也不能静默不缓存。

`SteamFeatureSnapshotStore` 目前专门处理 Library/Friends，并在内部按 resource kind 分支和引用两个 Feature 的具体模型。Achievement 不应继续扩张这个类；本切片应由自己的 Gateway 直接使用通用 `ISteamResourceCacheStore`，并在返回结果中携带 source/freshness/last-success 信息。

### 1.5 基线差距汇总

| 领域 | 已有 | Phase 3 缺口 |
| --- | --- | --- |
| 协议 | 批量游戏级完成摘要 | schema hash/full schema、逐项解锁状态与时间 |
| Gateway | Library 内部调用进度协议 | 独立 schema/progress capability ports |
| 缓存 | 通用 SQLite resource cache | achievement key、policy、codec、hash revalidation |
| Feature | Library 展示三个汇总字段 | 独立查询用例、schema/progress 合并规则 |
| IPC | Library DTO | achievement 请求、结果 envelope 和输入验证 |
| Renderer | 生成的全局 `Window.electron` 类型 | 可替换的 typed adapter、统一异步资源、Steam store |
| UI | Library 进度条 | overview、详情、stale/error/empty 状态、按需加载 |
| 测试 | 后端三层测试 | protocol fixture、cache/hash、Feature merge、前端 composable/store 测试 |
| Public Data | `SteamDataSource.PublicData` 与 health 占位 | 实际 source/repository 尚不存在，不应阻塞本地切片 |

---

## 2. Phase 3 范围与非目标

### 2.1 必须完成

- 重新验证 SteamKit2 3.4.0 的成就协议签名和响应字段，并将签名固定在 compile-contract test 中。
- 建立公共 schema 与个人 progress/unlock 的稳定 Core snapshot，不让 generated protobuf 离开 `Steam/Gateway/Internal`。
- 实现成就 schema 的 SQLite → hash-only revalidation → full CM 流程。
- 实现按账号和 app 获取个人成就进度；逐项解锁数据必须能与 schema 使用稳定键合并。
- 复用 `SteamCmOperationScheduler`、`SteamRequestCoalescer<SteamCacheKey>`、`SteamResultClassifier` 和 connectivity reporting。
- 提供显式 success-empty、failure、stale-success 语义；失败不得伪装成“该游戏没有成就”。
- 建立 `Features/Achievements` 用例，处理 schema/progress 合并、部分降级和排序所需数据。
- 新增由 C# Contracts 生成的 typed IPC；Host 只做输入绑定、调用和 DTO 映射。
- 新增实验性成就路由和页面，支持多账号、游戏摘要、单游戏详情、手动刷新、stale 与最后成功时间。
- 新增 `useIpc.ts`、`useAsyncResource.ts`、`store/modules/steam.ts`，先由 Achievements 使用，再迁移 Library/Friends。
- 为后端和前端新增自动化测试；所有测试不得访问真实 Steam、真实网络或真实用户数据库。
- 更新 `docs/ARCHITECTURE.md`、`CONTRIBUTING.md` 和 smoke checklist。

### 2.2 明确不做

- 不在初始页面加载时抓取用户全部游戏的完整成就 schema。
- 不把成就名或描述写入 `src/locales/*.json`。
- 不把个人解锁进度、SteamID、账号名或时间线发布到 Public Data。
- 不新增万能 `ISteamGateway`、通用 Repository、MediatR、CQRS 框架或新的持久化项目。
- 不为了“切片一致”机械地为每个类建立接口。
- 不在本阶段升级 SteamKit2、TFM、Electron、Vue、Pinia 或 EF Core；依赖升级使用独立 PR。
- 不在本阶段实现云同步、成就解锁器、成就修改、游戏内注入或任何会写 Steam 数据的能力。
- 不承诺支持好友或任意第三方用户的私有成就；第一版只服务当前已登录账号。
- 不把 Public Data CDN 上线作为 Phase 3 本地功能的阻塞条件。
- 不在本阶段同时重写 7 个 Steam 页面；Achievements 验证基础设施后只回填 Library/Friends。
- 不把 UI retry 与 Gateway/HTTP/CM retry 叠加成自动重试风暴；UI 默认只提供用户触发的 retry。

### 2.3 推荐的 MVP 产品边界

第一版成就页建议只包含：

1. **账号选择**：来源于共享 Steam store。
2. **游戏概览**：名称、appid、总成就、已解锁、完成率、最近游玩；数据优先复用已有 Library snapshot/批量摘要。
3. **单游戏详情**：本地化名称、描述、图标、隐藏标记、解锁状态和解锁时间。
4. **筛选与排序**：全部/已解锁/未解锁/隐藏，按解锁时间、完成状态或定义顺序。
5. **资源状态**：Fresh/Stale、来源、最后成功更新时间、受控错误与重新认证提示。
6. **手动刷新当前游戏**，而不是“一键刷新全部 schema”。

跨库统计、稀有度图表、完成趋势和全局时间线应等底层数据正确、缓存稳定后再做；不要让图表延误第一个垂直切片。

---

## 3. 对原计划的必要校正

### 3.1 `schema_hash` 是不透明版本值，不是应用自行计算的内容摘要

当前 SteamDatabase 上游 protobuf 中，`CPlayer_GetGameAchievements_Response.schema_hash` 是 `uint32`；但 P3-M0 已确认仓库锁定的 SteamKit2 3.4.0 generated surface 尚未包含该字段，详见 12.1 的 M0 完成记录。在选择并验证新的协议来源前，不得假设当前依赖可直接实现 hash revalidation。协议来源具备该字段后，实现应：

- 把它当作 Valve 给出的不透明版本号；
- 不假设它一定非 0；
- 不把它与应用 payload schema version 混为一谈；
- 不用“序列化 JSON 后再 SHA-256”替代它；
- 在 SteamKit2 升级时由 compile-contract 和 fixture 明确验证 optional/default 行为。

建议模型使用 `uint SchemaHash`，缓存 entry 的 `ContentHash` 保存 invariant decimal string，或者在内部 codec 中保存；不要把它转换成用户可见语义。

### 3.2 本地缓存不建议把 hash 直接放入主键

原计划写的是缓存键 `(appid, language, schema_hash)`。这适合内容寻址的 Public Data artifact，但不适合当前 SQLite 的“读取当前版本”路径：读取前尚不知道最新 hash，且每次变化都会留下旧版本。

本地 SQLite 推荐：

```text
resourceKind = achievement-schema
scopeId     = public
resourceId  = <appid>
language    = english | schinese | tchinese
variant     = ""
schemaVersion = 1               # Steam Stat payload codec version
ContentHash = <Valve schema_hash>
```

这样可以按稳定 key 一次读取当前 schema，再用 hash-only 请求做条件再验证。原子 upsert 自动替换旧版本，不会无限累积历史 schema。

如果未来建设 Public Data，可在发布仓库中另用：

```text
schemas/<appid>/<language>/<schema_hash>.json
```

并用 manifest 指向当前 hash。不要为了未来 CDN 改坏当前本地查询路径。

### 3.3 schema 与个人进度是两类资源

公共 schema 可以跨账号共享，个人进度不可以：

| 资源 | scope | 语言维度 | 可进入 Public Data | 推荐 freshness |
| --- | --- | --- | --- | --- |
| Achievement schema | `public` | 有 | 是 | 长 TTL + hash 再验证 |
| 游戏级完成摘要 | SteamID | 通常无 | 否 | 短 TTL |
| 逐项解锁状态/时间 | SteamID | 无；与 schema 合并后本地化 | 否 | 短 TTL，失败可回退旧快照 |

禁止把 accountName 当持久 cache scope；与 Phase 2 一致，使用稳定 SteamID。accountName 只用于选择当前 session 和 UI 展示。

### 3.4 完整 schema 不等于个人解锁详情

`Player.GetGameAchievements` 返回展示 schema；`Player.GetAchievementsProgress` 只提供 app 级总数、已解锁数和百分比。要展示逐项解锁状态/时间，还需要验证并实现个人 user-stats 协议路径。

因此 P3-M0 必须回答：

- 逐项 unlock block 如何稳定映射到 schema 的 `internal_key` / `internal_name`；
- `unlock_time` 的数量和索引语义；
- 私有资料、家庭共享、无成就游戏和未启动游戏的返回；
- 请求是否纯读取，是否会改变 persona/game-playing 状态；
- 是否需要 routing appid 或其他 header；
- 当前 SteamKit2 3.4.0 generated type 的确切命名和 EMsg 关联。

如果这一映射无法在受控 fixture 和真实 smoke 中证明，不能按 localized name 猜测。应缩小发布范围为“schema + 游戏级摘要”，并在文档/UI 明示缺少逐项状态，而不是生成可能错误的时间线。

### 3.5 Public Data 是后续 source，不是本地切片的前置工程

Phase 2 已预留 `SteamDataSource.PublicData` 和 dependency health，但实际仓库、manifest、发布流水线和信任策略都不存在。推荐顺序：

```text
先完成 SQLite → CM 的正确闭环
  → 收集 schema fixture 与缓存命中数据
  → 再以独立 PR/独立仓库增加 Public Data source
```

Public Data 上线前至少要定义 schema version、manifest、artifact hash、最大下载大小、压缩炸弹防护、allowed host 和失败回退。不能把 CDN JSON 当可信对象直接反序列化。

### 3.6 “回填切片”不是只移动文件

Friends / Library 后端已经具有 Feature + Gateway 的雏形。Phase 3 的回填重点应是：

- 查询与刷新命令分开；
- Feature 返回 typed result/status，而不是 `List`/`null`/`bool`；
- 页面通过 `useIpc`、`useAsyncResource` 和 Steam store 获取数据；
- 刷新失败保留旧数据；
- 去除重复拉取账号和重复 CM 请求；
- 架构测试固定新边界。

不要为了目录“看起来一致”重写已经稳定的 SessionManager 或 persistence adapter。

### 3.7 不再新建一个泛化的 `SchemaCache`

原计划所说“产出的 `SchemaCache` 可供 SaveScope 复用”，在当前仓库中应解释为**复用缓存端口、codec/version、原子更新和失效模式**，而不是再增加一个名为 `SchemaCache` 的通用服务。Phase 2 已有 `ISteamResourceCacheStore`，Achievement schema 应直接成为它的一个 typed consumer。

SaveScope 后续面对的是本地文件解析器版本、文件指纹和解析结果，信任边界与公共 Steam schema 不同。届时可以复用模式，但不能被迫使用相同 DTO、TTL 或 public scope。只有第二种 schema 类资源出现并证明存在相同操作后，才提取更高层 typed cache helper。

---

## 4. 目标架构与依赖方向

### 4.1 推荐目录

只在放入真实实现时创建目录：

```text
backend/src/SteamStat.Core/
├─ Features/
│  ├─ Achievements/
│  │  ├─ Contracts/
│  │  │  ├─ ISteamAchievementSchemaGateway.cs
│  │  │  └─ ISteamAchievementProgressGateway.cs
│  │  ├─ AchievementModels.cs
│  │  └─ SteamAchievementsService.cs
│  └─ Library/Contracts/
│     └─ IOwnedGameCatalog.cs               # 仅在现有契约无法提供只读概览时新增
├─ Steam/
│  ├─ Gateway/
│  │  ├─ SteamAchievementSchemaGateway.cs
│  │  ├─ SteamAchievementProgressGateway.cs
│  │  └─ Internal/
│  │     ├─ CmAchievementSchemaSource.cs
│  │     ├─ CmAchievementProgressSource.cs
│  │     └─ AchievementProtocolMapper.cs
│  └─ Cache/
│     └─ SteamCachePolicy.cs              # 增加 achievement policies

backend/src/SteamStat.Contracts/
├─ IpcContracts.cs                        # AchievementIpc
└─ IpcDtos.cs                             # request/response DTO

ElectronNet/ElectronNet/
├─ Hosting/IpcDtoMapper.cs
└─ Services/IpcMainService.cs             # 仅注册 thin handlers

src/
├─ composables/
│  ├─ useIpc.ts
│  └─ useAsyncResource.ts
├─ store/modules/steam.ts
├─ views/steam/achievements.vue
└─ router/modules/steam.ts
```

不要建立 `SteamStat.Achievements` 新项目。当前 Core 内的 Feature 目录和 architecture tests 已能提供足够边界，新增程序集只会增加装配和循环依赖成本。

### 4.2 依赖方向

```text
Achievements page
  ├─ useAsyncResource
  ├─ useSteamStore
  └─ useIpc
        │
        ▼
generated ElectronAPI
        │
        ▼
IpcMainService / DTO mapper
        │
        ▼
SteamAchievementsService
  ├─ ISteamAchievementSchemaGateway
  ├─ ISteamAchievementProgressGateway
  └─ IOwnedGameCatalog（Library 发布的只读契约）
        │
        ▼
Gateway implementations
  ├─ ISteamResourceCacheStore
  ├─ SteamRequestCoalescer
  └─ CM sources
        │
        ▼
ISteamSessionAccessor + SteamCmOperationScheduler
```

允许：

- Achievement Feature 依赖自己发布的 Contracts，以及 Library 明确发布的只读游戏目录契约。
- Gateway 实现依赖 cache port 和 internal CM source。
- CM source 依赖 raw session accessor、SteamKit2 和 generated protobuf。
- Host 依赖 Core 与 Contracts，执行 DTO 映射。

禁止：

- Achievement Feature 直接引用 `SteamClient`、`SteamUnifiedMessages`、EMsg 或 generated protobuf。
- Contracts/IPC DTO 引用 SteamKit2 类型、EF entity 或 Gateway result 类型。
- Vue 页面手写 channel string 或复制 C# DTO。
- schema Gateway 依赖个人 Library entity。
- progress cache 使用 `scopeId=public`。

### 4.3 推荐的能力端口

公共 schema 和个人 progress 生命周期不同，建议拆成两个窄端口，而不是一个拥有所有成就操作的接口：

```csharp
public interface ISteamAchievementSchemaGateway
{
    Task<SteamGatewayResult<SteamAchievementSchemaSnapshot>> GetSchemaAsync(
        uint appId,
        string language,
        string? preferredAccountName = null,
        SteamRefreshMode refreshMode = SteamRefreshMode.PreferCache,
        CancellationToken cancellationToken = default);
}

public interface ISteamAchievementProgressGateway
{
    Task<SteamGatewayResult<IReadOnlyList<SteamAchievementAppProgressSnapshot>>> GetSummariesAsync(
        string accountName,
        IReadOnlyList<uint> appIds,
        SteamRefreshMode refreshMode = SteamRefreshMode.PreferCache,
        CancellationToken cancellationToken = default);

    Task<SteamGatewayResult<SteamAchievementUnlockSnapshot>> GetUnlocksAsync(
        string accountName,
        uint appId,
        SteamRefreshMode refreshMode = SteamRefreshMode.PreferCache,
        CancellationToken cancellationToken = default);
}
```

这只是建议形状，M0 应按 SteamKit2 实际能力调整。关键约束是：schema 与个人数据不能共用 scope、TTL 或 DTO，也不能把原始协议对象暴露给 Feature。

### 4.4 Feature use case 的职责

`SteamAchievementsService` 应负责：

- 验证 app 是否可用于当前账号；
- 从 `ILanguageProvider` 取得 Steam language；
- 并行或顺序调用 schema/progress Gateway，但保持有界；
- 按稳定内部标识合并 definition 与 unlock；
- 计算 UI 需要的派生值；
- 组合 partial success：schema 成功、progress 失败时仍可显示公开定义；
- 返回与 transport 无关的 Feature result。

它不应负责：

- 构造 protobuf/EMsg；
- 自己读写 SQLite；
- 拼 Steam URL；
- 发 toast 或 Electron event；
- 管理全局账号选择；
- 对所有 app 自动抓取详情。

### 4.5 overview 的跨 Feature 只读边界

Achievement overview 需要“当前账号有哪些游戏、名称和最近游玩时间”，但不能直接读取 `SteamFeatureSnapshotStore`，也不能引用 `SteamOwnedGame` 可变实现模型。若现有 Library Contracts 不适合，应由 Library 发布一个窄的 `IOwnedGameCatalog`：

```csharp
public interface IOwnedGameCatalog
{
    Task<IReadOnlyList<OwnedGameCatalogItem>> GetCachedAsync(
        string accountName,
        CancellationToken cancellationToken = default);
}

public sealed record OwnedGameCatalogItem(
    uint AppId,
    string Name,
    string LocalizedName,
    int PlaytimeForever,
    long LastPlayedAt);
```

该接口默认只读已持久化/内存 Library snapshot，不隐式刷新 CM。Achievement overview 再用 progress Gateway 批量补充完成摘要。这样依赖保持单向：Achievements 依赖 Library 发布的只读契约；Library Feature 不依赖 Achievements Feature。

为兼容 Library 现有进度条，`CmLibrarySource` 与 `SteamAchievementProgressGateway` 可以在 `Steam/Gateway/Internal` 共享同一个 `IAchievementProgressSource` adapter，但不能复制 protobuf 调用，也不要让两个 Feature Contracts 互相引用。未来若 Library 不再需要成就摘要，可直接删除其内部消费路径。

---

## 5. 领域模型与合并规则

### 5.1 公共 schema snapshot

建议至少保留：

```text
SteamAchievementSchemaSnapshot
- AppId
- Language
- ValveSchemaVersion
- SchemaHash
- Definitions[]
- Groups[]

SteamAchievementDefinition
- InternalKey?                # 协议提供时优先使用
- InternalName                # 稳定内部名称，禁止本地化
- LocalizedName
- LocalizedDescription
- Icon
- IconGray
- Hidden
- GlobalUnlockedPercent?      # 协议为本地化无关的字符串时 invariant parse
- GroupId?
- Archived
- ProgressType
- Min/Max progress

SteamAchievementGroup
- GroupId
- LocalizedName
- DlcAppId?
- Archived
- DeveloperOnly
- IsPublic
- Order
```

原则：

- 不把 `localized_name` 当主键。
- 未知/新增枚举值要保留为 Unknown，而不是反序列化失败整个 schema。
- `player_percent_unlocked` 若是字符串，使用 invariant culture 安全解析；解析失败返回 `null`，不丢弃整个 achievement。
- 保留 `hidden`、`archived`、group 信息，即使 MVP UI 暂不全部展示。
- schema payload 只包含公共数据，不包含 accountName、SteamID 或 session generation。

### 5.2 个人进度 snapshot

建议区分游戏摘要与逐项详情：

```text
SteamAchievementAppProgressSnapshot
- AppId
- Total
- Unlocked
- Percentage

SteamAchievementUnlockSnapshot
- SteamId
- AppId
- SessionGeneration
- Entries[]

SteamAchievementUnlock
- InternalKey? / InternalName
- IsUnlocked
- UnlockTimeUtc?
- CurrentProgress?            # 只有协议语义已验证时保留
```

个人缓存序列化前应删除仅用于竞态控制的 `SessionGeneration`，或者把它明确视为 runtime metadata；generation 不能跨进程恢复后继续参与有效性判断。

### 5.3 合并键优先级

合并必须由 P3-M0 fixture 证明，推荐优先级是：

1. 协议明确证明可对应的 `internal_key` / numeric achievement id；
2. 大小写规则已验证后的 `internal_name`；
3. 无法证明时保留 unmatched progress，并记录低基数诊断计数。

绝对禁止：

- 按本地化名称匹配；
- 按数组位置默认匹配；
- 为了让计数“看起来正确”随意补齐；
- 把 unmatched 当作 locked 而不记录差异。

### 5.4 隐藏成就显示规则

建议与 Steam 的语义保持保守一致：

- 已解锁隐藏成就可展示名称、描述和图标。
- 未解锁隐藏成就默认显示“隐藏成就”，不要主动泄露描述或彩色图标。
- 可在设置中增加“显示隐藏描述”之前，先完成产品与隐私决策；不要在 MVP 中默认开启。
- Feature DTO 可以保留完整公共 schema，但页面层应明确执行显示策略并有测试。

---

## 6. schema hash 缓存流程

### 6.1 推荐 policy 初值

以下是初始建议，不是 Valve 的官方 TTL：

| 资源 | refresh | stale/expired 分界 | retain | negative cache |
| --- | --- | --- | --- | --- |
| `achievement-schema` | 24 小时 | 30 天 | 180 天 | 仅 NotFound，建议 6 小时以内 |
| `achievement-progress-summary` | 15 分钟 | 30 天 | 180 天 | 否 |
| `achievement-unlocks` | 5 分钟 | 30 天 | 365 天 | 否 |

所有 personal cache 在上游失败时允许返回 stale/expired 旧数据，但必须附带 failure metadata。AuthenticationRequired、Timeout、RateLimited、Offline 均不得 negative cache。

### 6.2 读取算法

```text
GetSchema(appId, language, refreshMode)
  1. 规范化 appId / Steam language，构造稳定 cache key
  2. 读取并严格解码 SQLite entry
  3. CacheOnly:
       hit  → 返回实际 freshness
       miss → typed cache_miss
  4. PreferCache + Fresh:
       直接返回，不访问 CM
  5. 其余路径进入 keyed coalescer
  6. 有旧 cache 时先发送 hash_only=true
       a. hash 相同：只延长 revalidation 时间，返回旧 payload + Fresh
       b. hash 不同：发送 hash_only=false 拉完整 schema
  7. 无旧 cache 时直接拉完整 schema；不必先多一次 hash 请求
  8. 验证、映射、检查 payload 大小后原子 upsert
  9. 上游失败：
       有可保留 cache → stale/expired success + failure metadata
       无 cache         → typed failure
```

### 6.3 请求合并 key

schema 是公共资源，coalescing key 不应包含 accountName：

```text
achievement-schema / public / appid / language / schemaVersion
```

CM source 可以优先使用调用者指定的 Ready account；不可用时选择另一个 Ready session，并最多做一次受控切换，禁止对所有账号 fan-out。

progress 是个人资源，key 必须包含稳定 SteamID：

```text
achievement-unlocks / steamId / appid / schemaVersion
```

现有 `SteamRequestCoalescer<SteamCacheKey>` 已按 key + result type 隔离共享 task，并让 caller cancellation 只取消自己的等待。应直接复用，不再创建 Feature 局部 `ConcurrentDictionary<K, Task<V>>`。

### 6.4 hash 相同时的写放大

现有 cache port 没有单独的 metadata-touch API。第一版可用同 key/upsert 更新 `FetchedAt/RefreshAfter`，但要观察大 schema 的 SQLite 写放大。

只有在真实测量显示明显问题后，才为 `ISteamResourceCacheStore` 增加类似 `RevalidateAsync(key, contentHash, fetchedAt, refreshAfter, retainUntil)` 的窄方法，并保证 SQL 带旧 hash 条件，避免并发覆盖新 payload。不要先增加通用 patch API。

### 6.5 payload 限制与损坏处理

P3-M0 应保存至少三类脱敏 fixture：

- 无成就/极小 schema；
- 包含 hidden、group、progress type 的普通 schema；
- 当前能找到的最大受控 schema。

根据 UTF-8 实际字节数决定是否调整 1 MiB 上限。若调整：

- Core `SteamCachePolicy.MaximumPayloadBytes` 与 Host adapter 上限必须一致；
- 增加刚好小于/大于限制的测试；
- IPC/JSON 反序列化也要有集合数量和字符串长度上限；
- 损坏或超限 entry 是 safe miss，不得变成 success-empty；
- 新成功写入失败时保留上一份成功 entry。

不建议 Phase 3 同时引入压缩 codec。若后续确需 gzip/brotli，应新增明确 payload format、解压后上限和压缩炸弹测试，而不是继续标记为 `json-v1`。

---

## 7. CM source 与个人进度实现

### 7.1 schema source

`CmAchievementSchemaSource` 应：

1. 选择 Ready session 并取得 generation；
2. 取得 `SteamUnifiedMessages` / `Player` service；
3. 经 `ISteamCmOperationScheduler` 调用，operation 名固定为低基数字符串，例如：
   - `achievement-schema-hash`
   - `achievement-schema-full`
4. 将非 OK `EResult` 交给 `SteamResultClassifier`；
5. 映射为稳定 snapshot；
6. 返回前再次检查 session generation，丢弃旧 session 的迟到结果；
7. 不记录完整 schema、appid 列表或个人标识到 metrics。

### 7.2 progress summary source

现有 Library 中每 100 app 调用 `GetAchievementsProgress` 的逻辑应迁移到 `CmAchievementProgressSource`，保留：

- 每批最多 100 个 app；
- CM scheduler；
- caller cancellation；
- generation check；
- typed partial/failure 语义。

不要继续 `catch Exception` 后把某批静默跳过并把整个结果标记为完整成功。推荐返回：

```text
Value = 已成功批次的 summaries
Failure = 若有失败批次则附带 Transient/Timeout/... metadata
DiagnosticCode = achievement_progress_partial
```

Library 若为了兼容继续显示三个汇总字段，应消费该 source/gateway 的结果，而不是保留第二份 protobuf 调用。这样 Achievements 与 Library 不会各发一轮相同请求。

### 7.3 逐项 unlock source

若使用 client user-stats EMsg：

- raw request/response、header routing 和 binary schema 解析全部限制在 `Steam/Gateway/Internal`；
- 通过现有 session callback/job 机制建立可等待、有界、可取消的 request；
- 仍需经过 CM scheduler；
- operation 名固定为 `achievement-unlocks`；
- 不允许为了读取数据而发送会改变用户在线/游戏状态的消息，除非单独完成产品决策、可见提示和 smoke；默认应保持纯读取；
- 对 unsupported/private/forbidden/not-found 做稳定分类；
- 解锁时间按 Unix UTC 处理，0 或协议缺失映射为 `null`；
- 不以本机时区保存持久数据，格式化只在 renderer 进行。

若 SteamKit2 没有现成 AsyncJob 封装，应编写**成就专用的 internal handler**，不要把任意 EMsg 请求抽成通用 service locator。

### 7.4 并发与取消

- overview 只发批量 summary 请求；每账号批次顺序或低并发执行。
- 用户打开一个游戏时，schema 与 unlock 可并行，但总共只针对一个 app。
- 用户快速切换游戏时，renderer 使用 latest-request-wins；后端 caller cancellation 若当前 IPC 框架无法传递，不要伪造“请求已取消”，而应丢弃迟到 UI 结果。
- scheduler timeout 后 permit 继续持有到真实 Steam job 完成，沿用 Phase 2 行为。
- 不对 protocol/invalid-data/forbidden 自动 retry。

---

## 8. IPC 契约与 Host 边界

### 8.1 推荐 endpoint

查询与强制刷新应分开，避免一个 `get` 隐式访问网络：

```text
steamAchievements:overview:get
steamAchievements:game:get
steamAchievements:game:refresh
```

可选地增加：

```text
steamAchievements:overview:refresh
```

但第一版不需要“刷新所有详细 schema”。overview refresh 只更新批量摘要。

### 8.2 请求 DTO

建议：

```text
SteamAchievementOverviewRequest
- accountName: required, max 64

SteamAchievementGameRequest
- accountName: required, max 64
- appId: integer, 1..uint.MaxValue
```

language 从 `ILanguageProvider` 取得，不接受 renderer 任意传入，避免 cache key 爆炸和未验证字符串进入 CM。若未来允许每页语言选择，再把允许值做成明确 union。

### 8.3 返回 envelope

不要只返回 achievements array。建议返回：

```text
SteamAchievementGameResultDto
- appId
- appName
- language
- schemaHash
- achievements[]
- summary
- source
- freshness
- lastSuccessfulUpdate
- partial
- failure?                 # 稳定枚举，不是异常文本
```

Feature model、Gateway result 和 IPC DTO 应分离：

- Core 使用 enum/record；
- Host mapper 转成稳定 wire string；
- renderer 只依赖生成类型；
- exception message/stack trace 不进入 IPC。

成功但没有成就必须表示为 `success + []`；获取失败必须是 failure envelope 或受控 IPC error，不能同样返回 `[]`。

### 8.4 Host 实现

`IpcMainService` 只应：

- 使用 `IpcRequestBinder` 验证输入；
- 调用 `SteamAchievementsService`；
- 经 `IpcDtoMapper` 映射；
- 不选择数据源、不读 cache、不处理 schema hash、不拼图标 URL。

新增 descriptor 后运行 generator，禁止手改：

- `ElectronNet/ElectronNet/Resources/preload.mjs`
- `src/types/ipc.d.ts`
- `ipc-contracts.snapshot.json`

### 8.5 是否需要事件

成就详情是用户触发查询，不需要在 MVP 中增加 Host-to-renderer event。只有未来真的存在后台增量刷新或本地游戏解锁监听时，才增加 typed event。不要为了“切片完整”制造无人消费的事件。

---

## 9. Renderer 基础设施

### 9.1 `useIpc.ts`

现有 `Window.electron` 已由 generator 强类型化。`useIpc` 不应再维护一份 method map，也不应接收任意 channel string。

推荐职责：

- 返回 `window.electron` 的窄 typed facade；
- 在非 Electron/test 环境提供明确的 unavailable error；
- 把 `unknown` 异常规范化为 renderer error model；
- 允许测试注入 fake `ElectronAPI`；
- 统一 listener 注册/移除的生命周期辅助函数。

不建议：

- `invoke<T>(channel: string, payload: unknown)`；这会绕过生成契约。
- 在 composable 中 toast；调用页面决定如何展示。
- 在这里实现 retry/cache/loading；这些属于 `useAsyncResource` 或 Pinia。
- 用 `as any` 掩盖生成类型不匹配。

可以采用以下使用形态：

```ts
const ipc = useIpc()
const result = await ipc.steamAchievementsGameGet({ accountName, appId })
```

测试通过 app/provider 注入 fake，而不是修改全局 `window` 的只读类型。

### 9.2 `useAsyncResource.ts`

该 composable 应明确状态机，而不只是 `{ loading, data }`：

```text
idle
  → initial-loading
  → success
  → refreshing（保留旧 data）
  → success | stale-error（保留旧 data + error）
```

推荐暴露：

```text
data
error
status = idle | loading | success | refreshing | error
hasData
isInitialLoading
isRefreshing
execute()
refresh()
retry()
reset()
```

必要语义：

- 初次失败且无旧数据：`error`。
- 刷新失败且有旧数据：保留 data，状态为可展示 stale/error 的组合。
- latest-request-wins，防止快速切换账号/app 后旧响应覆盖新响应。
- 同一资源同时刷新时默认复用或拒绝重复触发。
- 自动 retry 默认关闭；用户点击 retry 才重新请求。
- composable 不自行判断 Steam Fresh/Stale；后端返回的业务 freshness 与前端请求状态是两个维度。
- 页面卸载后不再提交状态；若 IPC 不能取消后端请求，只丢弃迟到结果。

建议先为该 composable 建立前端测试，再迁移页面。它一旦语义错误，会同时影响多个页面。

### 9.3 `store/modules/steam.ts`

`src/store/modules/user.ts` 表示应用模板自身的“用户”，不是 Steam 账号；不要复用或改名。新增 `useSteamStore`，第一版只保存跨页面真正共享的数据：

```text
loggedInAccounts
selectedAccountName
operationalStatus
bootstrapStatus
lastBootstrapAt
```

推荐 actions：

```text
ensureBootstrapped()
refreshAccounts()
refreshOperationalStatus()
selectAccount(accountName)
handleSession/login event（确有事件时）
reset()
```

约束：

- `ensureBootstrapped` 必须合并并发调用，防止多个页面同时拉账号。
- selected account 不存在时选择第一个可用账号；账号退出后自动修正。
- 不在 store 中保存 password、token、guard code 或 QR data。
- 不一开始把 Library/Friends/Achievements 全部 payload 塞进一个 God Store。
- Feature 页面数据可先保留在页面级 `useAsyncResource`；只有多个页面确实共享时再进入 store。

### 9.4 前端测试基础设施

当前项目没有直接的前端测试依赖。Phase 3 建议增加最小测试栈：

- Vitest；
- Vue Test Utils；
- happy-dom 或 jsdom（二选一）。

新增依赖时使用 pnpm、锁定经审查且发布至少 7 天的版本，不使用 `latest` 或浮动 `*`。建议增加：

```text
pnpm run test:unit
pnpm run test:unit:watch
```

CI 至少运行一次非 watch 模式。若暂时不测试整页，最低要求也要覆盖 `useAsyncResource`、`useSteamStore` 和 Achievements 的纯映射/排序函数。

### 9.5 成就页面交互

推荐页面结构：

```text
顶部：账号选择 + connectivity/stale 状态 + 刷新摘要
左侧/上部：游戏概览（虚拟列表）
右侧/下部：当前游戏详情（按需加载）
```

要求：

- 1000+ 游戏使用虚拟列表，不渲染全部卡片 DOM。
- 打开游戏前不请求完整 schema。
- 图标 lazy load，失败显示本地占位；外部 URL 必须经过明确 allowlist/normalization 决策。
- 初始 loading、空账号、无成就、认证失败、离线无缓存、离线有 stale 缓存均有不同 UI。
- 手动刷新时保留旧详情，不用空白 Spin 覆盖整个页面。
- 时间戳以用户本地时区显示，但 DTO/持久化使用 Unix UTC。
- zh-CN/en-US 文案同步更新。
- route 标记 `experimental: true`，与 Login/Friends/Library 一致。

---

## 10. Friends / Library 回填方案

### 10.1 先回填 Library

Library 与 Achievements 已共享进度摘要，是第一优先级：

1. 将页面中的 `window.electron` 改为 `useIpc`。
2. 账号列表改读 `useSteamStore`。
3. 数据读取改用 `useAsyncResource`。
4. 将“读取缓存/现有数据”和“强制同步”拆成两个动作。
5. 修复当前 `sync → get` 导致后端再次刷新的双请求：
   - 方案 A：sync endpoint 直接返回刷新后的 typed snapshot；推荐。
   - 方案 B：sync 后调用真正 cache-only 的 get；可接受。
6. Library 的三个 achievement summary 字段改由共享 Achievement progress capability 填充，不再在 `CmLibrarySource` 保留独立协议调用。
7. 刷新失败继续显示旧数据和 failure metadata。

### 10.2 再回填 Friends

1. 页面改用 `useIpc`、`useSteamStore`、`useAsyncResource`。
2. listener 用 composable 的生命周期 helper 注册和移除。
3. 初次 snapshot 与增量 event 采用同一合并函数并测试。
4. 刷新/获取失败保留已有好友列表。
5. tracking records 的 loading 可以使用独立 resource，不与主好友列表共用一个布尔值。
6. 保留 Friends 的实时 feed 特性，不为了统一而强行改成轮询。

### 10.3 后端 typed result 的渐进迁移

现有 Library/Friends 的外部方法大量返回 `List`、`null`、`bool`。不建议一次破坏所有旧 endpoint。推荐：

- 先增加新的 typed Feature result 和新 endpoint；
- 页面迁移后保留旧 endpoint 一个版本；
- 确认无调用者后再删除；
- 每次删除同步更新 Contracts、generator snapshot、Host tests 和 docs。

如果不需要兼容已发布 renderer/Host 混合版本，也可以原子替换，但必须在同一 PR 生成所有 IPC 文件并完成安装包 smoke。

---

## 11. 测试策略

### 11.1 P3-M0 协议契约测试

测试必须直接强类型引用当前 SteamKit2 3.4.0 的：

- `Player.GetGameAchievements` 请求/响应；
- `hash_only`、`schema_hash`、schema version、definitions/groups 字段；
- 个人 user-stats request/response 或专用 handler 使用的 EMsg/generated types；
- callback/job 等待签名。

这些测试只验证“升级后是否还能编译”和受控响应语义，不访问真实网络。

### 11.2 Core 单元测试

至少覆盖：

#### schema mapper

- hidden/archived/group/progress type 映射；
- invariant percent 解析；
- 空/未知字段容错；
- duplicate internal key/name 的确定性处理；
- protobuf object 未泄漏到稳定 snapshot。

#### schema Gateway

- fresh hit 为 0 次 CM；
- stale + hash unchanged 不拉 full schema；
- stale + hash changed 拉 full 并替换 cache；
- miss 直接拉 full，不多发 hash 请求；
- hash/full failure 返回旧 stale + failure；
- 无 cache failure 不返回 success-empty；
- language key 隔离；
- malformed/oversized cache safe miss；
- NotFound 可短期 negative cache，认证/超时/限流不缓存；
- 同 key 并发请求合并；
- caller cancellation 不取消其他 caller；
- 旧 generation 结果不写 cache。

#### progress Gateway

- 100 app 分批边界；
- 空 app list；
- 部分批次失败保留成功数据并标记 partial；
- personal cache scope 使用 SteamID；
- 账号之间不串数据；
- unlock timestamp 0/缺失/有效值；
- schema stable key 合并；
- unmatched unlock 不被错误归类；
- stale fallback 和 reauthentication。

#### Feature

- schema + progress 完整成功；
- schema 成功/progress 失败的公开只读降级；
- schema 失败/progress stale 的显示决策；
- 无成就是成功空；
- 隐藏成就显示策略；
- 排序和完成率边界；
- app 切换不共享错误状态。

### 11.3 Host 测试

至少覆盖：

- IPC request 对缺失账号、超长账号、0/负数/越界 appId 的拒绝；
- Feature → DTO mapper；
- failure/source/freshness enum 的稳定 wire 值；
- generator snapshot 包含新增 endpoint；
- preload 与 `ipc.d.ts` 由生成器更新；
- cache round-trip、hash、language 和 payload limit；
- 若调整上限，旧数据库 migration/schema/backup fixture 继续通过。

仅新增 resource kind 和 payload 不需要 migration。只有数据库列/index 变化时才生成 migration，禁止创建空 migration 留在仓库。

### 11.4 Architecture tests

新增门禁应固定：

- `Features/Achievements/**` 不含 `SteamClient`、`SteamUnifiedMessages`、generated protobuf、EMsg、`IHttpClientFactory` 或 Steam URL；
- Contracts/IPC DTO 不引用 SteamKit2、EF、Core internal 类型；
- achievement schema snapshot 不含 SteamID/accountName/token；
- personal progress cache 不使用 `public` scope；
- protocol types 只位于 `Steam/Gateway/Internal`；
- renderer 不新增手写 IPC channel；
- Library 不再直接调用 achievement-progress protobuf；
- Feature 不出现局部 `ConcurrentDictionary<..., Task<...>>` coalescer。

架构测试要验证真正边界，不要只按某个文件名通过；允许的 internal 目录应尽可能小。

### 11.5 前端测试

至少覆盖：

- `useAsyncResource` 初始成功/失败；
- refresh 保留旧 data；
- retry；
- latest-request-wins；
- 重复刷新合并/拒绝策略；
- unmount 后不提交迟到状态；
- Steam store bootstrap coalescing；
- 账号退出后 selected account 修正；
- Achievements DTO → view model 的 hidden、时间和排序规则；
- Library 手动同步只触发一轮刷新。

### 11.6 手工 smoke

至少记录：

1. 普通账号 + 有成就游戏：摘要、详情、图标、本地化和解锁时间正确。
2. 无成就游戏：显示明确空状态，不报网络错误。
3. 含隐藏成就游戏：未解锁内容不泄露。
4. 多账号：切换不串 progress/cache。
5. 首次在线成功 → 退出 → 断网重启：已访问详情可 stale 展示。
6. fresh cache 下重复打开同一游戏：不重复 CM full fetch。
7. cache 过期但 hash 不变：只做 hash 请求。
8. 模拟过期 token：停止刷新并提示重新认证，旧数据保留。
9. 快速切换多个游戏：最终只显示最后选择的 app。
10. 1000+ Library：overview 滚动无明显掉帧，且未预取全部 schema。
11. 浅色/深色、zh-CN/en-US。
12. 应用关闭时无未观察 task、无 callback/handler 泄漏。

---

## 12. 分里程碑实施顺序

### P3-M0：协议与产品契约 spike

内容：

- 固定当前 commit、测试数和现有 smoke 基线。
- 为 `GetGameAchievements` 增加 compile-contract test。
- 为 user-stats 路径建立最小 internal spike 和脱敏 fixture decoder。
- 使用受控账号手工验证 2–3 类游戏：普通、无成就、hidden/group 边界。
- 测量 schema UTF-8 payload 大小。
- 写下 stable merge key、unlock time 和失败分类决策。

出口：

- 不再依赖“应该可以”的协议猜测。
- 能证明逐项 progress 与 schema 的映射，或明确缩小 MVP。
- spike 不包含真实凭据、SteamID、原始用户 payload 或联网 CI 测试。
- 1 MiB cache 上限是否调整已有数据依据。

#### M0 完成记录（2026-09-15）

**基线与自动化证据**

- 固定基线为 `develop` / `50dd70e77c1e27c17d979326ee03707ef36751d5`；Phase 2 完成时完整 solution 为 215/215。M0 新增 7 个测试后 `SteamStat.Core.Tests` 为 96/96（原 89）；M0 协议筛选集为 12/12。
- 现有 smoke 基线仍以 `docs/dev/smoke-checklist.md` 为准：Phase 2 已通过 lint、build、IPC generator、NuGet audit、Windows 打包和隔离启动；真实 Steam 登录、断网与账号数据场景仍是发布前手工项，不将未执行项标为通过。
- 新增 `SteamKitContractTests` characterization/compile contract，直接固定 `Player.GetGameAchievements`、user-stats generated types、`EMsg.ClientGetUserStats` / `ClientGetUserStatsResponse`、专用 callback 与 `AsyncJob<T>` 等待签名；测试不联网。
- 新增未注册到 DI 的 internal `AchievementUserStatsProtocolHandler`，只发送纯读取 `ClientGetUserStats`，设置 `routing_appid`，且不发送 `ClientGamesPlayed` 或改变 persona/game-playing 状态。
- 三个 embedded JSON fixture 均为合成脱敏数据，不含 appid、SteamID、账号名、凭据或原始 user payload；覆盖普通、无成就、hidden/group、短 block 和 unmatched 边界。

**协议结论与 MVP 收缩**

- 仓库锁定的 SteamKit2 3.4.0 与当前 SteamDatabase protobuf 不同：其 `CPlayer_GetGameAchievements_Request` 只有 `appid`、`language`，没有 `hash_only`；响应只有 `achievements`，没有 `schema_version`、`schema_hash`、`groups`；achievement 也没有 `internal_key`、`groupid`、`progress_type`。测试显式固定这些字段当前不存在，依赖升级或 generated surface 变化会触发失败并要求重新决策。
- user-stats 响应的 `achievement_blocks[].achievement_id` 是二进制 user-stats schema 的 stat/group ID，`unlock_time[n]` 对应该 stat 的 bit `n`，不是 `GetGameAchievements.achievements[n]`。稳定桥接路径是 `(stat_id, bit) -> binary schema API/internal name -> GetGameAchievements.internal_name`；最终合并使用 `StringComparer.Ordinal` 的内部名称，禁止本地化名称、数组位置或大小写猜测。duplicate coordinate/name/block 视为 invalid data；非零但无法映射的坐标保留为 unmatched，不补成 locked。
- `unlock_time == 0` 表示已知未解锁并映射为 `UnlockTimeUtc = null`；正值按 Unix seconds UTC 解码；block 缺失或长度不足表示 unknown（`IsUnlocked = null`），不能猜成未解锁。
- 当前环境未安装 Steam，且本轮未提供可用的受控账号/session，因此没有执行普通、无成就、hidden/group 的真实联网 smoke。公开协议实现还显示部分 CM 会在纯读取失败后临时发送 `ClientGamesPlayed` 才能取得私有 stats，这违反本阶段默认的“读取不改变 playing 状态”约束。故 M0 按出口条件明确收缩 MVP：在受控账号证明纯读取、三类真实返回和 internal-name 对应关系前，只承诺公共展示 schema + 现有游戏级 progress summary，不发布逐项 unlock/time，也不把合成 fixture 当真实 smoke 证据。
- M1/M2 开始前必须选择并单独验证协议方案：升级 SteamKit2（依赖升级独立 PR）或在 `Steam/Gateway/Internal` 维护最小、版本固定的 achievement protobuf/service adapter。未解决前不得实现指南中的 hash-only cache 流程；不得用应用自行计算的 hash 冒充 Valve `schema_hash`。

**协议方案决策（2026-09-15 复核后确定）**

- 经检查，SteamKit2 最新已发布版本仍为 3.4.0（NuGet/GitHub Releases 均无 3.4.1/3.5/prerelease），**升级路径当前不存在**。但上游 master 分支的 generated 源码已包含所需字段：`hash_only`、`schema_version`、`schema_hash`、`groups`、`internal_key`、`groupid`、`progress_type`（`EAchievementProgressType`），且服务方法名为 `Player.GetGameAchievements#1`。上游已修复，只是未发版。
- **决策：采用最小 internal protobuf adapter**。在 `Steam/Gateway/Internal` 内手写 `CPlayer_GetGameAchievements_Request` / `CPlayer_GetGameAchievements_Response`（含嵌套 `Achievement` / `Group` / `EAchievementProgressType`）的 `[ProtoContract]` 契约，字段编号严格对齐上游 master 的 generated 定义，通过 `SteamUnifiedMessages.SendMessage<,>` 调用 `Player.GetGameAchievements#1`；实现与测试风格参照同目录已有的 `CCommunityGetAppRichPresenceLocalizationRequest` 手写契约先例。
- adapter 要求：contract test 固定字段名/编号/可选性/默认 `progress_type`；不允许生成 protobuf 类型离开 `Steam/Gateway/Internal`；若上游日后发布含这些字段的新版 SteamKit2，删除自定义类并改用 generated 类型（需评估 `Internal` 命名空间类型的稳定性），该替换视为一次独立的小步重构而非依赖升级的一部分。
- 保留 `SteamKitContractTests` 中的“字段不存在”断言：它们固定的是 **NuGet generated surface**，即使日后新增 adapter 也不删除，作为上游发版后提醒替换 adapter 的触发器。
- 待办调整：M1 仍按原计划建模型与 mapper；M2 的 hash-only revalidation 可照指南原设计实现，只是底层 source 调用 internal adapter 而非 `Player.GetGameAchievements` generated 方法。

**payload 测量与 cache 决策**

- 脱敏 fixture UTF-8 实测：empty 134 B、ordinary 572 B、hidden/group boundary 965 B。
- 受控合成的 1,328 achievement + 42 group 紧凑 JSON（固定 ASCII 与简体中文字段，未压缩）为 **786,913 B**，占 1 MiB 上限约 75.0%，余量 261,663 B。该测试固定为 `Representative1328AchievementSchemaPayload_IsBelowCurrentCacheLimit`；它是保守容量估算，不是 live CM payload。
- M0 决策为**暂不调整 1 MiB**：代表性高基数估算仍低于上限，且没有真实最大 CM schema 可证明需要提高。Achievement codec 在 M2 落地时必须对受控账号取得的最大 schema 再测；若达到上限的 80%（838,861 B）或超限，应同步调整 Core policy、Host adapter、边界测试和 smoke 文档，写失败必须保留旧成功 entry。超高 achievement-count 异常游戏在取得真实数据前不纳入 MVP 支持承诺。

**失败分类决策**

- 只有 `EResult.OK` 且已成功解码的空 definitions 才是 success-empty；请求失败、schema 缺失、invalid payload 或 mapping 不完整均不得显示“无成就”。
- 复用 `SteamResultClassifier`：登录/session 不可用或凭据过期为 `AuthenticationRequired`，`AccessDenied` 为 `Forbidden`，限流/超时/CM 暂不可用分别为 `RateLimited` / `Timeout` / `Transient`；网络异常为 `Offline`，损坏 fixture/binary schema、duplicate 或越界结构为 `InvalidData`，协议版本不匹配为 `Protocol`。
- `ClientGetUserStatsResponse` 的通用 `Fail` 在真实 smoke 定因前保持 `Unknown`，诊断码使用低基数 `achievement_user_stats_failed`；不得把它擅自改成 NotFound、private 或 success-empty。NotFound 仅接受上游明确的不存在语义，且只有公共 schema 可考虑短期 negative cache；个人 progress/unlock 失败不做 negative cache。

### P3-M1：稳定模型与 Characterization tests

内容：

- 新增 Achievement snapshots、Feature result 和两个 capability ports。
- 若现有 Library Contracts 无法满足只读 overview，新增 `IOwnedGameCatalog`，且读取不隐式触发网络刷新。
- 新增 schema/progress mapper 的纯函数 fixture tests。
- 定义 cache key、policy、diagnostic codes 和 partial semantics。
- 增加 achievement architecture tests。

出口：

- Core 模型不引用 SteamKit2/EF/IPC。
- success-empty、failure、stale-success、partial 均有测试。
- localized name 永远不是合并主键。

#### M1 完成记录（2026-09-15）

**新增的稳定模型与端口**

- `Features/Achievements/AchievementModels.cs`：`SteamAchievementSchemaSnapshot` / `SteamAchievementDefinition` / `SteamAchievementGroup`（公共 schema，不含 SteamID、accountName、session 或 generation）、`SteamAchievementAppProgressSnapshot`、`SteamAchievementUnlock` / `SteamAchievementUnlockSnapshot`（`IsUnlocked == null` 表示 unknown，`SessionGeneration` 仅为 runtime metadata）、`SteamAchievementProgressType { None, Int, Float, Unknown }`（协议值 0/1/2 之外一律保留为 `Unknown`，不使整份 schema 失败）。
- Feature result：`SteamAchievementResourceState`（source/freshness/last-success/failure/diagnostic，`HasValue => Source != null`）、`SteamAchievementEntry`（`IsRevealed`：隐藏成就只有证明已解锁才显示）、`SteamAchievementSummary`、`SteamAchievementGameResult`（`IsSuccess`/`IsEmpty`/`IsStale`/`IsPartial`）、`SteamAchievementOverviewItem` / `SteamAchievementOverviewResult`。语义固定为：`IsEmpty` 只在 schema 成功且 definitions 为空时为 true，失败永远不是“无成就”；`IsPartial` = schema 成功但个人 progress 完全不可用（stale progress + failure metadata 属于 stale-success，不是 partial）。
- 两个窄能力端口 `Features/Achievements/Contracts/ISteamAchievementSchemaGateway.cs`、`ISteamAchievementProgressGateway.cs`，形状与 4.3 一致，均返回 `SteamGatewayResult<T>` 并接受 `SteamRefreshMode`；M1 不提供实现和 DI 注册。
- 现有 `ISteamLibraryGateway` 每次调用都会访问 CM，无法满足只读 overview，因此按 4.5 新增 `Features/Library/Contracts/IOwnedGameCatalog.cs` 与 `Features/Library/SteamOwnedGameCatalog.cs`：仅读取 `SteamFeatureSnapshotStore` 中已持久化的 Library snapshot（owned + family-shared，排除 wishlist-only 项），构造参数不含 Library gateway、scheduler、session accessor，测试断言读取过程 0 次 upsert；已在 `AddSteamStatCore` 注册。

**合并规则（`Features/Achievements/SteamAchievementMerge.Compose`）**

- 合并键为 `InternalName` + `StringComparer.Ordinal`；不用 localized name、数组位置或大小写不敏感匹配。
- 双方 `InternalKey` 都存在且不同 → 该 unlock 视为 unmatched，定义保持 unknown；任一方缺 key 或 key 一致 → 按名称匹配。
- 有 schema 但没有对应 unlock 的定义 → `IsUnlocked = null`（unknown），不补成 locked。
- 未消费的 unlock 名称按 Ordinal 排序进入 `UnmatchedUnlockNames`，供 Gateway/Feature 记录低基数诊断计数。

**cache key、policy 与诊断码**

- `Steam/Cache/SteamAchievementCacheKeys.cs`：`Schema(appId, language)` → `achievement-schema / public / <appid> / <language> / v1`（语言经 `SteamCacheKey` 归一化为小写）；`ProgressSummary(steamId)` → `achievement-progress-summary / <steamId> / summary / v1`（每账号一份 blob，避免每次刷新上千次 upsert）；`Unlocks(steamId, appId)` → `achievement-unlocks / <steamId> / <appid> / v1`。appId、steamId 为 0 或 language 为空直接抛出，个人 key 无法产生 `public` scope。
- `SteamResourcePolicies` 新增 `AchievementSchema`（24h / 30d / 180d，NotFound negative cache 6h）、`AchievementProgressSummary`（15min / 30d / 180d，无 negative cache）、`AchievementUnlocks`（5min / 30d / 365d，无 negative cache）；三者 `AllowExpiredOnFailure = true`，`MaximumPayloadBytes` 沿用 M0 决策保持 1 MiB。
- `Steam/Gateway/SteamAchievementDiagnosticCodes.cs` 固定 13 个低基数 `achievement_*` 诊断码，含 `achievement_progress_partial` 与 M0 的 `achievement_user_stats_failed`；测试断言全部唯一且符合 `^achievement_[a-z0-9_]+$`。

**协议 adapter 与纯函数 mapper（均在 `Steam/Gateway/Internal`，internal）**

- `AchievementSchemaProtocol.cs`：按 M0 决策手写 `AchievementSchemaRequest`（`appid=1`、`language=2`、`hash_only=3`）与 `AchievementSchemaResponse`（`achievements=1`、`schema_version=2`、`groups=3`、`schema_hash=4`；`Achievement` 字段 1–15、`Group` 字段 1–9），字段编号已核对 SteamKit master `SteamMsgPlayer.cs` 与 SteamDatabase proto（`min/max_progress_int` 9/10、`min/max_progress_float` 14/15）。`progress_type` 故意声明为 `int` 而非枚举，以便保留未知值。服务方法名 `Player.GetGameAchievements#1`。契约测试固定 tag 表，并双向验证与 SteamKit2 3.4.0 generated 类型的 wire 兼容（手写 request → generated request；generated response → 手写 response，扩展字段为 null）。
- `AchievementProtocolMapper.MapSchema`：空/重复 `internal_name`（Ordinal）、重复非空 `internal_key`、重复 `groupid` → `InvalidDataException`；`player_percent_unlocked` 用 `InvariantCulture` 解析，空、不可解析、NaN、Infinity、超出 [0,100] → `null` 且不丢弃该成就（在 `de-DE` 当前文化下 `"12.5"` 仍解析成功、`"12,5"` 为 null）；min/max 只在 `Int`/`Float` 时取对应字段；`schema_version`/`schema_hash` 缺失映射为 0；引用不存在 group 的 `groupid` 原样保留；保持协议顺序。
- `MapProgressSummaries`：消费 3.4.0 generated `CPlayer_GetAchievementsProgress_Response.AchievementProgress`，重复 appid 或 `unlocked > total` → `InvalidDataException`，`total == 0` 保留为该 app 的 success-empty 由 Feature 决定。`MapUnlocks`：把 M0 decoder 输出桥接到 `SteamAchievementUnlockSnapshot`，unknown 语义原样保留。

**测试与证据**

- 新增 embedded fixture `schema-empty.json`、`schema-ordinary.json`、`schema-boundary.json`（合成脱敏，无真实 appid），通过 System.Text.Json `Populate` 反序列化为手写协议类型后进入 mapper。
- 新增 Core 测试 38 个：`AchievementSchemaMapperTests`、`AchievementProgressMapperTests`、`AchievementMergeTests`（覆盖 full success、success-empty、failure、stale-success、partial、stale-progress-not-partial、localized name 不作键、Ordinal 大小写、乱序匹配、key 冲突、hidden 显示策略、overview partial）、`AchievementCacheContractTests`、`AchievementSchemaProtocolContractTests`、`SteamOwnedGameCatalogTests`。`SteamStat.Core.Tests` 由 96 → **134/134**。
- 新增 `SteamStat.Architecture.Tests/P3M1BoundaryTests`（8 个）：`Features/Achievements/**` 不含 `SteamClient`/`SteamUnifiedMessages`/`SteamKit2`/`ClientMsgProtobuf<`/`EMsg`/`ProtoContract`/`IHttpClientFactory`/Steam URL/`ISteamSessionAccessor`/`ISteamResourceCacheStore`/`SteamFeatureSnapshotStore`/`SteamOwnedGame`/EF/`SteamStat.Contracts`/`Electron`，也无局部 `ConcurrentDictionary<..., Task<...>>`；Library 不引用 Achievements；Achievements 公共类型图（递归属性/方法签名）不触及 SteamKit2、protobuf-net、EF、Contracts 程序集；公共 schema 类型属性名不含 SteamId/AccountName/Token/Session/Generation；个人 cache key 必须带 `ulong steamId` 且 scope ≠ `public`；Core 内所有 `[ProtoContract]` 类型只能位于 `SteamStat.Core.Steam.Gateway.Internal`；`SteamOwnedGameCatalog` 只读且已注册；两个端口返回 `Task<SteamGatewayResult<>>` 并接受 `SteamRefreshMode`。Architecture tests 由 41 → **49/49**。
- `dotnet build SteamStat.slnx -c Debug -p:ElectronSkipExecCommands=true`：0 warning、0 error。所有测试不联网、不使用真实账号数据。

**对后续里程碑的说明**

- M2 的 `CmAchievementSchemaSource` 应直接使用 `AchievementSchemaProtocol.ServiceMethod` + 手写 request/response 经 `SteamUnifiedMessages.SendMessage<,>` 调用，再交给 `AchievementProtocolMapper.MapSchema`；cache key/policy/诊断码已就位，不要再新建。
- 核对 SteamDatabase proto 与 SteamKit master 时发现上游已存在 `Player.GetUserAchievements`（`CPlayer_GetUserAchievements_Request { steamid, appid }` / `Response { achievements[] { internal_key, unlocked, unlock_time, progress_int, progress_float }, schema_version, schema_hash, groups[] }`）。它是纯读取的 unified service，直接以 `internal_key` 关联 `GetGameAchievements`，可能比 M0 的 binary user-stats 桥接更稳定、也不触及 `ClientGamesPlayed`。**这只是研究线索，不改变 M0 的既有决策**：M3 开始前应像 M0 一样先用 compile-contract/手写 adapter + 受控账号 smoke 验证该方法的可用性、私有资料与无成就返回，再决定是否以它替代 user-stats 路径。当前 `SteamAchievementUnlock` 已同时保留 `InternalKey?` 与 `InternalName`，两条路径都能落到同一 snapshot。

### P3-M2：schema Gateway 与 hash cache

内容：

- 实现 `CmAchievementSchemaSource`。
- 实现 SQLite cache、hash-only revalidation、full fetch、coalescing 和 stale fallback。
- 处理 language normalization、generation、payload limit。
- 增加 cache restart 和损坏 payload 测试。

出口：

- fresh cache 为 0 次 CM。
- hash 未变化不下载完整 schema。
- hash 变化原子替换；失败不删除旧成功值。
- 重启断网可读取已访问 schema。

#### M2 完成记录（2026-09-15）

**CM source 与 Gateway**

- 新增 `Steam/Gateway/Internal/CmAchievementSchemaSource.cs`：直接以 M0 手写 `AchievementSchemaRequest` / `AchievementSchemaResponse` 调用 `Player.GetGameAchievements#1`，hash-only 与 full 分别经 `ISteamCmOperationScheduler` 的低基数 operation `achievement-schema-hash` / `achievement-schema-full` 执行；优先使用调用者指定的 Ready session，否则只选择首个可用 session，不 fan-out。非 OK `EResult` 交由 `SteamResultClassifier`，空 body / mapper invalid data、handler/session 不可用均返回稳定 typed failure；映射后再次检查 generation，旧 session 的迟到结果返回 `achievement_schema_stale_session_generation`，不得进入 cache。
- 新增 `Steam/Gateway/SteamAchievementSchemaGateway.cs` 并注册为 `ISteamAchievementSchemaGateway` singleton：language 以 trim + invariant lowercase 归一化，公共 key 固定为 `achievement-schema / public / appid / language / v1`，coalescing 不含 accountName。`PreferCache` fresh hit 与 `CacheOnly` 均不访问 CM；有可保留旧值时先 hash-only，相同 hash 只原子更新 freshness metadata 且复用旧 payload，不同 hash 才 full fetch；无 cache 直接 full。
- full 成功值在写入前验证 app/language、definition internal name/key 与 group id 唯一性，并以 UTF-8 实际字节数执行 1 MiB 上限；SQLite payload 继续使用 `json-v1` wrapper，`ContentHash` 保存 Valve `schema_hash` 的 invariant decimal。hash 变化通过现有 `ISteamResourceCacheStore.UpsertAsync` 原子替换；full、mapping、payload 或持久化失败均不删除旧成功值。

**cache、失败与并发语义**

- cache 解码严格验证 payload format、UTF-8 大小、JSON、AppId、language、集合、稳定键和 `ContentHash`；损坏、语义不匹配或超限 entry 一律 safe miss，不删除、不伪装成 success-empty。hash/full 失败时，可保留 positive entry 以 SQLite stale/expired success 返回并附带上游 failure metadata；无旧值才返回 typed failure。
- 仅明确 NotFound 写入最长 6 小时 negative cache；AuthenticationRequired、Timeout、RateLimited、Offline 等不缓存。相同公共 key 复用现有 `SteamRequestCoalescer<SteamCacheKey>`；caller cancellation 只取消自己的等待，不取消其他 caller 或共享 Steam operation。
- 沿用现有 SQLite `steam_resource_cache` 表和 `EfSteamResourceCacheStore`，未新增表、列、索引、migration 或泛化 `SchemaCache`。achievement schema 的 SQLite restart round-trip 已直接覆盖 key、language、payload schema version、source、payload 与 `ContentHash`；Gateway restart + offline 测试证明 fresh 已访问 schema 为 0 次 CM。

**自动化证据**

- `SteamAchievementSchemaGatewayTests` 共 20 个测试方法（部分方法覆盖多种输入）：fresh restart/offline、hash unchanged、hash changed、full failure 保留旧值、miss/full-only、跨 account coalescing、caller cancellation、language 隔离、malformed/semantic mismatch/oversized safe miss、UTF-8 边界、写失败、CacheOnly、generation、NotFound negative cache 与 invalid arguments。M2 schema 筛选集为 37/37，SQLite store 筛选集为 7/7，P3-M1/M2 architecture 筛选集为 9/9。
- 完整 `dotnet test SteamStat.slnx` 为 290/290；`dotnet list SteamStat.slnx package --vulnerable --include-transitive` 对 8 个项目均未发现已知漏洞。
- `dotnet format SteamStat.slnx --verify-no-changes` 仍因仓库既有 `.editorconfig` 后置 `[*] indent_size = 2` 覆盖 `[*.cs] indent_size = 4`，以及未修改的 `third_party/Electron.NET` charset 诊断而退出 2；本次新增 C# 保持仓库实际 4 空格约定，未为绕过既有全仓问题修改格式或 vendored 配置。

### P3-M3：个人 progress/unlock Gateway

内容：

- 从 Library 抽出批量 progress source。
- 实现逐项 unlock source 和 personal cache。
- 实现 stable merge、partial results 和 generation check。
- Achievement overview 通过 `IOwnedGameCatalog` 读取游戏目录。
- `CmLibrarySource` 与 progress Gateway 共享 internal progress source，保持 Library 现有三个摘要字段而不形成 Feature 循环依赖。

出口：

- Library 与 Achievements 不重复实现 progress protobuf。
- 账号间 cache 隔离。
- overview 无 N+1；详情只请求当前 app。
- 逐项状态与时间有 fixture 和真实 smoke 证据。

### P3-M4：Feature、IPC 与 Host adapter

内容：

- 实现 `SteamAchievementsService`。
- 新增 `AchievementIpc`、DTO、Host handlers 和 mapper。
- 运行 generator 并更新 snapshot。
- 加 Host/IPC validation tests。

出口：

- Host 仅做绑定、调用和映射。
- renderer 取得 typed envelope，不通过异常文本判断状态。
- generator `--check` 无差异。

### P3-M5：Renderer 基础设施与成就页面

内容：

- 建立最小前端测试环境。
- 先测试实现 `useIpc`、`useAsyncResource`、`useSteamStore`。
- 新增实验性 Achievements route/page 和两种语言文案。
- 实现 overview 虚拟列表、按需详情和 stale/error UI。

出口：

- 页面不直接声明 channel/DTO。
- 多页面并发进入时账号 bootstrap 只发生一次。
- refresh 失败保留旧详情。
- 1000+ overview 不预取完整 schema。

### P3-M6：Library / Friends 回填

内容：

- Library 迁移到三个前端基础设施并消除双刷新。
- Friends 迁移，统一 snapshot/event 合并和 listener lifecycle。
- 渐进收紧后端 typed result；删除确认无调用者的旧 endpoint。
- 增加回归测试和 architecture gates。

出口：

- Login/Library/Friends 不再各自重复拉共享账号状态；Login 若仍需专用登录进度，可保留局部状态。
- Library 手动刷新只产生一轮后端同步。
- 三个页面使用一致的 loading/error/retry/stale 语义。
- Friends 实时更新行为保持不变。

### P3-M7：收口、文档与发布硬化

内容：

- 删除 spike、临时兼容和重复 progress 路径。
- 更新 Architecture、Contributing、smoke checklist。
- 完整 lint/build/test/audit。
- Release build、unpacked app、安装器和真实 Steam smoke。
- 评估 Public Data 是否进入独立后续 PR。

出口：

- 下文 Definition of Done 全部满足。
- Phase 3 结束时仍是可安装、可运行、可回滚的版本。

---

## 13. 推荐 PR 拆分

为降低单人维护风险，建议每个 PR 聚焦一个可回滚职责：

1. `test(steam): lock achievement protocol contracts`
2. `feat(steam): add achievement schema cache gateway`
3. `feat(steam): add personal achievement progress gateway`
4. `feat(ipc): expose typed achievement queries`
5. `feat(ui): add achievement vertical slice`
6. `refactor(ui): adopt shared steam resources in library and friends`
7. `docs: complete phase 3 architecture and smoke guidance`

每个 PR 都应包含对应测试和必要文档，不要把“先写全部实现、最后补测试/生成物”作为工作流。

Public Data 建议独立：

```text
feat(steam): add optional public achievement schema source
```

这样 CDN/manifest 失败时可以回滚，不影响 SQLite → CM 主路径。

---

## 14. 可观测性、安全与性能

### 14.1 结构化日志

建议字段沿用 Phase 2：

```text
Feature=Achievements
Operation
Transport=CM|Cache|PublicData
ResourceKind
Source
Freshness
FailureKind
DiagnosticCode
SessionGeneration
SchemaChanged=true|false
DefinitionCount
ElapsedMs
```

约束：

- Information 可记录单次用户触发操作的结果摘要。
- cache hit/hash unchanged 默认 Debug + metric，不逐项 Information。
- 不记录完整 schema、achievement internal name 列表、解锁时间列表或 payload。
- 不记录 accountName、SteamID、appid 为 metrics label；日志若确需 appid，只作为结构化事件属性，不进入低基数 metric dimension。
- 不记录 token、guard data、Authorization、QR secret 或原始 protobuf。

### 14.2 建议 metrics

复用现有 `steam.gateway.*`、`steam.cache.*` 指标，并在确有需要时增加低基数结果：

```text
achievement.schema.revalidation
  outcome=unchanged|changed|miss|failed
achievement.progress.partial
```

不要把 appid、language、account 或 schema hash 作为 label。

### 14.3 URL 与图片安全

schema 的 icon 字段来自外部协议，renderer 不应无条件加载任意 scheme/host：

- 只允许 HTTPS；
- 明确 Steam CDN host allowlist；
- 拒绝 user-info、localhost、IP literal 和非标准 scheme；
- 不通过 `shellOpenExternal` 打开成就 icon；
- 图片失败使用本地占位；
- 不在日志中记录完整带 query 的 URL。

如果当前 Electron CSP/图片策略不允许相应 host，应以最小 allowlist 修改并加 Host/security test，不能关闭 `WebSecurity`。

### 14.4 性能预算

建议把以下作为实现约束而非事后优化：

- overview 初始请求数：每账号 `O(appCount / 100)` summary batches，不含 full schema。
- 打开 fresh-cached 游戏：0 次 CM。
- 打开 stale-cached 游戏：通常 1 次 hash；变化时最多再 1 次 full。
- 打开 uncached 游戏：1 次 full schema + 1 次 personal unlock，二者可有界并行。
- overview DOM：虚拟窗口内元素数量，不随 1000+ games 线性增长。
- schema 映射/JSON 序列化不得在 Steam callback 中同步写数据库。

---

## 15. 常见错误与禁止做法

- 把所有游戏 schema 在首次进入页面时 `Task.WhenAll` 拉完。
- 把 `schema_hash` 同时当 Valve schema version、cache codec version 和内容 SHA。
- 使用 `(appid, language, hash)` 作为本地唯一 key，却没有 current pointer 和旧版本 cleanup。
- 把 progress cache 写成 `scopeId=public`。
- 用 accountName 作为持久个人数据 scope。
- 按 localized name 或数组位置关联 unlock 与 definition。
- progress 请求失败时返回 `[]` 并显示“没有成就”。
- 刷新前删除旧 cache。
- 为 hash unchanged 重复下载 full schema。
- 在 Library 和 Achievements 各保留一份 `GetAchievementsProgress` 调用。
- 在 Vue 页面手写 IPC channel、DTO 或 `as any`。
- `useIpc` 退化成 `invoke<T>(string, unknown)`。
- `useAsyncResource` 自动无限 retry。
- 把业务 Fresh/Stale 与页面 loading/success 混成一个枚举。
- 把所有 Steam Feature payload 放进一个巨大 Pinia store。
- 为了支持 Public Data 提前建立空接口、空仓库或不可验证的签名系统。
- 为超过 1 MiB 的 schema 关闭所有 payload 限制。
- 把完整 schema 或个人成就数据写入日志、metrics 或错误消息。
- 为读取 stats 改变用户 playing 状态而不明确告知和验证。
- 为通过 CI 删除架构测试、关闭 audit 或隐藏 warning。

---

## 16. Definition of Done

### 16.1 协议与模型

- [ ] SteamKit2 成就协议签名被 compile-contract test 固定。
- [ ] 逐项 unlock 与 schema 的 stable key 映射有 fixture 和 smoke 证据。
- [ ] 公共 schema 与个人 progress 模型、scope、TTL 完全分离。
- [ ] generated protobuf/EMsg 未离开 `Steam/Gateway/Internal`。
- [ ] 无成就、失败、partial 和 stale-success 可明确区分。

### 16.2 Gateway 与缓存

- [ ] fresh schema hit 不访问 CM。
- [ ] stale schema 先 hash-only；hash unchanged 不下载 full payload。
- [ ] hash changed 原子替换，失败保留旧成功值。
- [ ] schema key 使用 public scope；progress key 使用稳定 SteamID。
- [ ] coalescing、scheduler、classifier、generation 和 cancellation 复用 Phase 2 机制。
- [ ] payload 大小、损坏、unknown enum 和 invalid percent 均有防御测试。
- [ ] 重启断网可读取已访问 schema/progress，并显示最后成功时间。

### 16.3 Feature 与 IPC

- [ ] Achievements Feature 不引用 raw SteamKit/HTTP/EF/Electron。
- [ ] overview 不对每个 app 拉 full schema。
- [ ] Feature 按稳定内部标识合并定义与进度。
- [ ] IPC 请求有 account/appId 边界验证。
- [ ] IPC 返回 typed envelope，Host 只负责绑定与映射。
- [ ] preload、`ipc.d.ts`、snapshot 全部由 generator 生成且 check 通过。

### 16.4 Renderer

- [ ] `useIpc.ts` 不复制 channel/type map，可在测试中注入 fake。
- [ ] `useAsyncResource.ts` 支持 initial/refresh/error/latest-wins，并在刷新失败时保留旧数据。
- [ ] `useSteamStore` 合并账号 bootstrap，且不保存凭据。
- [ ] Achievements 页面支持多账号、overview、按需详情、stale/error/empty 和 retry。
- [ ] 1000+ game overview 使用虚拟列表，不预取全部 schema。
- [ ] hidden achievement、时间、图片失败、浅色/深色和双语言经过验证。
- [ ] 前端 unit test 进入 CI。

### 16.5 Friends / Library 回填

- [ ] Library/Friends 使用共享 IPC/async/store 基础设施。
- [ ] Library 单次手动同步不会触发第二轮隐式刷新。
- [ ] Library 不再直接操作 achievement progress protobuf。
- [ ] Friends listener 注册/移除与页面生命周期一致。
- [ ] 刷新失败不清空旧 Library/Friends 数据。

### 16.6 交付

- [ ] Core、Host、Architecture 和前端测试全部通过。
- [ ] lint、build、generator check、NuGet audit 通过，Debug/Release 无新增 warning。
- [ ] smoke checklist 覆盖成就、多账号、离线、过期 token、hash unchanged/changed 和关闭。
- [ ] `docs/ARCHITECTURE.md`、`CONTRIBUTING.md`、smoke checklist 与实现同步。
- [ ] Windows 安装包和 unpacked app 可启动并完成成就 smoke。

---

## 17. 验证命令

从仓库根目录执行：

```bash
# 前端
pnpm run lint:ci
pnpm run test:unit
pnpm run build

# .NET
dotnet restore SteamStat.slnx -p:ElectronSkipExecCommands=true
dotnet build SteamStat.slnx -c Debug --no-restore -p:ElectronSkipExecCommands=true
dotnet test SteamStat.slnx -c Debug --no-build -p:ElectronSkipExecCommands=true

# IPC 生成物
dotnet run --project tools/GenerateIpcContracts -- --check

# 依赖审计
dotnet list ElectronNet/ElectronNet/ElectronNet.csproj package --vulnerable --include-transitive
pnpm audit --prod
```

Core 快速反馈：

```bash
dotnet test backend/tests/SteamStat.Core.Tests/SteamStat.Core.Tests.csproj -c Debug
dotnet test backend/tests/SteamStat.Architecture.Tests/SteamStat.Architecture.Tests.csproj -c Debug -p:ElectronSkipExecCommands=true
```

涉及 cache adapter 上限或数据库结构时，额外运行现有 migration/schema/backup tests，并只对 fixture/copy 操作，不对真实用户数据库试迁移。

Release 前：

```bash
pnpm run build:win
```

随后按 [Smoke 清单](./smoke-checklist.md) 记录真实应用结果。

> 注：`test:unit` 是 Phase 3 应新增的 script；在测试基础设施落地前该命令尚不存在，不能把当前缺失误判为构建故障。

---

## 18. 外部技术依据与复核原则

- [SteamDatabase `steammessages_player.steamclient.proto`](https://github.com/SteamDatabase/Protobufs/blob/master/steam/steammessages_player.steamclient.proto)：可用于核对 `GetGameAchievements`、`hash_only`、`schema_hash` 和展示字段。
- [SteamKit2 repository](https://github.com/SteamRE/SteamKit)：用于核对当前库的 handler、generated protobuf 和 callback/job 约定。
- [Vue composables guidance](https://vuejs.org/guide/reusability/composables.html)：用于约束 composable 的状态与生命周期。
- [Pinia core concepts](https://pinia.vuejs.org/core-concepts/)：用于共享 Steam 状态设计。

这些协议不是 Valve 面向 Steam Stat 提供的稳定公开 API。外部 protobuf 仓库只能作为研究证据，最终实现必须以本项目锁定的 SteamKit2 3.4.0、compile-contract tests、脱敏 fixture 和真实 smoke 为准。未来升级 SteamKit2 时，应先让协议契约测试暴露变化，再有意更新 mapper 与 cache schema version，不能靠宽泛 catch 隐藏 breaking change。

---

## 19. 实施决策摘要

Phase 3 最稳妥的主线是：

```text
P3-M0 证明协议
  → P3-M1 固定稳定模型
  → P3-M2 做对 schema hash cache
  → P3-M3 做对个人 progress
  → P3-M4 打通 generated IPC
  → P3-M5 用新前端基础设施交付页面
  → P3-M6 回填 Library/Friends
  → P3-M7 完整验证并发版
```

优先级始终是：

1. 数据正确与账号隔离；
2. 离线/stale 与失败语义；
3. 请求数量和缓存命中；
4. typed IPC 与可测试 UI 状态；
5. 页面视觉与后续统计功能；
6. Public Data 扩展。

只要这条顺序不反转，Phase 3 才会成为后续 SaveScope、同步和其他 Steam 功能可以复制的真实范例，而不是又一个只能继续维护的特例。
