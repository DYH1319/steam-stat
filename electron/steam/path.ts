import { exec } from 'node:child_process'
import fs from 'node:fs'
import path from 'node:path'
import { promisify } from 'node:util'

const execAsync = promisify(exec)

/**
 * 获取 Steam 安装路径
 * @returns Steam 安装路径，如果未找到则返回 null
 */
export async function getSteamPath(): Promise<string | null> {
  try {
    // 从注册表读取 Steam 路径
    const { stdout } = await execAsync('reg query "HKEY_CURRENT_USER\\Software\\Valve\\Steam" /v SteamPath', { encoding: 'utf8' })
    const match = stdout.match(/SteamPath\s+REG_SZ\s+(.+)/)
    if (match) {
      return match[1].trim().replace(/\//g, '\\')
    }
  }
  catch (error) {
    console.error('[Steam] 无法从注册表获取 Steam 路径:', error)
  }

  // 尝试默认路径
  const defaultPaths = [
    'C:\\Program Files (x86)\\Steam',
    'C:\\Program Files\\Steam',
  ]

  for (const p of defaultPaths) {
    if (fs.existsSync(p)) {
      return p
    }
  }

  return null
}

/**
 * 获取游戏库路径列表
 * @returns 游戏库路径数组
 */
export async function getLibraryFolders(): Promise<string[]> {
  try {
    const steamPath = await getSteamPath()
    if (!steamPath) {
      return []
    }

    const libraryFoldersPath = path.join(steamPath, 'steamapps', 'libraryfolders.vdf')
    if (!fs.existsSync(libraryFoldersPath)) {
      return [path.join(steamPath, 'steamapps')]
    }

    // 延迟加载 vdf 模块
    const vdf = await import('@node-steam/vdf')
    const { readFileSync } = await import('node:fs')
    const content = readFileSync(libraryFoldersPath, 'utf8')
    const data = vdf.parse(content)

    const folders: string[] = []
    const libraryfolders = data.libraryfolders || data.LibraryFolders || {}

    for (const key of Object.keys(libraryfolders)) {
      if (/^\d+$/.test(key)) {
        const folder = libraryfolders[key]
        const folderPath = folder.path || folder
        if (typeof folderPath === 'string') {
          folders.push(path.join(folderPath.replace(/\\\\/g, '\\'), 'steamapps'))
        }
      }
    }

    // 如果没有找到，至少返回默认路径
    if (folders.length === 0) {
      folders.push(path.join(steamPath, 'steamapps'))
    }

    console.warn('[Steam] 游戏库路径:', folders)
    return folders
  }
  catch (error) {
    console.error('[Steam] 获取游戏库失败:', error)
    const steamPath = await getSteamPath()
    return steamPath ? [path.join(steamPath, 'steamapps')] : []
  }
}
