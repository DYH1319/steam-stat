import type { LoginusersVdf } from '../types/localFile'
import type { NewSteamUser, SteamUser } from './schema'
import { eq, inArray, sql } from 'drizzle-orm'
import { steamIdToAccountId } from '../util/utils'
import { getDatabase } from './connection'
import { steamUser } from './schema'

/**
 * 批量插入或更新 Steam 用户信息到数据库
 */
export async function insertOrUpdateSteamUserBatch(loginusersVdf: Record<string, LoginusersVdf>) {
  const db = getDatabase()
  const users: NewSteamUser[] = []

  if (loginusersVdf) {
    for (const [_steamId64, userInfo] of Object.entries(loginusersVdf)) {
      users.push({
        steamId: userInfo.SteamID,
        accountId: steamIdToAccountId(userInfo.SteamID),
        accountName: userInfo.AccountName,
        personaName: userInfo.PersonaName,
        rememberPassword: userInfo.RememberPassword === 1,
        wantsOfflineMode: userInfo.WantsOfflineMode === 1,
        skipOfflineModeWarning: userInfo.SkipOfflineModeWarning === 1,
        allowAutoLogin: userInfo.AllowAutoLogin === 1,
        mostRecent: userInfo.MostRecent === 1,
        timestamp: new Date(userInfo.Timestamp * 1000),
      })
    }
  }

  if (users.length === 0) {
    console.warn('[DB] 没有找到用户数据')
    return
  }

  // 查询数据库中已存在的 steamId
  const steamIds = users.map(user => user.steamId)
  const existingUsers = await db.select({ steamId: steamUser.steamId })
    .from(steamUser)
    .where(inArray(steamUser.steamId, steamIds))

  const existingSteamIds = new Set(existingUsers.map(user => user.steamId))

  // 分离新增/更新/删除的用户
  const usersToInsert = users.filter(user => !existingSteamIds.has(user.steamId))
  const usersToUpdate = users.filter(user => existingSteamIds.has(user.steamId))
  const usersToDelete = existingUsers.filter(user => !users.some(u => u.steamId === user.steamId))

  let insertCount = 0
  let updateCount = 0
  let deleteCount = 0

  try {
    // 插入新用户（只有新用户才会消耗自增 ID）
    if (usersToInsert.length > 0) {
      const insertResult = await db.insert(steamUser).values(usersToInsert)
      insertCount = insertResult.changes || 0
    }

    // 批量更新已存在的用户（不会影响自增 ID）
    // 使用同步事务批量执行（better-sqlite3 事务必须是同步的）
    if (usersToUpdate.length > 0) {
      db.transaction((tx) => {
        for (const user of usersToUpdate) {
          const updateResult = tx.update(steamUser)
            .set({
              accountName: user.accountName,
              personaName: user.personaName,
              rememberPassword: user.rememberPassword,
              wantsOfflineMode: user.wantsOfflineMode,
              skipOfflineModeWarning: user.skipOfflineModeWarning,
              allowAutoLogin: user.allowAutoLogin,
              mostRecent: user.mostRecent,
              timestamp: user.timestamp,
            })
            .where(eq(steamUser.steamId, user.steamId))
            .run()
          updateCount += updateResult.changes || 0
        }
      })
    }
  }
  catch (error) {
    console.error(`[DB] 保存用户失败:`, error)
  }

  // 删除不存在的用户
  if (usersToDelete.length > 0) {
    db.transaction((tx) => {
      for (const user of usersToDelete) {
        const deleteResult = tx.delete(steamUser)
          .where(eq(steamUser.steamId, user.steamId))
          .run()
        deleteCount += deleteResult.changes || 0
      }
    })
  }

  console.warn(`[DB] 成功更新 ${insertCount + updateCount}/${users.length} 个用户（新增: ${insertCount}, 更新: ${updateCount}, 删除：${deleteCount}）`)
}

/**
 * 获取所有用户
 */
export async function getAllUsers(): Promise<SteamUser[]> {
  const db = getDatabase()
  return await db.select().from(steamUser)
}

/**
 * 更新用户头像和等级信息
 */
export async function updateUserAvatarAndLevel(
  accountId: number,
  personaName: string | null,
  avatarFull: string | null,
  avatarMedium: string | null,
  avatarSmall: string | null,
  animatedAvatar: string | null,
  avatarFrame: string | null,
  level: number | null,
  levelClass: string | null,
): Promise<boolean> {
  const db = getDatabase()
  try {
    await db.update(steamUser)
      .set({
        personaName: personaName || undefined,
        avatarFull: avatarFull || undefined,
        avatarMedium: avatarMedium || undefined,
        avatarSmall: avatarSmall || undefined,
        animatedAvatar: animatedAvatar || undefined,
        avatarFrame: avatarFrame || undefined,
        level: level ?? undefined,
        levelClass: levelClass || undefined,
      })
      .where(eq(steamUser.accountId, accountId))

    console.warn(`[DB] 成功更新用户头像和等级信息 (AccountID: ${accountId})`)
    return true
  }
  catch (error) {
    console.error(`[DB] 更新用户头像和等级信息失败 (AccountID: ${accountId}):`, error)
    return false
  }
}

// ===================================================================================

/**
 * 根据 SteamID 查找用户
 */
export async function getUserBySteamId(steamId: bigint): Promise<SteamUser | null> {
  const db = getDatabase()
  const users = await db.select()
    .from(steamUser)
    .where(eq(steamUser.steamId, steamId))
    .limit(1)

  return users[0] || null
}

/**
 * 根据 AccountID 查找用户
 */
export async function getUserByAccountId(accountId: number): Promise<SteamUser | null> {
  const db = getDatabase()
  const users = await db.select()
    .from(steamUser)
    .where(eq(steamUser.accountId, accountId))
    .limit(1)

  return users[0] || null
}

/**
 * 根据账户名查找用户
 */
export async function getUserByAccountName(accountName: string): Promise<SteamUser | null> {
  const db = getDatabase()
  const users = await db.select()
    .from(steamUser)
    .where(eq(steamUser.accountName, accountName))
    .limit(1)

  return users[0] || null
}

/**
 * 删除用户
 */
export async function deleteUser(steamId: bigint): Promise<boolean> {
  const db = getDatabase()
  try {
    await db.delete(steamUser)
      .where(eq(steamUser.steamId, steamId))
    console.warn(`[DB] 成功删除用户 (SteamID: ${steamId})`)
    return true
  }
  catch (error) {
    console.error(`[DB] 删除用户失败 (SteamID: ${steamId}):`, error)
    return false
  }
}

/**
 * 获取用户统计
 */
export async function getUserStats() {
  const db = getDatabase()
  const userCount = await db.select({ count: sql<number>`count(*)` }).from(steamUser)

  return {
    totalUsers: userCount[0]?.count || 0,
  }
}
