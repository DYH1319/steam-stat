#!/usr/bin/env node
/**
 * 用发布产物渲染各包管理器的清单模板（winget / Scoop / Flatpak / AUR）。
 *
 *   node packaging/render-manifests.mjs --version 4.1.0 --artifacts release --out release/packaging
 *
 * 读取 artifacts 目录下的发布文件计算 SHA-256，替换模板中的占位符：
 *   {{VERSION}} {{TAG}} {{PKGVER}} {{RELEASE_DATE}} {{DOWNLOAD_BASE}}
 *   {{SHA256_SETUP}} {{SHA256_PORTABLE}} {{SHA256_TARBALL}} {{SHA256_APPIMAGE}}
 * 缺少某个产物时跳过依赖它的清单（例如只构建了 Windows 时不生成 Flatpak / AUR）。
 * 只用 Node 内置模块，CI 与本地都可直接运行。
 */
import { createHash } from 'node:crypto'
import { existsSync, mkdirSync, readdirSync, readFileSync, writeFileSync } from 'node:fs'
import path from 'node:path'
import process from 'node:process'
import { fileURLToPath } from 'node:url'

const here = path.dirname(fileURLToPath(import.meta.url))

function option(name, fallback) {
  const index = process.argv.indexOf(`--${name}`)
  if (index >= 0 && process.argv[index + 1]) {
    return process.argv[index + 1]
  }
  if (fallback !== undefined) {
    return fallback
  }
  console.error(`Missing --${name}`)
  process.exit(2)
}

const version = option('version').replace(/^v/, '')
const artifacts = path.resolve(option('artifacts'))
const out = path.resolve(option('out'))
const releaseDate = option('release-date', new Date().toISOString().slice(0, 10))
const tag = `v${version}`

const files = {
  SHA256_SETUP: `Steam-Stat-Setup-${version}.exe`,
  SHA256_PORTABLE: `Steam-Stat-Portable-${version}-win-x64.zip`,
  SHA256_TARBALL: `Steam-Stat-${version}-linux-x64.tar.gz`,
  SHA256_APPIMAGE: `Steam-Stat-${version}-x86_64.AppImage`,
}

const values = {
  VERSION: version,
  TAG: tag,
  // pacman 的 pkgver 不允许连字符：4.1.0-beta.1 → 4.1.0beta.1（与 vercmp 的预发布排序一致）
  PKGVER: version.replace(/-/g, ''),
  RELEASE_DATE: releaseDate,
  DOWNLOAD_BASE: `https://github.com/DYH1319/steam-stat/releases/download/${tag}`,
}
for (const [key, file] of Object.entries(files)) {
  const full = path.join(artifacts, file)
  if (existsSync(full)) {
    values[key] = createHash('sha256').update(readFileSync(full)).digest('hex')
  }
}

/** 清单目录 → 输出位置（winget 按 winget-pkgs 仓库的目录结构输出） */
const targets = [
  { source: 'winget', dest: path.join('winget', 'manifests', 'd', 'DYH1319', 'SteamStat', version) },
  { source: 'scoop', dest: 'scoop' },
  { source: 'flatpak', dest: 'flatpak' },
  { source: 'aur', dest: 'aur' },
]

let rendered = 0
for (const target of targets) {
  const sourceDir = path.join(here, target.source)
  const templates = readdirSync(sourceDir, { withFileTypes: true }).filter(entry => entry.isFile())
  const contents = templates.map(entry => [entry.name, readFileSync(path.join(sourceDir, entry.name), 'utf8')])
  const missing = new Set(contents.flatMap(([, text]) => [...text.matchAll(/\{\{([A-Z0-9_]+)\}\}/g)].map(match => match[1]))
    .filter(key => values[key] === undefined))
  if (missing.size > 0) {
    console.warn(`Skipping ${target.source}: missing ${[...missing].join(', ')} (artifact not built)`)
    continue
  }
  const destDir = path.join(out, target.dest)
  mkdirSync(destDir, { recursive: true })
  for (const [name, text] of contents) {
    writeFileSync(path.join(destDir, name), text.replace(/\{\{([A-Z0-9_]+)\}\}/g, (_, key) => values[key]))
  }
  rendered++
  console.log(`Rendered ${target.source} → ${path.relative(process.cwd(), destDir)}`)
}

if (rendered === 0) {
  console.error('No manifests were rendered')
  process.exit(1)
}
