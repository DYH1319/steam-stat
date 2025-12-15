import type { SteamUser } from '../db/schema'
import fs from 'node:fs'
import path from 'node:path'
import * as steamUser from '../db/steamUser'
import { steamIdToAccountId } from '../util/utils'
import * as localFileService from './localFileService'

/**
 * 获取用户头像
 * 优先使用本地缓存文件，若不存在则从 localconfig.vdf 获取 URL
 */
async function getUserAvatar(steamPath: string, steamId: bigint): Promise<string | null> {
  // 1. 尝试从本地缓存获取头像文件
  const avatarCachePath = path.join(steamPath, 'config', 'avatarcache', `${steamId}.png`)
  if (fs.existsSync(avatarCachePath)) {
    return avatarCachePath
  }

  // 2. 尝试从 localconfig.vdf 获取头像 URL
  try {
    const accountId = steamIdToAccountId(steamId)
    const localconfig = await localFileService.readLocalconfigVdf(steamPath, accountId.toString())

    // 从 friends 中查找当前用户的头像
    if (localconfig.friends && localconfig.friends[accountId]) {
      const avatarHash = localconfig.friends[accountId].avatar
      if (avatarHash) {
        return `https://avatars.steamstatic.com/${avatarHash}_full.jpg`
      }
    }
  }
  catch (error) {
    console.error(`[getUserAvatar] 获取用户头像失败 (SteamID: ${steamId}):`, error)
  }

  return null
}

/**
 * 初始化或更新 Steam 用户信息到数据库
 */
export async function initOrUpdateSteamUser(steamPath: string) {
  const loginusersVdf = await localFileService.readLoginusersVdf(steamPath)

  // 为每个用户获取头像
  const avatarMap = new Map<bigint, string | null>()
  for (const [_steamId64, userInfo] of Object.entries(loginusersVdf)) {
    const avatar = await getUserAvatar(steamPath, userInfo.SteamID)
    avatarMap.set(userInfo.SteamID, avatar)
  }

  await steamUser.insertOrUpdateSteamUserBatch(loginusersVdf, avatarMap)
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
