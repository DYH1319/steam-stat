import fs from 'node:fs'
import path from 'node:path'
import { getSteamStatus } from './getSteamStatus'

/**
 * 获取游戏库路径列表
 * @returns 游戏库路径数组
 */
export async function getLibraryFolders(): Promise<string[]> {
  const SteamStatus = await getSteamStatus()
  const steamPath = SteamStatus.SteamPath
  if (!steamPath) {
    return []
  }

  const libraryFoldersPath = path.join(steamPath, 'steamapps', 'libraryfolders.vdf')
  if (!fs.existsSync(libraryFoldersPath)) {
    return [path.join(steamPath, 'steamapps')]
  }

  // 延迟加载 vdf 模块
  const vdf = await import('@node-steam/vdf')
  const content = fs.readFileSync(libraryFoldersPath, 'utf8')
  const data = vdf.parse(content)

  const folders: string[] = []
  const libraryfolders = data.libraryfolders || data.LibraryFolders || {}

  for (const key of Object.keys(libraryfolders)) {
    if (/^\d+$/.test(key)) {
      const folder = libraryfolders[key]
      const folderPath = folder.path || folder
      if (typeof folderPath === 'string') {
        folders.push(path.join(folderPath, 'steamapps'))
      }
    }
  }

  // 如果没有找到，至少返回默认路径
  if (folders.length === 0) {
    folders.push(path.join(steamPath, 'steamapps'))
  }

  return folders
}

(async () => console.warn('[Steam Test] 游戏库路径: ', await getLibraryFolders()))()
