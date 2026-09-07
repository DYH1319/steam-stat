# Steam Stat 手工 Smoke Checklist

适用于影响 Host、Electron、IPC、持久化、设置、日志、后台任务或打包的 PR。执行者应复制本文件到 PR 描述或测试记录中，勾选适用项并填写证据；不适用项需写明原因。

## 验证信息

- Commit / tag：
- Windows 版本：
- .NET / Node / pnpm 版本：
- Debug 或 Release/安装包：
- 新用户数据目录或既有数据库：
- 执行人和日期：

## 自动化前置检查

- [ ] `pnpm install --frozen-lockfile`
- [ ] `pnpm run lint:ci`
- [ ] `pnpm run build`
- [ ] `dotnet build SteamStat.slnx -c Debug -p:ElectronSkipExecCommands=true`
- [ ] `dotnet test SteamStat.slnx -c Debug --no-build -p:ElectronSkipExecCommands=true`
- [ ] `dotnet run --project tools/GenerateIpcContracts -- --check`
- [ ] `dotnet list ElectronNet/ElectronNet/ElectronNet.csproj package --vulnerable --include-transitive` 无 High/Critical
- [ ] `pnpm run build:win` 至少在 Release/里程碑 PR 执行一次

## 启动、窗口与退出

- [ ] 普通 Debug 启动显示主窗口，renderer、preload、托盘和 IPC 注册无错误。
- [ ] `--silent-start` 不显示主窗口或 DevTools，但托盘和后台任务正常。
- [ ] 主窗口关闭后 Electron、dotnet 和 Vite 子进程均退出。
- [ ] 托盘 Exit 后 Electron、dotnet 和 Vite 子进程均退出。
- [ ] renderer 的退出按钮走同一关闭路径，不留下后台进程。
- [ ] Vite 启动失败或 120 秒未 ready 时有 Error 日志，进程不空转 CPU。
- [ ] 每次正常退出只出现一次 `Cleanup completed`。
- [ ] 退出时 updater 与运行状态 `BackgroundService` 被取消并等待，没有 shutdown timeout 或 disposed-object 错误。

证据：

## 数据库与文件

- [ ] 全新 UserData 首次启动创建 `Database/steam-stat.db`、`Settings/app-settings.json` 和 `Logs/`。
- [ ] 无 pending migration 的既有数据库正常启动，原数据可读。
- [ ] 有 pending migration 时先生成/替换 `steam-stat.bak`，再升级数据库。
- [ ] 模拟迁移失败时保留原数据库和可用备份，不启动后续写数据任务。
- [ ] Steam 的 `loginusers.vdf`、`libraryfolders.vdf` 和有效 ACF 可读取；单个损坏 ACF 不使整次扫描崩溃。

证据：

## IPC 与 renderer 安全

- [ ] 41 个 invoke、13 个 send、4 个 Host-to-renderer event 的核心页面调用无 channel-not-found。
- [ ] 缺失字段、错误类型、超长字符串、越界数字和未知字段返回受控错误，不使 Host 崩溃。
- [ ] renderer reload 后 listener 不重复注册。
- [ ] 登录用户、登录进度、好友和自动更新事件 payload 与当前前端兼容。
- [ ] DevTools 确认 `nodeIntegration=false`、`contextIsolation=true`、`webSecurity=true`、sandbox 生效。
- [ ] `shell:openExternal` 拒绝 `javascript:`、`data:`、`file:` 和带 user-info 的 URL。
- [ ] `shell:openPath` 拒绝任意路径，只允许已知 Steam 安装/用户目录中的现存路径。

证据：

## 设置与后台任务

- [ ] 修改主题、语言、缩放、开机启动和静默启动设置后行为与持久化值一致。
- [ ] 开关自动更新后 schedule 启停一次，不创建重复检查循环。
- [ ] 手动检查更新、下载更新和更新事件状态正确；重复点击不会并发发起同类操作。
- [ ] 修改应用运行状态检查间隔后旧 schedule 被取消，新间隔生效。
- [ ] 禁用应用运行状态检查后不再写使用记录；重新启用后只有一个循环。
- [ ] 应用启动/停止产生正确使用记录，结束和作废记录操作可用。

证据：

## Steam 实验性功能

- [ ] 密码登录进入原有状态，日志和事件不含密码。
- [ ] QR 登录进入原有状态，日志和事件不含 QR secret/challenge。
- [ ] 已保存 token 登录进入原有状态，日志和事件不含 access/refresh token。
- [ ] guard code/device confirmation 路径可用，日志不含 guard data。
- [ ] 用户主动退出不会触发自动重连。
- [ ] 连接中断按原策略重连；退出时 callback loop 和 reconnect timer 被取消并等待。
- [ ] 好友刷新、好友状态跟踪和事件推送可用。
- [ ] 游戏库单账号/全部账号刷新可用。
- [ ] session 结束后 Friends/Library cache 被清理；多账号 callback/cache 不串号。
- [ ] 500+ 好友、1000+ 游戏时无明显 UI 卡死或重复事件。

证据：

## 结构化日志与秘密

- [ ] Electron ready 前的故障由 bootstrap logger 输出。
- [ ] final 日志位于 `<UserData>/Logs/steam-stat-YYYYMMDD.log`，不写安装目录。
- [ ] 日志含时间、级别、`SourceContext`、结构化 properties；异常含 stack trace。
- [ ] 日志达到 10 MiB 或跨天后 rolling 生效，最多保留 14 个文件。
- [ ] Debug 同时输出 console；Release/安装包可写文件并在退出时 flush。
- [ ] 在日志目录全文搜索确认无 password、access token、refresh token、guard data、Authorization header、QR secret/challenge。
- [ ] 产品源代码搜索 `Console.WriteLine`、`Console.Write(`、`Serilog.Log.` 均为 0。

证据：

## Release / 安装包

- [ ] `pnpm run build:win` 成功生成 NSIS 安装器、block map、与版本 channel 对应的 metadata YAML（M7 为 `M7.yml`）和 unpacked app。
- [ ] 安装、首次启动、覆盖安装和卸载路径正常。
- [ ] 打包 runtime 版本与项目锁定 Electron 版本一致且处于官方支持期。
- [ ] 安装包环境重复执行启动、登录或核心只读页面、自动更新事件和退出检查。
- [ ] 安装目录只读时应用仍将数据库、设置、临时文件和日志写入 UserData。

证据：

## 结论

- [ ] 全部适用项通过。
- [ ] 失败项已附 issue/日志/截图，且不被误标为通过。
- [ ] 未发现凭据、真实用户数据库或日志文件进入 git diff。
