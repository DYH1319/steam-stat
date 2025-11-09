import type { ProcessInfo } from '../process'
import type { SteamGame } from './games'
import { getProcessList } from '../process'
import { getInstalledGames } from './games'
import { getSteamUser } from './users'

export interface RunningGame extends SteamGame {
  process: ProcessInfo
  isRunning: boolean
  matchedExePath: string
}

export interface SteamStatus {
  user: { steamId: string, accountName: string } | null
  installedGames: SteamGame[]
  runningGames: RunningGame[]
  isSteamRunning: boolean
  steamProcess: ProcessInfo | null
}

/**
 * 获取 Steam 游戏监控信息
 * @returns Steam 状态信息
 */
export async function getSteamGamesStatus(): Promise<SteamStatus> {
  try {
    console.warn('[Steam] 开始获取 Steam 游戏状态...')

    // 获取用户信息
    const user = await getSteamUser()

    // 获取已安装游戏
    const installedGames = await getInstalledGames()

    // 获取当前进程
    const processes = await getProcessList()

    // 通过 exe 路径匹配进程和游戏
    const runningGames: RunningGame[] = []

    for (const game of installedGames) {
      if (!game.exePaths || game.exePaths.length === 0) {
        continue
      }

      // 标准化游戏的 exe 路径
      const gameExePaths = game.exePaths.map((p: string) => p.toLowerCase().replace(/\\/g, '/'))

      // 查找匹配的进程
      for (const proc of processes) {
        if (!proc.path) {
          continue
        }

        const procPath = proc.path.toLowerCase().replace(/\\/g, '/')

        // 检查进程路径是否匹配游戏的任何一个 exe 文件
        if (gameExePaths.some((gamePath: string) => {
          const normalizedGamePath = gamePath.toLowerCase().replace(/\\/g, '/')
          return procPath === normalizedGamePath || procPath.endsWith(normalizedGamePath)
        })) {
          runningGames.push({
            ...game,
            process: proc,
            isRunning: true,
            matchedExePath: proc.path,
          })
          break
        }
      }
    }

    // 检测 Steam 客户端是否运行
    const steamProcess = processes.find(p => p.name.toLowerCase() === 'steam.exe')
    const isSteamRunning = !!steamProcess

    console.warn('[Steam] 当前运行的游戏:', runningGames.map(g => g.name))

    return {
      user,
      installedGames,
      runningGames,
      isSteamRunning,
      steamProcess: steamProcess || null,
    }
  }
  catch (error) {
    console.error('[Steam] 获取游戏状态失败:', error)
    return {
      user: null,
      installedGames: [],
      runningGames: [],
      isSteamRunning: false,
      steamProcess: null,
    }
  }
}
