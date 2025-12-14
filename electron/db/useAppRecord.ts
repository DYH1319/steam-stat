import type { GetValidRecordsResponse } from '../types/useAppRecord'
import type { NewUseAppRecord, UseAppRecord } from './schema'
import { and, desc, eq, gte, inArray, isNotNull, isNull, lte, ne, sql } from 'drizzle-orm'
import { getDatabase } from './connection'
import { steamApp, useAppRecord } from './schema'

/**
 * 初始化 UseAppRecord
 * 统一结束掉数据库中所有未结束的记录
 */
export async function initUseAppRecord() {
  const db = getDatabase()
  try {
    const now = new Date()
    const result = await db.update(useAppRecord)
      .set({
        endTime: now,
        duration: -1,
      })
      .where(isNull(useAppRecord.endTime))
    console.warn(`[DB] 初始化 UseAppRecord，结束了 ${result.changes} 个未完成的记录`)
  }
  catch (error) {
    console.error('[DB] 初始化 UseAppRecord 失败:', error)
  }
}

/**
 * 获取所有记录
 */
export async function getAllRecords(): Promise<UseAppRecord[]> {
  const db = getDatabase()
  return await db.select().from(useAppRecord)
}

/**
 * 获取有效的记录
 * @param steamIds 可选，筛选特定用户的记录
 * @param startDate 可选，开始日期
 * @param endDate 可选，结束日期
 */
export async function getValidRecords(
  steamIds?: bigint[],
  startDate?: Date,
  endDate?: Date,
): Promise<GetValidRecordsResponse[]> {
  const db = getDatabase()
  const conditions = [
    isNotNull(useAppRecord.endTime),
    ne(useAppRecord.duration, -1),
  ]

  // 按用户筛选
  if (steamIds && steamIds.length > 0) {
    const steamIdStrings = steamIds.map(String)

    const steamIdTextExpr = sql<string>`CAST(${useAppRecord.steamId} AS TEXT)`

    conditions.push(
      inArray(steamIdTextExpr, steamIdStrings),
    )
  }

  // 按开始日期筛选
  if (startDate) {
    conditions.push(gte(useAppRecord.startTime, startDate))
  }

  // 按结束日期筛选
  if (endDate) {
    conditions.push(lte(useAppRecord.startTime, endDate))
  }

  return await db
    .select({
      appId: useAppRecord.appId,
      steamId: useAppRecord.steamId,
      startTime: useAppRecord.startTime,
      endTime: useAppRecord.endTime,
      duration: useAppRecord.duration,
      appName: steamApp.name,
      nameLocalized: steamApp.nameLocalized,
    })
    .from(useAppRecord)
    .leftJoin(steamApp, eq(useAppRecord.appId, steamApp.appId))
    .where(and(...conditions))
}

/**
 * 开始记录
 */
export async function startRecord(steamId: bigint, appId: number) {
  const db = getDatabase()
  try {
    const newRecord: NewUseAppRecord = {
      steamId,
      appId,
      startTime: new Date(),
      endTime: null,
      duration: null,
    }
    await db.insert(useAppRecord).values(newRecord)
    console.warn(`[DB] 开始记录应用使用: SteamID=${steamId}, AppID=${appId}`)
  }
  catch (error) {
    console.error(`[DB] 开始记录应用使用失败: SteamID=${steamId}, AppID=${appId}`, error)
  }
}

/**
 * 结束记录
 * 查找最近一条该用户该应用的未结束记录，更新 endTime 和 duration
 */
export async function stopRecord(steamId: bigint, appId: number) {
  const db = getDatabase()
  try {
    const now = new Date()
    // 查找最近一条未结束的记录
    const records = await db.select()
      .from(useAppRecord)
      .where(
        and(
          eq(useAppRecord.steamId, steamId),
          eq(useAppRecord.appId, appId),
          isNull(useAppRecord.endTime),
        ),
      )
      .orderBy(desc(useAppRecord.startTime))
      .limit(1)

    if (records.length === 0) {
      console.warn(`[DB] 未找到需要结束的记录: SteamID=${steamId}, AppID=${appId}`)
      return
    }

    const record = records[0]
    const startTime = record.startTime
    const duration = Math.floor((now.getTime() - startTime.getTime()) / 1000) // 秒数

    await db.update(useAppRecord)
      .set({
        endTime: now,
        duration,
      })
      .where(eq(useAppRecord.id, record.id))

    console.warn(`[DB] 结束记录应用使用: SteamID=${steamId}, AppID=${appId}, Duration=${duration}s`)
  }
  catch (error) {
    console.error(`[DB] 结束记录应用使用失败: SteamID=${steamId}, AppID=${appId}`, error)
  }
}

/**
 * 结束所有正在运行的记录（记录当前时间为结束时间）
 */
export async function endAllRunningRecords() {
  const db = getDatabase()
  try {
    const now = new Date()
    // 获取所有正在运行的记录
    const runningRecords = await db.select()
      .from(useAppRecord)
      .where(isNull(useAppRecord.endTime))

    // 更新每条记录的结束时间和时长
    for (const record of runningRecords) {
      const duration = Math.floor((now.getTime() - record.startTime.getTime()) / 1000)
      await db.update(useAppRecord)
        .set({
          endTime: now,
          duration,
        })
        .where(eq(useAppRecord.id, record.id))
    }

    console.warn(`[DB] 结束了 ${runningRecords.length} 个正在运行的记录`)
    return runningRecords.length
  }
  catch (error) {
    console.error('[DB] 结束正在运行的记录失败:', error)
    throw error
  }
}

/**
 * 作废所有正在运行的记录（duration 设为 -1）
 */
export async function discardAllRunningRecords() {
  const db = getDatabase()
  try {
    const now = new Date()
    const result = await db.update(useAppRecord)
      .set({
        endTime: now,
        duration: -1,
      })
      .where(isNull(useAppRecord.endTime))

    console.warn(`[DB] 作废了 ${result.changes} 个正在运行的记录`)
    return result.changes
  }
  catch (error) {
    console.error('[DB] 作废正在运行的记录失败:', error)
    throw error
  }
}
