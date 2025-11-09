/**
 * Steam 功能模块
 * 提供 Steam 平台相关的所有功能
 */

// appinfo 相关
export { EUniverse, getAppInfoById, getInstalledAppsInfo, parseAppInfo } from './appinfo'
export type { AppInfo } from './appinfo'
export { debugAppInfoHeader, runCSharpParserAndCompare, testParseAppInfo } from './appinfo.test'

// 认证相关
export {
  getSteamLoginStatus,
  getSteamStoreAccessToken,
  loginSteam,
  logoutSteam,
  steamAuthManager,
} from './auth'
export type { SteamLoginOptions, SteamStoreAccessToken, SteamWebSession } from './auth'

// 游戏相关
export { getInstalledGames } from './games'

export type { SteamGame } from './games'
// 监控相关
export { getSteamGamesStatus } from './monitor'

export type { RunningGame, SteamStatus } from './monitor'
// 路径相关
export { getLibraryFolders, getSteamPath } from './path'

// 用户相关
export { getSteamUser, getSteamUsers } from './users'
export type { SteamUser } from './users'
