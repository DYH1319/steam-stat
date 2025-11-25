# 发布指南

## 如何发布新版本

### 1. 使用 Git Tag 标识版本

当你准备发布新版本时，按照以下步骤操作：

```bash
# 1. 确保所有更改都已提交
git add .
git commit -m "feat: 准备发布 v5.9.0"

# 2. 创建并推送 tag（版本号格式：v主版本号.次版本号.修订号）
git tag v5.9.0
git push origin v5.9.0

# 或者一步完成：创建带注释的 tag
git tag -a v5.9.0 -m "Release version 5.9.0"
git push origin v5.9.0
```

### 2. 自动化构建和发布

- 当你推送 tag 到 GitHub 后，GitHub Actions 会自动触发构建流程
- 构建完成后会自动创建 Release 并上传安装包
- 用户可以在 GitHub Releases 页面下载安装包

### 3. Tag 命名规范

- **格式**：`v主版本号.次版本号.修订号`
- **示例**：`v1.0.0`, `v1.2.3`, `v2.0.0`
- **说明**：
  - 主版本号：重大功能变更或不兼容的 API 修改
  - 次版本号：向下兼容的功能性新增
  - 修订号：向下兼容的问题修正

### 4. 查看和管理 Tags

```bash
# 查看所有 tags
git tag

# 查看特定 tag 的信息
git show v5.9.0

# 删除本地 tag
git tag -d v5.9.0

# 删除远程 tag
git push origin :refs/tags/v5.9.0
```

### 5. 版本号更新

在创建 tag 之前，建议先更新 `package.json` 中的版本号：

```bash
# 使用 npm version 命令自动更新版本号并创建 tag
npm version patch  # 修订号 +1，如 1.0.0 -> 1.0.1
npm version minor  # 次版本号 +1，如 1.0.0 -> 1.1.0
npm version major  # 主版本号 +1，如 1.0.0 -> 2.0.0

# 推送更改和 tag
git push origin main --tags
```

### 6. GitHub Actions 工作流说明

工作流文件位置：`.github/workflows/build-and-release.yml`

**触发条件**：
- 推送格式为 `v*.*.*` 的 tag

**构建步骤**：
1. 检出代码
2. 设置 Node.js 和 pnpm 环境
3. 安装依赖
4. 重新构建原生模块
5. 构建 Electron 应用
6. 创建 GitHub Release
7. 上传安装包到 Release

**构建产物**：
- `Steam Stat Setup.<版本号>.exe` - Windows 安装程序

### 7. 注意事项

- 确保 `package.json` 中的版本号与 tag 版本号一致
- Tag 一旦推送到远程仓库，不建议删除或修改
- 如果构建失败，检查 GitHub Actions 日志排查问题
- 首次使用需要在 GitHub 仓库设置中确保 Actions 已启用
