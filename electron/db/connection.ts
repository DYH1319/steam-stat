import type { Database } from 'better-sqlite3'
import fs from 'node:fs'
import { createRequire } from 'node:module'
import path from 'node:path'
import { drizzle } from 'drizzle-orm/better-sqlite3'
import { app } from 'electron'

// 使用 createRequire 加载原生模块
const require = createRequire(import.meta.url)
const DatabaseConstructor = require('better-sqlite3') as any

let sqlite: Database | null = null
let db: ReturnType<typeof drizzle> | null = null

/**
 * 初始化数据库连接
 */
export function initDatabase() {
  if (db) {
    console.warn('[DB] 数据库连接已存在')
    return db
  }

  // 使用 Electron 的 userData 路径存储数据库
  const userDataPath = app.getPath('userData')
  const dbDir = path.join(userDataPath, 'Database')

  // 确保数据库目录存在
  if (!fs.existsSync(dbDir)) {
    fs.mkdirSync(dbDir, { recursive: true })
  }

  const dbPath = path.join(dbDir, 'steam-stat.db')
  console.warn('[DB] 初始化数据库连接:', dbPath)

  sqlite = new DatabaseConstructor(dbPath) as Database
  db = drizzle(sqlite, { logger: false })
  return db
}

/**
 * 获取数据库连接
 */
export function getDatabase() {
  if (!db) {
    return initDatabase()
  }
  return db
}

/**
 * 关闭数据库连接
 */
export function closeDatabase() {
  if (sqlite) {
    sqlite.close()
    sqlite = null
    db = null
    console.warn('[DB] 数据库连接已关闭')
  }
}

/**
 * 检查数据库连接状态
 */
export function isDatabaseConnected(): boolean {
  return db !== null && sqlite !== null
}
