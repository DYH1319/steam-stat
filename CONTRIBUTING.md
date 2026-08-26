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

本仓库使用 git submodule 引入了一个修改过的 [Electron.NET](https://github.com/DYH1319/Electron.NET)，
**必须带 submodule 克隆**，否则 .NET 项目无法构建：

```bash
git clone --recurse-submodules https://github.com/DYH1319/steam-stat.git
cd steam-stat

# 如果已经克隆过但没带 --recurse-submodules：
git submodule update --init --recursive

pnpm install
```

### 运行（开发模式）

```bash
cd ElectronNet/ElectronNet
dotnet run -lp "Development (Dotnet First)"
```

.NET 进程会自动拉起 Vite 开发服务器，无需另开终端。

### 构建安装包

```bash
pnpm run build:win
```

---

## 关于 Electron.NET 依赖

项目依赖一个对 `ElectronNET.API` 做了少量修改的分支（主要是给 `IpcMain` 增加 `Handle` / `HandleOnce` /
`RemoveHandler` 以及返回 `Task<object>` 的重载）。

- 默认从 `third_party/Electron.NET` 这个 **固定版本的 submodule** 构建
- 路径由仓库根目录 `Directory.Build.props` 里的 `$(ElectronNetSourcePath)` 决定

如果你在本地另有一份 Electron.NET 源码想要指向，在仓库根目录创建
`Directory.Build.local.props`（已在 `.gitignore` 中）：

```xml
<Project>
    <PropertyGroup>
        <ElectronNetSourcePath>D:\path\to\your\Electron.NET</ElectronNetSourcePath>
    </PropertyGroup>
</Project>
```

也可以临时用命令行覆盖：`dotnet build -p:ElectronNetSourcePath=...`

---

## 提交前自检

CI 会在每个 PR 上跑以下检查，请先在本地跑通：

```bash
# 前端：类型检查 + ESLint + Stylelint（不自动修复，与 CI 一致）
pnpm run lint:ci

# 前端：自动修复版本
pnpm run lint

# 后端：构建 + 测试
dotnet test ElectronNet/ElectronNet.Tests/ElectronNet.Tests.csproj
```

---

## 测试约定

测试放在 `ElectronNet/ElectronNet.Tests/`，使用 NUnit + FluentAssertions。

**硬性要求**：

- 不依赖网络
- 不依赖本机安装的 Steam
- 不依赖 Electron 运行时
- 单个测试应在毫秒级完成

需要读取 Steam 目录结构的测试，用 `TestSupport/TempSteamLayout` 在临时目录里搭建仿真结构，
样本文件放在 `Fixtures/` 下（会自动复制到输出目录）。参考 `Services/LocalFileServiceTests.cs`。

> 历史上曾有一批「测试」实际是会连真实 Steam 服务器的 SteamKit2 sample，导致测试要跑 9 分钟。
> 它们已被移到 `docs/research/steamkit2-samples/`。请不要再往测试项目里放这类代码。

---

## 提交规范

遵循 [Conventional Commits](https://www.conventionalcommits.org/)：

```
feat: 新功能
fix: 修复缺陷
refactor: 重构（不改变外部行为）
perf: 性能优化
docs: 文档
test: 测试
chore: 构建 / 工具链
```

可以用 `pnpm run commit` 走交互式提交。

---

## 提交 PR 之前

1. 先开 Issue 讨论，尤其是新功能——请先看 README 里的 **Non-goals**，避免做了之后无法合并
2. 一个 PR 只做一件事
3. 确保 `pnpm run lint:ci` 与 `dotnet test` 均通过
4. 涉及 UI 的改动请附截图（浅色 + 深色）
5. 新增用户可见文案，两个语言文件都要加：`src/locales/zh-CN.json` 与 `src/locales/en-US.json`

---

## 架构

动手改代码前建议先读 [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md)，里面写了当前结构、
已知的技术债，以及正在进行的重构方向。
