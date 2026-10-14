import { execSync } from 'node:child_process'
import fs from 'node:fs'
import path from 'node:path'
import process from 'node:process'
import { fileURLToPath } from 'node:url'

/**
 * 按版本构建并归档
 * 构建产物会保存到 releases/v{version} 目录
 */

const __dirname = path.dirname(fileURLToPath(import.meta.url))
const rootDir = path.join(__dirname, '..')

// 读取版本号
const pkg = JSON.parse(fs.readFileSync(path.join(rootDir, 'apps', 'steam-stat', 'package.json'), 'utf-8'))
const version = pkg.version

// 创建版本化的输出目录
const versionedDir = path.join(rootDir, 'releases', `v${version}`)

console.log(`📦 构建版本: ${version}`)
console.log(`📁 输出目录: ${versionedDir}\n`)

// 如果目录已存在，询问是否覆盖
if (fs.existsSync(versionedDir)) {
  console.log(`⚠️  目录已存在: ${versionedDir}`)
  console.log('正在清理旧版本...\n')
  fs.rmSync(versionedDir, { recursive: true, force: true })
}

// 创建输出目录
fs.mkdirSync(versionedDir, { recursive: true })

try {
  // 执行构建（使用临时的 release 目录）
  console.log('🔨 开始构建...\n')
  execSync('pnpm --filter @fantastic-admin/steam-stat build && electron-builder --win --config -c.compression=store', {
    cwd: rootDir,
    stdio: 'inherit',
  })

  // 移动构建产物到版本目录
  console.log('\n📦 移动构建产物...')
  const releaseDir = path.join(rootDir, 'release')

  if (fs.existsSync(releaseDir)) {
    const items = fs.readdirSync(releaseDir)
    for (const item of items) {
      const src = path.join(releaseDir, item)
      const dest = path.join(versionedDir, item)
      fs.renameSync(src, dest)
      console.log(`  ✓ ${item}`)
    }

    // 删除临时 release 目录
    fs.rmSync(releaseDir, { recursive: true, force: true })
  }

  console.log(`\n✅ 构建完成！`)
  console.log(`📁 输出位置: ${versionedDir}`)
}
catch (error) {
  console.error('\n❌ 构建失败:', error.message)
  process.exit(1)
}
