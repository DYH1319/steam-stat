# 架构说明

本文描述 Steam Stat 的**当前**结构、已知技术债，以及正在推进的重构方向。
它会随重构进度更新——如果你发现文档与代码不符，请提 Issue 或直接提 PR 修正。

---

## 1. 进程结构

```
┌─────────────────────────────────────────────────────────┐
│  Electron 主进程（由 Electron.NET 托管）                 │
│                                                          │
│  ┌────────────────────────────────────────────────┐     │
│  │  .NET 后端（ElectronNet/ElectronNet/）          │     │
│  │  Program.cs      窗口 / 托盘 / 生命周期          │     │
│  │  Services/       业务逻辑（当前全为 static）     │     │
│  │  Jobs/           定时任务                        │     │
│  │  AppDbContext    EF Core + SQLite               │     │
│  └────────────────────────────────────────────────┘     │
│                        ▲                                 │
│                        │ IPC（preload.mjs 暴露的通道）   │
│                        ▼                                 │
│  ┌────────────────────────────────────────────────┐     │
│  │  渲染进程：Vue 3 + Vite（src/）                 │     │
│  │  基于 Fantastic Admin 模板                      │     │
│  └────────────────────────────────────────────────┘     │
└─────────────────────────────────────────────────────────┘
```

- 启动方式是 **DotNet-First**：入口是 .NET 进程，由它拉起 Electron
- 开发模式下 .NET 进程会自动启动 Vite dev server（见 `Program.StartViteDevServer`）
- 生产环境加载打包后的 `dist/index.html`

---

## 2. 数据来源

Steam 数据有四个来源，成本与可靠性差别很大：

| 来源 | 用途 | 位置 | 备注 |
| --- | --- | --- | --- |
| **本地文件（VDF / ACF）** | 已登录用户、库目录、已安装应用 | `LocalFileService` | 最可靠，无网络依赖 |
| **Windows 注册表** | Steam 安装路径、当前登录用户、应用运行状态 | `LocalRegService` | Windows 专属 |
| **进程扫描** | Steam 是否在运行 | `LocalProcessService` | Windows 专属 |
| **SteamKit2（CM 协议）** | 好友、游戏库、家庭共享、成就进度、富文本状态 | `SteamLibraryService` / `SteamFriendsService` / `SteamRichPresenceService` | 需登录；**国内网络下通常可用** |
| **HTTP（Steam Web / Store API）** | 应用名兜底、愿望单、头像 | `SteamAppService` / `SteamUserService` | **国内经常不可达**，且有限流 |

> **重要**：`store.steampowered.com`、`api.steampowered.com` 在国内经常不通，而 SteamKit2 使用的 CM 协议
> 是 Steam 客户端自己的协议，通常可用。因此**能走 CM 就不要走 HTTP**。
> 例如 `PICSGetProductInfo` 可以替代 `store.steampowered.com/api/appdetails`。

HTTP 请求统一走 `Helpers/HttpClientProvider` 提供的共享 `HttpClient`，不要再 `new HttpClient()`。

---

## 3. 目录速查

```
src/                          Vue 前端
  views/steam/                各功能页面（单文件较大，待拆分）
  router/modules/steam.ts     路由定义，含 meta.experimental 标记
  utils/experimental.ts       实验性功能开关
  types/ipc.d.ts              IPC 类型定义（手工维护，与后端无强约束）
  locales/                    i18n（zh-CN / en-US）
  ui/ layouts/ iconify/       Fantastic Admin 模板代码，一般不改

ElectronNet/ElectronNet/      .NET 后端
  Program.cs                  入口、窗口、托盘、生命周期
  Services/IpcMainService.cs  所有 IPC 通道注册的唯一入口
  Services/                   业务服务
  Helpers/                    VDF 解析、HttpClient、SteamID 换算等
  Models/LocalFiles/          VDF / ACF 的 DTO
  Migrations/                 EF Core 迁移
  Resources/preload.mjs       渲染进程可见的 IPC 桥

ElectronNet/ElectronNet.Tests/  单元测试（不联网、不依赖 Steam 安装）
third_party/Electron.NET/       submodule，修改过的 Electron.NET
docs/research/                  研究草稿与遗留代码，不参与构建
```

---

## 4. 实验性功能开关

尚未稳定的模块（Steam 登录 / 好友 / 游戏库）默认隐藏，需在「设置 → 实验性功能」中开启。

实现方式：

1. 路由上打 `meta.experimental: true`（`src/router/modules/steam.ts`）
2. `src/utils/experimental.ts` 持有开关值
3. `src/main.ts` 在 `app.use(router)` **之前**读取设置并写入开关
   （vue-router 安装时会触发首次导航，动态路由在守卫里生成，所以必须早于安装）
4. `src/router/guards.ts` 用 `filterExperimentalRoutes()` 过滤后再交给 `generateRoutesAtFront`
5. 菜单派生自 `routeStore.routesRaw`，因此自动跟随过滤结果

后端持久化字段：`AppSettings.ExperimentalFeatures`。切换开关后会重载渲染进程，因为动态路由需要重新注册。

给一个新功能加开关，只需要在它的路由 `meta` 里加 `experimental: true`。

---

## 5. 已知技术债

以下都是**已确认存在**的问题，按影响排序。想动手的话这里就是清单。

### 5.1 服务全是 `static class`，没有 DI

`Services/` 下 20 多个类全部是 `static`，持有静态可变状态，彼此按具体类型直接调用。
后果：

- 无法替换实现，无法 mock，业务逻辑基本无法单元测试
- 已出现依赖环：`SteamLibraryService → SteamAppService → GlobalStatusService`、
  `SteamFriendsService → SteamAppService`

### 5.2 业务逻辑直接调用 UI

`Electron.IpcMain.Send(...)` 出现在 4 处业务服务深处：

- `SteamLoginService.SendEvent`
- `SteamFriendsService.SendFriendsUpdateEvent`
- `SteamUserService.SyncDb`
- `UpdateService.SendUpdaterEvent`

这条是「Core 层无法脱离 Electron 测试」的直接原因。目标是换成事件总线，由 Host 层统一转发。

### 5.3 IPC 契约在三处手抄

同一批通道名同时出现在：

- `ElectronNet/ElectronNet/Resources/preload.mjs`（JS）
- `ElectronNet/ElectronNet/Services/IpcMainService.cs`（C#）
- `src/types/ipc.d.ts`（TS）

改名没有任何编译期保护，只会在运行时报错。目标是从 C# 单一来源代码生成另外两份。

### 5.4 `AppDbContext` 是 600+ 行的 God Object

6 张表的 Fluent 配置全在一个文件里，连接串硬编码在 `OnConfiguring`，且是手写单例。
每加一个功能都要改这个文件，天然是冲突点。目标是每个功能自带 `IEntityTypeConfiguration<T>`。

### 5.5 没有限流 / 持久缓存 / HTTP 重试

- 无任何 rate limiter，Steam Web API 实际限制约 200 请求 / 5 分钟
- 缓存（`_userLibraryCache`、`_userFriendsData`）只在内存里，**重启即失效**，每次启动都重新拉取
- HTTP 请求失败即放弃，没有重试

### 5.6 日志只有 `Console.WriteLine`

200+ 处，靠 `ConsoleLogPrefix` 里的字符串前缀区分模块，没有日志级别、没有结构化字段。

### 5.7 前端重复代码

7 个 Steam 页面各自实现了一遍 loading 状态、错误 toast（52 处结构相同）、刷新逻辑、
最后刷新时间显示。没有 Steam 领域的 Pinia store，多个页面重复拉取同一份数据。

### 5.8 大列表没有真正的虚拟滚动

`friends.vue` / `library.vue` 目前靠 `content-visibility: auto` 跳过视口外元素的布局与绘制，
DOM 节点仍然全部存在。数千条目时仍有内存压力。`app.vue` 用的是 `el-table-v2`，是真虚拟滚动。

### 5.9 跨平台阻塞项

`RuntimeIdentifier` 锁定 `win-x64`；注册表读取、`System.ServiceProcess`、DPAPI 加密
（`TokenProtectionService`）、进程名扫描都是 Windows 专属。SteamKit2 与 VDF/ACF 解析本身跨平台。

---

## 6. 重构方向

目标形态：**Core 层不依赖 Electron，Electron 只作为外壳**。

```
SteamStat.Core/                 不引用 ElectronNET，可独立测试
  Abstractions/                 IEventBus / ISecretStore / ISteamInstallLocator ...
  Steam/Session/                SteamKit2 会话生命周期（唯一持有 SteamClient 的地方）
  Steam/Gateway/                统一出口：限流 + 重试 + 持久缓存 + 请求合并
  Features/<切片>/              功能垂直切片，彼此不直接引用
  Persistence/
SteamStat.Platform.Windows/     注册表 / 进程 / DPAPI
SteamStat.Contracts/            IPC 通道与 DTO 的单一来源
SteamStat.Host.Electron/        薄壳：DI 装配 + IPC 注册
```

三条计划用架构测试强制的规则：

1. `SteamStat.Core` 不得引用 `ElectronNET.API`
2. Feature 切片之间不得直接 `using`，跨切片走事件总线或只读接口
3. 业务代码中不得出现 `Electron.IpcMain.Send`

完整路线见项目规划文档。

---

## 7. 添加一个新功能的推荐顺序

1. 先确认它通过 README 里 **Non-goals** 的判据：*Steam 自己不保存这个数据吗？*
2. 数据能从 CM 协议拿就别走 HTTP（见第 2 节）
3. 后端：`Services/` 加服务 → `IpcMainService` 注册通道 → `preload.mjs` 暴露 → `ipc.d.ts` 补类型
4. 前端：`views/steam/` 加页面 → `router/modules/steam.ts` 加路由，**新功能一律先打 `experimental: true`**
5. i18n：`zh-CN.json` 与 `en-US.json` 同步补 key
6. 测试：能抽成纯函数的解析 / 计算逻辑，放到 `ElectronNet.Tests` 里
