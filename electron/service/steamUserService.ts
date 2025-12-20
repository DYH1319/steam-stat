import type { BrowserWindow } from 'electron'
import type { SteamUser } from '../db/schema'
import { Buffer } from 'node:buffer'
import axios from 'axios'
import * as steamUser from '../db/steamUser'
import { steamIdToAccountId } from '../util/utils'
import * as globalStatusService from './globalStatusService'
import * as localFileService from './localFileService'

/**
 * 从 URL 下载图片并转换为 Base64
 */
async function downloadImageAsBase64(url: string): Promise<string | null> {
  try {
    const response = await axios.get(url, {
      responseType: 'arraybuffer',
      timeout: 10000,
    })
    // 将 ArrayBuffer 转换为 Base64
    const buffer = Buffer.from(response.data)
    return buffer.toString('base64')
  }
  catch {
    return null
  }
}

/**
 * 异步从 Steam API 获取用户头像和等级信息
 */
async function fetchUserAvatarFromAPI(accountId: number) {
  try {
    const url = `https://steam-chat.com/miniprofile/${accountId}/json`
    const response = await axios.get(url, { timeout: 10000 })
    const obj = response.data

    if (!obj) {
      return
    }

    // 下载头像图片并转换为 Base64
    const avatarFull = obj.avatar_url ? await downloadImageAsBase64(obj.avatar_url) : null
    const avatarMedium = obj.avatar_url ? await downloadImageAsBase64(obj.avatar_url.replace('_full', '_medium')) : null
    const avatarSmall = obj.avatar_url ? await downloadImageAsBase64(obj.avatar_url.replace('_full', '')) : null
    const animatedAvatar = obj.animated_avatar ? await downloadImageAsBase64(obj.animated_avatar) : null
    const avatarFrame = obj.avatar_frame ? await downloadImageAsBase64(obj.avatar_frame) : null

    // 修改本地文件中的 PersonaName
    // await localFileService.updatePersonaName(accountId, obj.persona_name || null)

    // 更新数据库
    await steamUser.updateUserAvatarAndLevel(
      accountId,
      obj.persona_name || null,
      avatarFull,
      avatarMedium,
      avatarSmall,
      animatedAvatar,
      avatarFrame,
      obj.level ?? null,
      obj.level_class || null,
    )
  }
  catch {
  }
}

/**
 * 初始化或更新 Steam 用户信息到数据库
 */
export async function initOrUpdateSteamUser(steamPath: string, win?: BrowserWindow) {
  // 读取 loginusers.vdf 文件
  const loginusersVdf = await localFileService.readLoginusersVdf(steamPath)

  // 插入或更新用户基本信息到数据库
  await steamUser.insertOrUpdateSteamUserBatch(loginusersVdf)

  // 异步获取头像信息（不等待完成）
  // 使用 Promise.resolve().then() 确保异步执行，不阻塞主流程
  Promise.resolve().then(async () => {
    try {
      await Promise.all(
        Object.values(loginusersVdf).map(userInfo =>
          fetchUserAvatarFromAPI(steamIdToAccountId(userInfo.SteamID)),
        ),
      )
    }
    finally {
      // 无论成功失败，更新刷新时间
      await globalStatusService.updateSteamUserRefreshTime()

      // 通知前端刷新
      if (win && !win.isDestroyed()) {
        win.webContents.send('steam-user-updated')
      }
    }
  })
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
export async function refreshSteamLoginUserInfo(steamPath: string, win?: BrowserWindow): Promise<SteamUser[]> {
  await initOrUpdateSteamUser(steamPath, win)
  return steamUser.getAllUsers()
}
