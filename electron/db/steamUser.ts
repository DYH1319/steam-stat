import type { SteamLoginUsersInfo } from '../types/steamUser'
import type { NewSteamUser, SteamUser } from './schema'
import { eq, sql } from 'drizzle-orm'
import { getDatabase } from './connection'
import { steamUser } from './schema'

/**
 * 批量插入或更新 Steam 用户信息到数据库
 */
export async function insertOrUpdateSteamUserBatch(loginUsersInfo: SteamLoginUsersInfo) {
  const db = getDatabase()
  const users: NewSteamUser[] = []

  if (loginUsersInfo.loginusers) {
    for (const [_steamId64, userInfo] of Object.entries(loginUsersInfo.loginusers)) {
      users.push({
        steamId: userInfo.SteamID,
        accountId: userInfo.AccountID,
        accountName: userInfo.AccountName,
        personaName: userInfo.PersonaName || null,
        rememberPassword: userInfo.RememberPassword === 1,
      })
    }
  }

  let results
  try {
    results = await db.insert(steamUser)
      .values(users)
      .onConflictDoUpdate({
        target: steamUser.steamId,
        set: {
          accountName: sql`excluded.account_name`,
          personaName: sql`excluded.persona_name`,
          rememberPassword: sql`excluded.remember_password`,
        },
      })
  }
  catch (error) {
    console.error(`[DB] 批量更新用户失败:`, error)
  }

  console.warn(`[DB] 成功更新 ${results?.changes ?? 0}/${users.length} 个用户`)
  return results
}

/**
 * 获取所有用户
 */
export async function getAllUsers(): Promise<SteamUser[]> {
  const db = getDatabase()
  return await db.select().from(steamUser)
}

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
