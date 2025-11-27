import type { Database } from 'better-sqlite3'
import fs from 'node:fs'
import { createRequire } from 'node:module'
import path from 'node:path'
import process from 'node:process'
import { fileURLToPath } from 'node:url'
import { drizzle } from 'drizzle-orm/better-sqlite3'
import { migrate } from 'drizzle-orm/better-sqlite3/migrator'
import { app } from 'electron'
import * as schema from './schema'

// 使用 createRequire 加载原生模块
const require = createRequire(import.meta.url)
const DatabaseConstructor = require('better-sqlite3') as any

let sqlite: Database | null = null
let db: ReturnType<typeof drizzle> | null = null

const __filename = fileURLToPath(import.meta.url)
const __dirname = path.dirname(__filename)

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

  // 检查数据库文件是否是新创建的
  const isNewDatabase = !fs.existsSync(dbPath)

  sqlite = new DatabaseConstructor(dbPath) as Database
  db = drizzle(sqlite, { logger: false, schema })

  // 初始化数据库表结构
  // 统一使用 drizzle 的 migrate 功能，确保版本追踪正确
  if (isNewDatabase || needsMigration()) {
    const action = isNewDatabase ? '初始化' : '迁移'
    console.warn(`[DB] 正在${action}数据库...`)

    try {
      const migrationsFolder = app.isPackaged
        ? path.join(process.resourcesPath, 'drizzle')
        : path.join(__dirname, '../drizzle')

      if (fs.existsSync(migrationsFolder)) {
        // 对于全新数据库，migrate 会创建所有表
        // 对于已有数据库，migrate 只应用未执行的迁移
        // drizzle 会自动创建并维护 __drizzle_migrations 表来追踪版本
        migrate(db, { migrationsFolder })
        console.warn(`[DB] 数据库${action}完成`)
      }
      else {
        console.error('[DB] 未找到 migrations 文件夹:', migrationsFolder)
      }
    }
    catch (error) {
      console.error(`[DB] 数据库${action}失败:`, error)
      throw error
    }
  }

  return db
}

/**
 * 检查是否需要迁移
 * 通过比对数据库中已执行的迁移和文件系统中的迁移文件来判断
 */
function needsMigration(): boolean {
  if (!sqlite) {
    return false
  }

  try {
    // 检查是否存在 __drizzle_migrations 表
    const tableExists = sqlite.prepare(`
      SELECT name FROM sqlite_master
      WHERE type='table' AND name='__drizzle_migrations'
    `).get()

    // 如果不存在迁移表，说明是全新数据库或旧版本数据库，需要迁移
    if (!tableExists) {
      return true
    }

    // 获取数据库中已执行的迁移记录
    const executedMigrations = sqlite.prepare(`
      SELECT hash, created_at FROM __drizzle_migrations
      ORDER BY created_at DESC
      LIMIT 1
    `).all()

    // 获取文件系统中的迁移文件
    const migrationsFolder = app.isPackaged
      ? path.join(process.resourcesPath, 'drizzle')
      : path.join(__dirname, '../drizzle')

    if (!fs.existsSync(migrationsFolder)) {
      // 没有迁移文件夹，如果数据库有表就不需要迁移
      const tables = sqlite.prepare(`
        SELECT name FROM sqlite_master
        WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name != '__drizzle_migrations'
      `).all()
      return tables.length === 0
    }

    // 读取迁移文件数量
    const migrationFiles = fs.readdirSync(migrationsFolder)
      .filter(file => file.endsWith('.sql'))

    // 如果迁移文件数量大于已执行的迁移数量，需要迁移
    if (migrationFiles.length > executedMigrations.length) {
      return true
    }

    // 否则不需要迁移
    return false
  }
  catch (error) {
    console.error('[DB] 检查迁移状态失败:', error)
    // 出错时返回 true，让 migrate 函数自己判断
    return true
  }
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
