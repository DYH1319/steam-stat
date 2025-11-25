import type { GetValidRecordsResponse } from '../types/useAppRecord'
import type { NewUseAppRecord, UseAppRecord } from './schema'
import { and, desc, eq, isNotNull, isNull, ne } from 'drizzle-orm'
import { getDatabase } from './connection'
import { steamApp, useAppRecord } from './schema'

const db = getDatabase()

/**
 * 初始化 UseAppRecord
 * 统一结束掉数据库中所有未结束的记录
 */
export async function initUseAppRecord() {
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
  return await db.select().from(useAppRecord)
}

/**
 * 获取有效的记录
 */
export async function getValidRecords(): Promise<GetValidRecordsResponse[]> {
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
    .where(
      and(
        isNotNull(useAppRecord.endTime),
        ne(useAppRecord.duration, -1),
      ),
    )
}

/**
 * 开始记录
 */
export async function startRecord(steamId: bigint, appId: number) {
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
