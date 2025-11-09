/**
 * 用户数据操作模块
 */

import type { SteamUser } from '../steam'
import { getDatabase } from './index'

/**
 * 保存用户列表到数据库
 */
export function saveUsers(users: SteamUser[]): void {
  const db = getDatabase()
  const stmt = db.prepare(`
    INSERT OR REPLACE INTO steam_users
    (steam_id, account_name, persona_name, timestamp, most_recent, remember_password, updated_at)
    VALUES (?, ?, ?, ?, ?, ?, strftime('%s', 'now'))
  `)

  const transaction = db.transaction((userList: SteamUser[]) => {
    for (const user of userList) {
      stmt.run(
        user.steamId,
        user.accountName,
        user.personaName,
        user.timestamp,
        user.mostRecent ? 1 : 0,
        user.rememberPassword ? 1 : 0,
      )
    }
  })

  transaction(users)
  console.warn('[DB] 保存用户列表成功，共', users.length, '个用户')
}

/**
 * 从数据库获取用户列表
 */
export function getUsers(): SteamUser[] {
  const db = getDatabase()
  const stmt = db.prepare('SELECT * FROM steam_users ORDER BY timestamp DESC')
  const rows = stmt.all() as any[]

  return rows.map(row => ({
    steamId: row.steam_id,
    accountName: row.account_name,
    personaName: row.persona_name,
    timestamp: row.timestamp,
    mostRecent: Boolean(row.most_recent),
    rememberPassword: Boolean(row.remember_password),
  }))
}

/**
 * 获取当前用户
 */
export function getCurrentUser(): SteamUser | null {
  const db = getDatabase()
  const stmt = db.prepare('SELECT * FROM steam_users WHERE most_recent = 1 LIMIT 1')
  const row = stmt.get() as any

  if (!row) {
    // 如果没有标记为当前用户，返回最近的一个
    const stmt2 = db.prepare('SELECT * FROM steam_users ORDER BY timestamp DESC LIMIT 1')
    const row2 = stmt2.get() as any
    if (!row2) {
      return null
    }

    return {
      steamId: row2.steam_id,
      accountName: row2.account_name,
      personaName: row2.persona_name,
      timestamp: row2.timestamp,
      mostRecent: Boolean(row2.most_recent),
      rememberPassword: Boolean(row2.remember_password),
    }
  }

  return {
    steamId: row.steam_id,
    accountName: row.account_name,
    personaName: row.persona_name,
    timestamp: row.timestamp,
    mostRecent: Boolean(row.most_recent),
    rememberPassword: Boolean(row.remember_password),
  }
}
