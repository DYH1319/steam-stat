import { eq, sql } from 'drizzle-orm'
import { getDatabase } from './connection'
import { steamApp, steamUser, useAppRecord } from './schema'

/**
 * 获取综合统计数据
 */
export async function getStats() {
  const db = getDatabase()

  const [userCount, appCount, installedAppCount, recordCount] = await Promise.all([
    db.select({ count: sql<number>`count(*)` }).from(steamUser),
    db.select({ count: sql<number>`count(*)` }).from(steamApp),
    db.select({ count: sql<number>`count(*)` })
      .from(steamApp)
      .where(eq(steamApp.installed, true)),
    db.select({ count: sql<number>`count(*)` }).from(useAppRecord),
  ])

  return {
    totalUsers: userCount[0]?.count || 0,
    totalApps: appCount[0]?.count || 0,
    installedApps: installedAppCount[0]?.count || 0,
    totalRecords: recordCount[0]?.count || 0,
  }
}

/**
 * 获取应用使用统计（按应用分组）
 */
export async function getAppUsageStats(limit: number = 10) {
  const db = getDatabase()

  return await db.select({
    appId: useAppRecord.appId,
    appName: steamApp.name,
    totalDuration: sql<number>`COALESCE(SUM(${useAppRecord.duration}), 0)`,
    playCount: sql<number>`COUNT(${useAppRecord.id})`,
    lastPlayed: sql<number>`MAX(${useAppRecord.startTime})`,
  })
    .from(useAppRecord)
    .leftJoin(steamApp, eq(useAppRecord.appId, steamApp.appId))
    .groupBy(useAppRecord.appId)
    .orderBy(sql`totalDuration DESC`)
    .limit(limit)
}

/**
 * 获取用户使用统计（按用户分组）
 */
export async function getUserUsageStats(limit: number = 10) {
  const db = getDatabase()

  return await db.select({
    steamId: useAppRecord.steamId,
    accountName: steamUser.accountName,
    personaName: steamUser.personaName,
    totalDuration: sql<number>`COALESCE(SUM(${useAppRecord.duration}), 0)`,
    sessionCount: sql<number>`COUNT(${useAppRecord.id})`,
    uniqueApps: sql<number>`COUNT(DISTINCT ${useAppRecord.appId})`,
    lastActive: sql<number>`MAX(${useAppRecord.startTime})`,
  })
    .from(useAppRecord)
    .leftJoin(steamUser, eq(useAppRecord.steamId, steamUser.steamId))
    .groupBy(useAppRecord.steamId)
    .orderBy(sql`totalDuration DESC`)
    .limit(limit)
}

/**
 * 获取最近游戏记录
 */
export async function getRecentGameSessions(limit: number = 20) {
  const db = getDatabase()

  return await db.select({
    id: useAppRecord.id,
    appId: useAppRecord.appId,
    appName: steamApp.name,
    steamId: useAppRecord.steamId,
    accountName: steamUser.accountName,
    startTime: useAppRecord.startTime,
    endTime: useAppRecord.endTime,
    duration: useAppRecord.duration,
  })
    .from(useAppRecord)
    .leftJoin(steamApp, eq(useAppRecord.appId, steamApp.appId))
    .leftJoin(steamUser, eq(useAppRecord.steamId, steamUser.steamId))
    .orderBy(sql`${useAppRecord.startTime} DESC`)
    .limit(limit)
}

/**
 * 获取数据库大小信息
 */
export async function getDatabaseInfo() {
  const db = getDatabase()

  // 获取各表的行数
  const [userCount, appCount, recordCount] = await Promise.all([
    db.select({ count: sql<number>`count(*)` }).from(steamUser),
    db.select({ count: sql<number>`count(*)` }).from(steamApp),
    db.select({ count: sql<number>`count(*)` }).from(useAppRecord),
  ])

  return {
    tables: {
      users: userCount[0]?.count || 0,
      apps: appCount[0]?.count || 0,
      records: recordCount[0]?.count || 0,
    },
    totalRows: (userCount[0]?.count || 0) + (appCount[0]?.count || 0) + (recordCount[0]?.count || 0),
  }
}
