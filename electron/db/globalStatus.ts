import type { SteamActiveProcessReg, SteamReg } from '../types/localReg'
import type { GlobalStatus, NewGlobalStatus } from './schema'
import { sql } from 'drizzle-orm'
import { accountIdToSteamId } from '../util/utils'
import { getDatabase } from './connection'
import { globalStatus } from './schema'

/**
 * 插入或更新全局状态信息到数据库
 */
export async function insertOrUpdateGlobalStatus(steamReg: SteamReg, steamActiveProcessReg: SteamActiveProcessReg) {
  const db = getDatabase()
  const newGlobalStatus: NewGlobalStatus = {
    id: 1,
    steamPath: steamReg.SteamPath,
    steamExePath: steamReg.SteamExe,
    steamPid: steamActiveProcessReg.pid,
    steamClientDllPath: steamActiveProcessReg.SteamClientDll,
    steamClientDll64Path: steamActiveProcessReg.SteamClientDll64,
    activeUserSteamId: accountIdToSteamId(steamActiveProcessReg.ActiveUser),
    runningAppId: steamReg.RunningAppID,
    refreshTime: new Date(),
    steamUserRefreshTime: new Date(),
  }

  try {
    await db.insert(globalStatus)
      .values(newGlobalStatus)
      .onConflictDoUpdate({
        target: globalStatus.id,
        set: {
          steamPath: sql`excluded.steam_path`,
          steamExePath: sql`excluded.steam_exe_path`,
          steamPid: sql`excluded.steam_pid`,
          steamClientDllPath: sql`excluded.steam_client_dll_path`,
          steamClientDll64Path: sql`excluded.steam_client_dll_64_path`,
          activeUserSteamId: sql`excluded.active_user_steam_id`,
          runningAppId: sql`excluded.running_app_id`,
          refreshTime: sql`excluded.refresh_time`,
          steamUserRefreshTime: sql`excluded.steam_user_refresh_time`,
        },
      })
    console.warn(`[DB] 成功更新全局状态`)
  }
  catch (error) {
    console.error(`[DB] 更新全局状态失败:`, error)
  }
}

/**
 * 获取全局状态
 */
export async function getGlobalStatus(): Promise<GlobalStatus> {
  const db = getDatabase()
  const result = await db.select().from(globalStatus).limit(1)
  return result[0]
}

/**
 * 更新 Steam 用户刷新时间
 */
export async function updateSteamUserRefreshTime() {
  const db = getDatabase()
  try {
    await db.update(globalStatus)
      .set({
        steamUserRefreshTime: new Date(),
      })
      .where(sql`${globalStatus.id} = 1`)
    console.warn(`[DB] 成功更新 Steam 用户刷新时间`)
  }
  catch (error) {
    console.error(`[DB] 更新 Steam 用户刷新时间失败:`, error)
  }
}
