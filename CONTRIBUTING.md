# 贡献指南 | Contributing

感谢你愿意为 Steam Stat 出一份力！

---

## 快速开始

### 环境要求

| 工具 | 版本 |
| --- | --- |
| [Node.js](https://nodejs.org/) | 22.21.1 |
| [pnpm](https://pnpm.io/) | 10.18.1 |
| [.NET SDK](https://dotnet.microsoft.com/download) | 10.0+ |
| 操作系统 | Windows 10 / 11 (x64) |

### 克隆与安装

本仓库通过 submodule 使用修改过的 Electron.NET，构建 Electron Host 前必须初始化它：

```bash
git clone --recurse-submodules https://github.com/DYH1319/steam-stat.git
cd steam-stat

# 已克隆但尚未初始化 submodule 时执行：
git submodule update --init --recursive

pnpm install --frozen-lockfile
dotnet restore SteamStat.slnx -p:ElectronSkipExecCommands=true
```

### 运行（开发模式）

在仓库根目录执行：

```bash
dotnet run --project ElectronNet/ElectronNet/ElectronNet.csproj --launch-profile "Development (Dotnet First)"
```

.NET 进程会启动 Electron runtime 和 Vite dev server，无需另开前端终端。IDE 若注入了 `ELECTRON_RUN_AS_NODE=1`，只在当前启动进程中移除该变量，不要修改用户或仓库级环境配置。

### 构建安装包

```bash
pnpm run build:win
```

产物位于 `release/`。打包脚本会先构建前端，再 publish .NET Host，并由 electron-builder 使用锁定的 Electron 版本生成 Windows 安装包。

---

## Electron.NET 依赖

默认从 `third_party/Electron.NET` 的固定 submodule revision 构建。路径由根 `Directory.Build.props` 的 `$(ElectronNetSourcePath)` 决定。

如需临时使用本地 Electron.NET 源码，在仓库根创建已被忽略的 `Directory.Build.local.props`：

```xml
<Project>
    <PropertyGroup>
        <ElectronNetSourcePath>D:\path\to\your\Electron.NET</ElectronNetSourcePath>
    </PropertyGroup>
</Project>
```

也可以单次覆盖：`dotnet build SteamStat.slnx -p:ElectronNetSourcePath=...`。不要提交本机绝对路径。

---

## 提交前自检

`SteamStat.slnx` 是唯一主 solution。请从仓库根目录依次运行：

```bash
# 前端类型检查、ESLint、Stylelint 与生产构建
pnpm run lint:ci
pnpm run build

# 后端恢复、构建和完整测试；跳过 Electron runtime 安装命令以匹配 CI
dotnet restore SteamStat.slnx -p:ElectronSkipExecCommands=true
dotnet build SteamStat.slnx -c Debug --no-restore -p:ElectronSkipExecCommands=true
dotnet test SteamStat.slnx -c Debug --no-build -p:ElectronSkipExecCommands=true

# 生成 IPC 文件必须无差异
dotnet run --project tools/GenerateIpcContracts -- --check

# NuGet 漏洞审计；High/Critical 同时由根构建配置阻断
dotnet list ElectronNet/ElectronNet/ElectronNet.csproj package --vulnerable --include-transitive
```

若只修改 Core，可先运行不需要 submodule 或 Electron runtime 的快速测试：

```bash
dotnet test backend/tests/SteamStat.Core.Tests/SteamStat.Core.Tests.csproj -c Debug
```

架构边界可以单独验证：

```bash
dotnet test backend/tests/SteamStat.Architecture.Tests/SteamStat.Architecture.Tests.csproj -c Debug -p:ElectronSkipExecCommands=true
```

涉及启动、IPC、设置、更新、Steam 功能或关闭流程时，还必须执行并记录 [`docs/dev/smoke-checklist.md`](docs/dev/smoke-checklist.md) 中相关项目。Release PR 至少执行一次 `pnpm run build:win`。

---

## 测试约定

测试分为三层：

- `backend/tests/SteamStat.Core.Tests/`：纯 Core 测试，不联网、不依赖 Steam、submodule 或 Electron runtime。
- `ElectronNet/ElectronNet.Tests/`：Host adapter、SQLite、IPC compatibility、后台服务和安全策略测试。
- `backend/tests/SteamStat.Architecture.Tests/`：依赖方向、日志、静态状态、IPC、安全和生成边界。

硬性要求：

- 单元测试不得访问真实网络、真实 Steam 安装或真实用户数据库。
- 需要 Steam 目录结构时使用 `ElectronNet/ElectronNet.Tests/TestSupport/TempSteamLayout` 和 `Fixtures/`。
- 新缺陷优先先写失败的 characterization/regression test，再修实现。
- 不得通过删除架构测试、`NoWarn` 或降低 NuGet audit 级别绕过门禁。
- IPC channel、preload API 和 TypeScript wire type 必须先修改 C# Contracts，再运行生成器；不要手改生成文件。

---

## 架构与安全规则

修改代码前请阅读 [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md)。核心规则包括：

1. Core 不引用 Electron、Host 或 Windows 实现；Contracts 不引用 Core、EF、SteamKit2 或 Electron。
2. Electron API 只出现在 Host；业务 UI 通知经 typed event 和唯一 `ElectronIpcEventForwarder`。
3. 持有状态或生命周期的服务必须由 DI 管理；后台工作必须可取消、可等待、可释放。
4. 产品日志只使用 `ILogger<T>`；禁止 `Console.WriteLine`、Serilog static logger 和凭据日志。
5. 数据库操作使用 `IDbContextFactory<AppDbContext>` 创建短生命周期 Context，并保持 migration/schema/path 兼容。
6. Renderer 输入均不可信；IPC 需执行类型、范围、协议和路径授权校验。

---

## 提交规范

遵循 [Conventional Commits](https://www.conventionalcommits.org/)：

```text
feat: 新功能
fix: 修复缺陷
refactor: 重构（不改变外部行为）
perf: 性能优化
docs: 文档
test: 测试
chore: 构建 / 工具链
```

可以用 `pnpm run commit` 生成提交信息。

---

## 提交 PR 之前

1. 新功能先开 Issue，并确认符合 README 的 Non-goals。
2. 一个 PR 聚焦一个可回滚的职责，代码、测试、生成物和文档保持原子一致。
3. 附上适用的自动化命令结果和 smoke checklist 证据。
4. 涉及 UI 时附浅色、深色截图。
5. 新增用户可见文案时同步更新 `src/locales/zh-CN.json` 与 `src/locales/en-US.json`。
6. 检查 diff 中没有 token、密码、Authorization header、QR secret、真实用户数据库或日志文件。
