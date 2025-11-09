/**
 * 游戏数据操作模块
 */

import type { SteamGame } from '../steam'
import { getDatabase } from './index'

/**
 * 保存游戏列表到数据库
 */
export function saveGames(games: SteamGame[]): void {
  const db = getDatabase()
  const stmt = db.prepare(`
    INSERT OR REPLACE INTO steam_games
    (app_id, name, install_dir, game_folder, library_folder, exe_paths, updated_at)
    VALUES (?, ?, ?, ?, ?, ?, strftime('%s', 'now'))
  `)

  const transaction = db.transaction((gameList: SteamGame[]) => {
    for (const game of gameList) {
      stmt.run(
        game.appId,
        game.name,
        game.installDir,
        game.gameFolder,
        game.libraryFolder,
        JSON.stringify(game.exePaths || []),
      )
    }
  })

  transaction(games)
  console.warn('[DB] 保存游戏列表成功，共', games.length, '个游戏')
}

/**
 * 从数据库获取游戏列表
 */
export function getGames(): SteamGame[] {
  const db = getDatabase()
  const stmt = db.prepare('SELECT * FROM steam_games ORDER BY name')
  const rows = stmt.all() as any[]

  return rows.map(row => ({
    appId: row.app_id,
    name: row.name,
    installDir: row.install_dir,
    gameFolder: row.game_folder,
    libraryFolder: row.library_folder,
    exePaths: JSON.parse(row.exe_paths || '[]'),
  }))
}

/**
 * 根据 AppID 获取游戏
 */
export function getGameByAppId(appId: string): SteamGame | null {
  const db = getDatabase()
  const stmt = db.prepare('SELECT * FROM steam_games WHERE app_id = ?')
  const row = stmt.get(appId) as any

  if (!row) {
    return null
  }

  return {
    appId: row.app_id,
    name: row.name,
    installDir: row.install_dir,
    gameFolder: row.game_folder,
    libraryFolder: row.library_folder,
    exePaths: JSON.parse(row.exe_paths || '[]'),
  }
}
