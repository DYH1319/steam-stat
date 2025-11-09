/**
 * 数据库模块
 * 使用 SQLite 存储 Steam 相关数据
 */

import { createRequire } from 'node:module'
import path from 'node:path'
import { app } from 'electron'

// 使用 createRequire 加载原生模块
const require = createRequire(import.meta.url)
const DatabaseConstructor = require('better-sqlite3') as any

interface DatabaseInstance {
  exec: (sql: string) => void
  prepare: (sql: string) => any
  pragma: (pragma: string) => any
  close: () => void
  transaction: (fn: (...args: any[]) => any) => (...args: any[]) => any
}

type DatabaseType = DatabaseInstance

let db: DatabaseType | null = null

/**
 * 获取数据库实例
 */
export function getDatabase(): DatabaseType {
  if (!db) {
    // 确保 app 已经 ready
    if (!app.isReady()) {
      throw new Error('[DB] 数据库必须在 app ready 之后初始化')
    }

    const userDataPath = app.getPath('userData')
    const dbPath = path.join(userDataPath, 'steam-stat.db')
    console.warn('[DB] 数据库路径:', dbPath)

    db = new DatabaseConstructor(dbPath)
    db!.pragma('journal_mode = WAL')

    initDatabase()
  }

  return db!
}

/**
 * 初始化数据库表
 */
function initDatabase() {
  if (!db) {
    return
  }

  // 用户表
  db.exec(`
    CREATE TABLE IF NOT EXISTS steam_users (
      steam_id TEXT PRIMARY KEY,
      account_name TEXT NOT NULL,
      persona_name TEXT,
      timestamp INTEGER,
      most_recent INTEGER DEFAULT 0,
      remember_password INTEGER DEFAULT 0,
      created_at INTEGER DEFAULT (strftime('%s', 'now')),
      updated_at INTEGER DEFAULT (strftime('%s', 'now'))
    )
  `)

  // 游戏表
  db.exec(`
    CREATE TABLE IF NOT EXISTS steam_games (
      app_id TEXT PRIMARY KEY,
      name TEXT NOT NULL,
      install_dir TEXT,
      game_folder TEXT,
      library_folder TEXT,
      exe_paths TEXT,
      created_at INTEGER DEFAULT (strftime('%s', 'now')),
      updated_at INTEGER DEFAULT (strftime('%s', 'now'))
    )
  `)

  // 游戏游玩记录表
  db.exec(`
    CREATE TABLE IF NOT EXISTS game_play_records (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      app_id TEXT NOT NULL,
      game_name TEXT NOT NULL,
      steam_id TEXT,
      exe_path TEXT,
      pid INTEGER,
      start_time INTEGER NOT NULL,
      end_time INTEGER,
      duration INTEGER,
      created_at INTEGER DEFAULT (strftime('%s', 'now')),
      FOREIGN KEY (app_id) REFERENCES steam_games(app_id),
      FOREIGN KEY (steam_id) REFERENCES steam_users(steam_id)
    )
  `)

  // 创建索引
  db.exec(`
    CREATE INDEX IF NOT EXISTS idx_game_play_records_app_id ON game_play_records(app_id);
    CREATE INDEX IF NOT EXISTS idx_game_play_records_steam_id ON game_play_records(steam_id);
    CREATE INDEX IF NOT EXISTS idx_game_play_records_start_time ON game_play_records(start_time);
  `)

  console.warn('[DB] 数据库初始化完成')
}

/**
 * 关闭数据库连接
 */
export function closeDatabase() {
  if (db) {
    db.close()
    db = null
    console.warn('[DB] 数据库连接已关闭')
  }
}

// 导出类型
export type { DatabaseType as Database }
