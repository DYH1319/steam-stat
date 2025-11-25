import type { SteamUser } from '../db/schema'
import * as steamUser from '../db/steamUser'
import * as localFileService from './localFileService'

/**
 * 初始化或更新 Steam 用户信息到数据库
 */
export async function initOrUpdateSteamUser(steamPath: string) {
  const loginusersVdf = await localFileService.readLoginusersVdf(steamPath)
  await steamUser.insertOrUpdateSteamUserBatch(loginusersVdf)
}

/**
 * 获取 Steam 用户信息
 */
export async function getSteamLoginUserInfo(): Promise<SteamUser[]> {
  return steamUser.getAllUsers()
}

/**
 * 刷新 Steam 用户信息（重新读取文件并写入数据库，返回最新数据）
 */
export async function refreshSteamLoginUserInfo(steamPath: string): Promise<SteamUser[]> {
  await initOrUpdateSteamUser(steamPath)
  return steamUser.getAllUsers()
}
