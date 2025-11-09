import fs from 'node:fs'
import path from 'node:path'
import { promisify } from 'node:util'
import { getLibraryFolders } from './path'

const readFileAsync = promisify(fs.readFile)

export interface SteamGame {
  appId: string
  name: string
  installDir: string
  gameFolder: string
  exePaths: string[]
  libraryFolder: string
}

/**
 * 递归查找目录下的所有 .exe 文件
 * @param dir 目录路径
 * @param depth 当前递归深度
 * @param maxDepth 最大递归深度
 * @returns exe 文件路径数组
 */
function findExeFiles(dir: string, depth = 0, maxDepth = 2): string[] {
  if (depth > maxDepth) {
    return []
  }

  const files: string[] = []
  try {
    const items = fs.readdirSync(dir)
    for (const item of items) {
      const fullPath = path.join(dir, item)
      try {
        const stat = fs.statSync(fullPath)
        if (stat.isFile() && item.toLowerCase().endsWith('.exe')) {
          files.push(fullPath)
        }
        else if (stat.isDirectory() && depth < maxDepth) {
          files.push(...findExeFiles(fullPath, depth + 1, maxDepth))
        }
      }
      catch {
        // 忽略权限错误
      }
    }
  }
  catch (error) {
    console.error(`[Steam] 读取目录失败 ${dir}:`, error)
  }

  return files
}

/**
 * 获取已安装的游戏列表
 * @returns Steam 游戏列表
 */
export async function getInstalledGames(): Promise<SteamGame[]> {
  try {
    const libraryFolders = await getLibraryFolders()
    const games: SteamGame[] = []

    for (const folder of libraryFolders) {
      if (!fs.existsSync(folder)) {
        continue
      }

      const files = fs.readdirSync(folder)
      for (const file of files) {
        if (file.startsWith('appmanifest_') && file.endsWith('.acf')) {
          try {
            const manifestPath = path.join(folder, file)
            const content = await readFileAsync(manifestPath, 'utf8')

            // 延迟加载 vdf 模块
            const vdf = await import('@node-steam/vdf')
            const data = vdf.parse(content)
            const appState = data.AppState || {}

            const appId = appState.appid
            const name = appState.name || 'Unknown'
            const installDir = appState.installdir || ''

            // 构建游戏可执行文件的完整路径
            const gameFolder = path.join(folder, 'common', installDir)
            let exePaths: string[] = []

            // 尝试查找游戏目录下的 .exe 文件
            if (fs.existsSync(gameFolder)) {
              try {
                exePaths = findExeFiles(gameFolder)
              }
              catch (e) {
                console.error(`[Steam] 扫描游戏目录失败 ${gameFolder}:`, e)
              }
            }

            games.push({
              appId,
              name,
              installDir,
              gameFolder,
              exePaths,
              libraryFolder: folder,
            })
          }
          catch (error) {
            console.error(`[Steam] 解析 ${file} 失败:`, error)
          }
        }
      }
    }

    console.warn('[Steam] 已安装游戏数量:', games.length)
    return games
  }
  catch (error) {
    console.error('[Steam] 获取游戏列表失败:', error)
    return []
  }
}
