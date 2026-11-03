# 发布渠道与包管理器清单

Steam Stat 的发布产物由私有 UI 仓的 `build-and-release.yml` 构建，上传到本仓库（`DYH1319/steam-stat`）的 Releases。
本目录存放各包管理器的**清单模板**；CI 在发版时用 `render-manifests.mjs` 填入版本号与 SHA-256，
渲染结果作为 `Steam-Stat-<版本>-packaging.zip` 附在 Release 中。向外部仓库提交仍需人工完成（见下文）。

## 发布产物

| 文件 | 平台 | 用途 | 更新方式 |
| --- | --- | --- | --- |
| `Steam-Stat-Setup-<v>.exe` + `latest.yml` / `beta.yml` | Windows | NSIS 安装包（winget 也使用它） | 应用内自动更新 |
| `Steam-Stat-Portable-<v>-win-x64.zip` | Windows | 便携版，数据保存在程序目录的 `data/`（Scoop 使用它） | 提示新版本，手动下载 |
| `Steam-Stat-<v>-x86_64.AppImage` + `latest-linux.yml` / `beta-linux.yml` | Linux / Steam Deck | 单文件，免安装 | 应用内自动更新 |
| `Steam-Stat-<v>-linux-x64.tar.gz` | Linux | 解压即用（AUR / Flatpak 使用它） | 提示新版本，手动下载 |

应用按 `distribution.json`（程序目录）与运行环境（`APPIMAGE`、`FLATPAK_ID`）判断发行形态，见
`ElectronNet/ElectronNet/Hosting/AppDistribution.cs`：

```json
{ "kind": "scoop", "portableData": true, "updateCommand": "scoop update steam-stat" }
```

- `kind`：`installer` / `portable` / `appimage` / `archive` / `flatpak` / `aur` / `scoop` / `winget`，或其他自定义值。
- `portableData`：为 `true` 时数据目录为程序目录下的 `data/`。
- `updateCommand`：由包管理器更新时在设置页展示的命令；有该字段的自定义 `kind` 视为包管理器安装。

## 更新通道

- **stable**：只接收正式版（tag 形如 `v4.1.0`）。
- **beta**：同时接收 `beta` / `rc` 预发布（tag 形如 `v4.1.0-beta.1`）与正式版；不接收 `alpha`。

预发布 tag 在公开仓以 **Pre-release** 发布。electron-updater 使用 GitHub provider：beta 通道读取 `releases.atom`
找到最新的 beta / 正式版，再读取该 Release 中的 `beta*.yml`（不存在时回退 `latest*.yml`）。无法应用内安装的发行形态
（便携版、tar.gz、包管理器）由应用直接比较 `releases.atom` 中的版本号。

## 渲染清单

```bash
node packaging/render-manifests.mjs --version 4.1.0 --artifacts release --out release/packaging
```

缺少某个产物时，依赖它的清单会被跳过（例如本地只构建了 Linux 时不会生成 winget / Scoop 清单）。

## 提交到各渠道（人工）

### winget（`microsoft/winget-pkgs`）

渲染结果位于 `winget/manifests/d/DYH1319/SteamStat/<v>/`，与 winget-pkgs 仓库目录一致。

```powershell
winget validate --manifest winget\manifests\d\DYH1319\SteamStat\<v>
winget install --manifest winget\manifests\d\DYH1319\SteamStat\<v>   # 本地试装
```

Fork `microsoft/winget-pkgs`，把目录复制到同一路径后提 PR。也可以用 `wingetcreate update DYH1319.SteamStat --version <v> --urls <安装包 URL> --submit`。
预发布版本不要提交到 winget。

### Scoop

`scoop/steam-stat.json` 自带 `checkver` / `autoupdate`。建议建一个 bucket 仓库（如 `DYH1319/scoop-bucket`），首次提交该文件后，
Scoop 的 Excavator（或 bucket 模板自带的 GitHub Action）会自动跟进新版本。

```powershell
scoop bucket add dyh1319 https://github.com/DYH1319/scoop-bucket
scoop install dyh1319/steam-stat
```

Scoop 版使用便携包，`persist: data` 保证升级后数据不丢失。

### Flatpak（Flathub）

`flatpak/` 下有清单、`.desktop` 与 AppStream metainfo。本地验证：

```bash
flatpak install flathub org.freedesktop.Platform//24.08 org.freedesktop.Sdk//24.08 org.electronjs.Electron2.BaseApp//24.08
flatpak-builder --user --install --force-clean build-dir flatpak/asia.dyh1319.steamstat.yml
flatpak run asia.dyh1319.steamstat
```

首次上架按 <https://docs.flathub.org/docs/for-app-authors/submission> 向 `flathub/flathub` 提 PR；之后在 `flathub/asia.dyh1319.steamstat` 仓库更新清单。

Flatpak 沙箱的限制：

- 看不到宿主进程：运行中的游戏只能依赖 Steam 写入 `registry.vdf` 的标记，检测可能比原生版慢。
- 关闭 / 启动 Steam 需要 `org.freedesktop.Flatpak` 权限（经 `flatpak-spawn --host`），Flathub 审核可能要求说明用途。
- Chromium 沙箱需要 zypak，当前以 `STEAM_STAT_NO_SANDBOX=1` 关闭。

### AUR（`steam-stat-bin`）

```bash
git clone ssh://aur@aur.archlinux.org/steam-stat-bin.git
cp aur/PKGBUILD aur/.SRCINFO steam-stat-bin/
cd steam-stat-bin && makepkg -si   # 本地验证
git commit -am "Update to <v>" && git push
```

`.SRCINFO` 已按模板渲染；修改 `PKGBUILD` 后用 `makepkg --printsrcinfo > .SRCINFO` 重新生成。

## Linux 运行说明

- 凭证：Steam 登录凭证经 libsecret 加密保存（GNOME Keyring、KWallet、KeePassXC 等 Secret Service 均可）。没有密钥环时登录仍可用，但「记住登录」只在本次运行有效。
- 桌面活动：挂机与前台检测使用 X11 / XWayland。纯 Wayland 窗口获得焦点时无法判断，此时不区分前后台、不判定挂机；手柄输入暂不参与挂机判定。
- Chromium 沙箱：没有 SUID 的 `chrome-sandbox` 且系统限制非特权用户命名空间（如 Ubuntu 23.10+）时自动以 `--no-sandbox` 启动；`STEAM_STAT_NO_SANDBOX=1` 可强制关闭。
- AppImage 需要 FUSE 2（`libfuse2`）；Steam Deck 桌面模式已自带。
