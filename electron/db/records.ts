/**
 * 游戏游玩记录操作模块
 */

import { getDatabase } from './index'

export interface GamePlayRecord {
  id?: number
  appId: string
  gameName: string
  steamId?: string
  exePath?: string
  pid?: number
  startTime: number
  endTime?: number
  duration?: number
  createdAt?: number
}

/**
 * 开始游戏记录
 */
export function startGameRecord(record: {
  appId: string
  gameName: string
  steamId?: string
  exePath?: string
  pid?: number
}): number {
  const db = getDatabase()
  const stmt = db.prepare(`
    INSERT INTO game_play_records
    (app_id, game_name, steam_id, exe_path, pid, start_time)
    VALUES (?, ?, ?, ?, ?, strftime('%s', 'now'))
  `)

  const result = stmt.run(
    record.appId,
    record.gameName,
    record.steamId || null,
    record.exePath || null,
    record.pid || null,
  )

  console.warn('[DB] 开始游戏记录:', record.gameName, '记录ID:', result.lastInsertRowid)
  return result.lastInsertRowid as number
}

/**
 * 结束游戏记录
 */
export function endGameRecord(recordId: number): void {
  const db = getDatabase()
  const stmt = db.prepare(`
    UPDATE game_play_records
    SET end_time = strftime('%s', 'now'),
        duration = strftime('%s', 'now') - start_time
    WHERE id = ? AND end_time IS NULL
  `)

  stmt.run(recordId)
  console.warn('[DB] 结束游戏记录，记录ID:', recordId)
}

/**
 * 获取进行中的游戏记录
 */
export function getActiveRecords(): GamePlayRecord[] {
  const db = getDatabase()
  const stmt = db.prepare(`
    SELECT * FROM game_play_records
    WHERE end_time IS NULL
    ORDER BY start_time DESC
  `)

  const rows = stmt.all() as any[]

  return rows.map(row => ({
    id: row.id,
    appId: row.app_id,
    gameName: row.game_name,
    steamId: row.steam_id,
    exePath: row.exe_path,
    pid: row.pid,
    startTime: row.start_time,
    endTime: row.end_time,
    duration: row.duration,
    createdAt: row.created_at,
  }))
}

/**
 * 根据 PID 查找活跃记录
 */
export function getActiveRecordByPid(pid: number): GamePlayRecord | null {
  const db = getDatabase()
  const stmt = db.prepare(`
    SELECT * FROM game_play_records
    WHERE pid = ? AND end_time IS NULL
    LIMIT 1
  `)

  const row = stmt.get(pid) as any

  if (!row) {
    return null
  }

  return {
    id: row.id,
    appId: row.app_id,
    gameName: row.game_name,
    steamId: row.steam_id,
    exePath: row.exe_path,
    pid: row.pid,
    startTime: row.start_time,
    endTime: row.end_time,
    duration: row.duration,
    createdAt: row.created_at,
  }
}

/**
 * 根据 AppID 查找活跃记录
 */
export function getActiveRecordByAppId(appId: string): GamePlayRecord | null {
  const db = getDatabase()
  const stmt = db.prepare(`
    SELECT * FROM game_play_records
    WHERE app_id = ? AND end_time IS NULL
    ORDER BY start_time DESC
    LIMIT 1
  `)

  const row = stmt.get(appId) as any

  if (!row) {
    return null
  }

  return {
    id: row.id,
    appId: row.app_id,
    gameName: row.game_name,
    steamId: row.steam_id,
    exePath: row.exe_path,
    pid: row.pid,
    startTime: row.start_time,
    endTime: row.end_time,
    duration: row.duration,
    createdAt: row.created_at,
  }
}

/**
 * 获取游戏历史记录
 */
export function getGameRecords(appId?: string, limit = 100): GamePlayRecord[] {
  const db = getDatabase()

  let sql = `
    SELECT * FROM game_play_records
    WHERE 1=1
  `

  const params: any[] = []

  if (appId) {
    sql += ' AND app_id = ?'
    params.push(appId)
  }

  sql += ' ORDER BY start_time DESC LIMIT ?'
  params.push(limit)

  const stmt = db.prepare(sql)
  const rows = stmt.all(...params) as any[]

  return rows.map(row => ({
    id: row.id,
    appId: row.app_id,
    gameName: row.game_name,
    steamId: row.steam_id,
    exePath: row.exe_path,
    pid: row.pid,
    startTime: row.start_time,
    endTime: row.end_time,
    duration: row.duration,
    createdAt: row.created_at,
  }))
}

/**
 * 获取游戏总游玩时长（秒）
 */
export function getGameTotalPlayTime(appId: string): number {
  const db = getDatabase()
  const stmt = db.prepare(`
    SELECT COALESCE(SUM(duration), 0) as total
    FROM game_play_records
    WHERE app_id = ? AND duration IS NOT NULL
  `)

  const row = stmt.get(appId) as any
  return row.total || 0
}
