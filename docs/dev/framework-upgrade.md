# 框架底座（Fantastic-admin basic）升级约定

Steam Stat 的前端底座为 **Fantastic-admin basic**，应用层基于 `apps/core-ant-design-vue` 模板（ant-design-vue，另为 el-table-v2/el-skeleton 引入 Element Plus），以 git 远程
`upstream`（`git@github.com:fantastic-admin/basic.git`）的方式引入，而不是复制粘贴代码。迁移时通过一次
`--allow-unrelated-histories` 合并把 upstream 历史接入本仓库，之后的每次升级都有正确的 merge-base，
`git merge upstream/main` 只会带入 upstream 的**新增变更**。

- 迁移基线：upstream `main` @ `1b5961ef`（basic 6.4.0 之后的“chore: 更新依赖”），取代原先 vendor 的 basic v5.8.0
- 迁移分支：`chore/upgrade-fa-basic-6.4`（合并提交的第二个父提交即 upstream 基线）

upstream 的 tag 与本仓库发布 tag（`v*.*.*` 触发 release 工作流）可能重名，因此 tag 已做命名空间隔离：

```bash
git remote add upstream git@github.com:fantastic-admin/basic.git
git config remote.upstream.tagOpt --no-tags
git config --add remote.upstream.fetch '+refs/tags/*:refs/upstream-tags/*'
# upstream 的 v6.4.0 等 tag 落在 refs/upstream-tags/v6.4.0，不会进入 refs/tags
```

## 1. 目录归属

| 归属 | 路径 | 规则 |
|---|---|---|
| upstream（框架） | `packages/**`、`apps/core*`、`apps/example`、`skills/`、`.agents/`、`.claude/`、`CLAUDE.md`、`scripts/cli.ts`、根工程配置（`package.json`、`pnpm-workspace.yaml`、`eslint.config.js`、`stylelint.config.js`、`tsconfig.json`、`uno.config.ts`、`.lintstagedrc`、`.commitlintrc.js`、`.node-version`、`.editorconfig`、`.vscode/`、`fantastic-admin.code-workspace`、`AGENTS.md` 前半部分） | **不修改**；确需修改时登记到第 3 节 |
| 本应用 | `apps/steam-stat/**`、`backend/`、`ElectronNet/`、`tools/`、`third_party/`、`docs/`、`resources/`、`.github/`、`scripts/`（除 `cli.ts`）、根 `README*.md`、`LICENSE`、`CONTRIBUTING.md`、`COPYRIGHT`、`SteamStat.slnx`、`Directory.Build.props`、`AGENTS.md` 末尾的“Steam Stat”章节 | 业务代码只进 `apps/steam-stat` |

`apps/steam-stat/src` 中的“框架层”（`layouts/`、`store/modules/app/`、`composables/app/`、`router/guards.ts` 等）来自
`apps/core-ant-design-vue` 模板，属于本应用的代码；upstream 不会自动修改它们，升级时按第 2 节第 5 步同步，
并保留第 4 节列出的定制。

## 2. 升级框架 = fetch upstream + merge 框架目录

```bash
# 1. 获取 upstream 最新代码（只使用该远程地址）
git remote get-url upstream   # 应为 git@github.com:fantastic-admin/basic.git
git fetch upstream            # tag 走 refs/upstream-tags/*

# 2. 在独立分支上合并（可指定 tag，如 upstream 的 v6.5.0 → refs/upstream-tags/v6.5.0）
git checkout -b chore/upgrade-fa-basic-<版本>
git merge --no-commit upstream/main

# 3. 处理冲突（规则见下表），然后重新安装依赖并生成类型
pnpm install

# 4. 验证（Node 24 + pnpm 12.6.0）
pnpm --filter @fantastic-admin/steam-stat lint
pnpm --filter @fantastic-admin/steam-stat test:unit
pnpm --filter @fantastic-admin/steam-stat build
pnpm exec eslint .
pnpm exec stylelint "{apps,packages}/**/*.{css,scss,vue}"
pnpm lint   # 全量
dotnet run --project tools/GenerateIpcContracts -- --check

# 5. 同步应用内的框架层：把 upstream 模板（core-ant-design-vue）在两个版本间的差异应用到本应用
OLD=<上次升级的 upstream 提交，如 1b5961ef>
NEW=upstream/main
git diff $OLD $NEW -- apps/core-ant-design-vue/src/layouts apps/core-ant-design-vue/src/store/modules/app \
  apps/core-ant-design-vue/src/composables/app apps/core-ant-design-vue/src/router/guards.ts \
  apps/core-ant-design-vue/src/locales apps/core-ant-design-vue/vite \
  | sed 's#apps/core-ant-design-vue/#apps/steam-stat/#g' | git apply -3
# 逐个检查 .rej / 冲突，保留第 4 节列出的定制

git commit   # 提交信息注明 upstream 版本
```

冲突处理规则：

| 文件 | 处理方式 |
|---|---|
| `packages/**`、`apps/core*`、`apps/example`、`skills/**`、`.agents/**`、`.claude/**` | 一律取 upstream：`git checkout --theirs -- <路径>` |
| `pnpm-lock.yaml` | 取 upstream 后执行 `pnpm install` 重新生成（会加入 `apps/steam-stat` 的 importer） |
| `eslint.config.js` | 取 upstream 后重新应用第 3 节补丁 #1 |
| `package.json` | 取 upstream 后重新应用第 3 节补丁 #2 |
| `AGENTS.md` | 取 upstream 的前半部分，保留末尾“Steam Stat”章节 |
| `.github/workflows/deploy-example.yml`、`repo-sync.yml`、`release.yml` | 保持删除：`git rm <文件>` |
| `README.md`、`LICENSE`、`.github/workflows/build-and-release.yml`、`ci.yml` | 取本仓库版本：`git checkout --ours -- <路径>` |

## 3. 框架文件补丁清单（被迫修改的 upstream 文件）

| # | 路径 | 原因 | 补丁说明 |
|---|---|---|---|
| 1 | `eslint.config.js` | `third_party`（submodule 源码）、.NET 构建产物（`bin`/`obj`/`Publish`）、生成的 `preload.mjs` 被 ESLint 扫描会报错或极慢；应用专属依赖需精确锁定版本，不进入框架 catalog | `ignores` 增加 `'**/preload.mjs'`、`'third_party'`、`'**/bin/**'`、`'**/obj/**'`、`'ElectronNet/**/Publish/**'`；新增一段只作用于 `apps/steam-stat/package.json` 的配置，关闭 `pnpm/json-enforce-catalog`（否则 `--fix` 会把精确锁定的依赖改写为 `catalog:` 并向 `pnpm-workspace.yaml` 注入条目） |
| 2 | `package.json` | 保留 `pnpm run build:win`（Windows 打包入口）与 `pnpm run test:unit`（前端测试入口）两个根脚本，供文档与本地流程使用 | `scripts` 末尾追加 `build:win`、`test:unit` 两项，其余内容取 upstream |
| 3 | `.gitignore` | 本仓库有 .NET/Electron 产物与本地覆盖文件 | 末尾追加 `release`、`releases`、`dist-electron`、`*.db`、`.ef/`、`.idea/`、`bin/`、`obj/`、`.vs/`、`*.user`、`Publish/`、`Directory.Build.local.props`、`testColor.ts` |
| 4 | `AGENTS.md` | 需要记录本应用的开发约定 | 仅在文件**末尾**追加“Steam Stat”章节，upstream 修改前半部分时可自动合并 |
| 5 | `.github/workflows/deploy-example.yml`、`repo-sync.yml`、`release.yml` | 依赖 upstream 的部署密钥与外部仓库，在本仓库无法运行；upstream `release.yml` 还会在 `v*` tag 上触发 changelog 上传，与本仓库发布流程冲突 | 删除；本仓库发布工作流为 `build-and-release.yml` |
| 6 | `apps/core-tdesign/src/ui/provider/index.vue` | upstream 的 `merge({} as GlobalConfigProvider, zhCN)` 在 tdesign-vue-next 类型下报 `TS2322`（locale 中的 readonly 数组不能赋给 `DatePickerConfig`），`pnpm lint` 的 vue-tsc 失败 | `zhCN` 断言为 `unknown as GlobalConfigProvider` 后再 merge（运行时行为不变）。upstream 修正后删除本补丁 |

未修改：`pnpm-workspace.yaml`（应用专属依赖在 `apps/steam-stat/package.json` 精确锁定）、`packages/themes`（主题色在应用内 `src/themes/colors.ts`）、`uno.config.ts`、`stylelint.config.js`、`tsconfig.json`。

## 4. 应用内框架层的定制（同步 upstream 模板时需保留）

| 位置 | 与 `apps/core-ant-design-vue` 的差异 |
|---|---|
| `layouts/components/Logo/index.vue` | logo 使用 `@/assets/images/logo.png`；RouterLink `<a>` 加 `:draggable="false"`（Chromium 默认拖拽链接） |
| `layouts/components/Menu/item.vue` | 菜单项链接 `target` 恒为 `_self`（外链走 `views/link.vue` 经 `shellOpenExternal`）；菜单项元素加 `:draggable="false"`（`<a>` 不受全局 `img` 规则覆盖）；叶子菜单项读取 `meta.dataSources`，经 `dataSource` store 判定缺失/解析失败时置灰（`opacity-50 grayscale`）并显示缺失数据源 Tooltip（仍可点击，由路由守卫重定向） |
| `layouts/components/Topbar/Toolbar/endSide.vue` | 工具栏右侧前置 Steam 连接状态 `Tag`+`Tooltip`（每 15s 轮询 `window.electron.steamOperationalStatusGet`，i18n key `connectivity.*`）与“重新认证”链接按钮（跳转 `/data-source/cm`） |
| `layouts/components/views/link.vue` | i18n 文案；`shellOpenExternal` 打开外链；额外“复制链接”按钮；`mb-0` |
| `layouts/index.vue` | `sidebar-container` 与 `fixed-content-around-area` 的 `top` 增加 `var(--g-title-bar-height, 40px)` 偏移（自定义标题栏）；`.copyright` 的 `box-shadow` 移除 `0 -1px` 段（去掉版权栏上方的分割线） |
| `App.vue` | 增加 `TitleBar`、`.app-content` 滚动容器、Ctrl+滚轮/键盘缩放、updater store 初始化、`settingGet` → `homePage` 路由（homePage 缺失或指向未注册的实验性路由时回退 `/data-source/local`）、`useDataSourceStore().startPolling()` 启动数据源健康轮询（供路由守卫与菜单置灰使用）、禁用中键与生产环境刷新、`CloseConfirmDialog`；`FaToast` 传 `:duration="1500" :visible-toasts="1"`；不调用 `setMode`/监听 resize，`mode` 恒为 `pc`（窗口缩放会跌破 1024px 阈值，桌面端不做移动端适配） |
| `main.ts` | 异步 bootstrap：`app.use(router)` 前先 `settingGet` 写入 i18n locale 与实验性开关；`v-ripple` 指令（`src/utils/ripple.js`）；离线 iconify |
| `store/modules/app/account.ts` | 始终视为已登录的本机用户，默认账号 `Steam 本地数据`、默认头像 `i-mdi-local.svg` |
| `store/modules/appearance.ts` | 应用新增：主题色/语言/圆角/颜色方案状态与 `settingGet()` 初始化，colorScheme/radius 转发给 `useAppSettingsStore` |
| `components/AppAccountButton/index.vue` | 下拉菜单项全部移除（桌面单用户），头部使用 i18n `accountButton.*`、`select-none` |
| `components/AppCopyright/index.vue` | 链接通过 `window.electron.shellOpenExternal` 打开；页脚底色 `bg-[#f2f2f2]`/暗色 `dark:bg-[#09090b]` 且用 `py-4` 填满整条色带 |
| `ui/provider/index.ts` / `index.vue` | 同时安装 Ant Design Vue 与 Element Plus（含 dark css-vars）；antd token 完整映射 + locale 随 `appearanceStore.locale` 切换（zh-CN/en-US）；element-plus `--el-*` 变量桥接（oklch） |
| `router/routes.ts` + `router/modules/*` | 顶级路由：新增 `dataSource.ts`（`/data-source/{local,web-api,cm}` 菜单组）；`steam.ts` 改为按 `setting.ts` 模式的根级单页模块（`/user`、`/app`、`/useRecord`、`/friends`、`/library`、`/achievements`，后三个 `meta.experimental`），无 `/status` 与 `/steamLogin`（登录改为 CM 页内弹窗）；`guards.ts` 以 `filterExperimentalRoutes(asyncRoutes)` 过滤实验性路由并新增 `setupDataSources`（`meta.dataSources` 缺失时重定向数据源页）；`meta.experimental` 与 `meta.dataSources` 增强均在 `src/types/global.d.ts` |
| `settings.ts` | `app.home.enable=false`、`menu.mode='single'` + smart click + 折叠按钮、`topbar.toolbar=false`（隐藏整条工具栏，与 v5 `toolbar.enable:false` 行为一致；连接状态 Tag 的代码保留在 `endSide.vue` 以备启用）、`app.copyright` 启用 |
| `src/themes/colors.ts` | 9 个主题色（OKLCH 'L C H' 三元组，`--primary`/`--ring`/菜单 active 变量覆盖）；`THEME_COLOR_CSS_VARS` 清单 |
| `src/assets/styles/globals.css` | `--g-title-bar-height`、滚动条变量、`html` overflow hidden + 圆角、`img` 禁拖拽、antd modal z-index |
| `vite.config.ts` | `base: './'`（file:// 加载）、端口 9000、无 `server.open`、`__SYSTEM_INFO__.pkg.version` |
| `vite/plugins.ts` | 追加 `vite-plugin-disable-devtool`（`VITE_APP_DISABLE_DEVTOOL`） |
| `index.html` | CSP meta、`favicon.png`、`body.select-none`；`loading.html` z-index 5000 |
| `views/index.vue` | 空页面（home 已禁用，启动后由 `settingGet` → `homePage` 接管路由），勿回填模板演示页 |
| `components/DropdownMenu/` | 自 v5 vendor 层迁移的 dropdown-menu 原子组件（`views/steam/user.vue` 右键菜单使用，`@lucide/vue` 图标） |

## 5. 合并演练记录

每次升级后在此追加一行。

| 日期 | upstream 版本 | 结果 |
|---|---|---|
| 2026-10-12 | `1b5961ef`（迁移基线，basic 6.4.0） | 迁移完成；`git merge --allow-unrelated-histories --no-commit upstream/main` 后按第 2 节规则解决冲突（合并提交 `12c5b25d`，第二父提交即基线） |
| 2026-10-12 | `1b5961ef` | `git merge-base HEAD upstream/main` = `1b5961ef`；`git merge --no-commit upstream/main` → “Already up to date”（merge-base 正确） |

例外文件清单（可能需要人工处理的冲突）：`pnpm-lock.yaml`（upstream 改依赖时几乎必然冲突，按第 2 节规则取 upstream 后重新 `pnpm install`）；`eslint.config.js` 的 `ignores` 数组附近改动；`package.json` 的 `scripts` 段；被删除的 `.github/workflows/*.yml`（modify/delete）。
