import SteamUser from 'steam-user'

let steamAnonymousUser: SteamUser | null = null

/**
 * 初始化匿名 SteamUser
 */
export function initSteamAnonymousUser() {
  if (steamAnonymousUser) {
    console.warn('[SteamUser] 匿名 SteamUser 已存在')
    return steamAnonymousUser
  }

  steamAnonymousUser = new SteamUser({})
  steamAnonymousUser.logOn({ anonymous: true })
  return steamAnonymousUser
}

/**
 * 获取匿名 SteamUser
 */
export function getSteamAnonymousUser() {
  if (!steamAnonymousUser) {
    return initSteamAnonymousUser()
  }
  return steamAnonymousUser
}

/**
 * 退出匿名 SteamUser
 */
export function logOffSteamAnonymousUser() {
  if (steamAnonymousUser) {
    steamAnonymousUser.logOff()
    steamAnonymousUser = null
    console.warn('[SteamUser] 匿名 SteamUser 已退出')
  }
}

/**
 * 检查匿名 SteamUser 状态
 */
export function isSteamAnonymousUserConnected(): boolean {
  return steamAnonymousUser !== null && steamAnonymousUser.steamID !== null
}
