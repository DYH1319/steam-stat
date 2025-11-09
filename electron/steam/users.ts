import fs, { readFileSync } from 'node:fs'
import path from 'node:path'
import { getSteamPath } from './path'

export interface SteamUser {
  steamId: string
  accountName: string
  personaName: string
  timestamp: number
  mostRecent: boolean
  rememberPassword: boolean
}

/**
 * 获取所有 Steam 用户
 * @returns Steam 用户列表
 */
export async function getSteamUsers(): Promise<SteamUser[]> {
  try {
    const steamPath = await getSteamPath()
    if (!steamPath) {
      return []
    }

    console.warn('[Steam] Steam 安装路径:', steamPath)

    const loginUsersPath = path.join(steamPath, 'config', 'loginusers.vdf')
    if (!fs.existsSync(loginUsersPath)) {
      return []
    }

    // 延迟加载 vdf 模块
    const vdf = await import('@node-steam/vdf')
    const content = readFileSync(loginUsersPath, 'utf8')
    const data = vdf.parse(content)

    // 获取所有用户
    const users = data.users || {}
    const userList: SteamUser[] = []

    for (const [steamId, userInfo] of Object.entries(users)) {
      const timestamp = Number.parseInt((userInfo as any).Timestamp || '0', 10)
      const mostRecent = (userInfo as any).mostrecent === '1'
      const rememberPassword = (userInfo as any).RememberPassword === '1'

      userList.push({
        steamId,
        accountName: (userInfo as any).AccountName || 'Unknown',
        personaName: (userInfo as any).PersonaName || 'Unknown',
        timestamp,
        mostRecent,
        rememberPassword,
      })
    }

    // 按时间戳排序，最近的在前
    userList.sort((a, b) => b.timestamp - a.timestamp)

    return userList
  }
  catch (error) {
    console.error('[Steam] 获取用户信息失败:', error)
    return []
  }
}

/**
 * 获取当前登录的 Steam 用户
 * @returns 当前用户信息，如果未找到则返回 null
 */
export async function getSteamUser(): Promise<{ steamId: string, accountName: string } | null> {
  const users = await getSteamUsers()
  const currentUser = users.find(u => u.mostRecent) || users[0]
  return currentUser
    ? {
        steamId: currentUser.steamId,
        accountName: currentUser.accountName,
      }
    : null
}
