import type { InstalledApps } from '../steam/test/getInstalledApps'
import type { NewSteamApp, SteamApp } from './schema'
import { eq, sql } from 'drizzle-orm'
import { getDatabase } from './connection'
import { steamApp } from './schema'

/**
 * 保存 Steam 应用信息到数据库
 */
export async function saveSteamApps(installedApps: InstalledApps[]) {
  const db = getDatabase()
  const apps: NewSteamApp[] = installedApps.map(app => ({
    appId: app.appId,
    name: app.name,
    nameLocalized: [],
    installed: true, // 因为这些都是已安装的应用
    installDir: app.installDir,
    installDirPath: app.gameFolder,
    appOnDisk: app.sizeOnDisk && app.sizeOnDisk > 0 ? app.sizeOnDisk : null,
    appOnDiskReal: null, // 暂时没有这个数据
  }))

  if (apps.length === 0) {
    console.warn('[DB] 没有找到应用数据')
    return []
  }

  // 批量插入或更新应用
  const results = []
  for (const app of apps) {
    try {
      const result = await db.insert(steamApp)
        .values(app)
        .onConflictDoUpdate({
          target: steamApp.appId,
          set: {
            name: sql`EXCLUDED.name`,
            installed: sql`EXCLUDED.installed`,
            installDir: sql`EXCLUDED.install_dir`,
            installDirPath: sql`EXCLUDED.install_dir_path`,
            appOnDisk: sql`EXCLUDED.app_on_disk`,
          },
        })
      results.push(result)
    }
    catch (error) {
      console.error(`[DB] 保存应用失败 (AppID: ${app.appId}):`, error)
    }
  }

  console.warn(`[DB] 成功保存 ${results.length}/${apps.length} 个应用`)
  return results
}

/**
 * 获取所有应用
 */
export async function getAllApps(): Promise<SteamApp[]> {
  const db = getDatabase()
  return await db.select().from(steamApp).orderBy(steamApp.name)
}

/**
 * 获取已安装的应用
 */
export async function getInstalledApps(): Promise<SteamApp[]> {
  const db = getDatabase()
  return await db.select()
    .from(steamApp)
    .where(eq(steamApp.installed, true))
    .orderBy(steamApp.name)
}

/**
 * 根据 AppID 查找应用
 */
export async function getAppById(appId: number): Promise<SteamApp | null> {
  const db = getDatabase()
  const apps = await db.select()
    .from(steamApp)
    .where(eq(steamApp.appId, appId))
    .limit(1)

  return apps[0] || null
}

/**
 * 根据名称搜索应用
 */
export async function searchAppsByName(name: string): Promise<SteamApp[]> {
  const db = getDatabase()
  return await db.select()
    .from(steamApp)
    .where(sql`${steamApp.name} LIKE ${`%${name}%`}`)
    .orderBy(steamApp.name)
}

/**
 * 更新应用安装状态
 */
export async function updateAppInstallStatus(appId: number, installed: boolean): Promise<boolean> {
  const db = getDatabase()
  try {
    await db.update(steamApp)
      .set({ installed })
      .where(eq(steamApp.appId, appId))
    console.warn(`[DB] 成功更新应用安装状态 (AppID: ${appId}, installed: ${installed})`)
    return true
  }
  catch (error) {
    console.error(`[DB] 更新应用安装状态失败 (AppID: ${appId}):`, error)
    return false
  }
}

/**
 * 删除应用
 */
export async function deleteApp(appId: number): Promise<boolean> {
  const db = getDatabase()
  try {
    await db.delete(steamApp)
      .where(eq(steamApp.appId, appId))
    console.warn(`[DB] 成功删除应用 (AppID: ${appId})`)
    return true
  }
  catch (error) {
    console.error(`[DB] 删除应用失败 (AppID: ${appId}):`, error)
    return false
  }
}

/**
 * 获取应用统计
 */
export async function getAppStats() {
  const db = getDatabase()
  const totalApps = await db.select({ count: sql<number>`count(*)` }).from(steamApp)
  const installedApps = await db.select({ count: sql<number>`count(*)` })
    .from(steamApp)
    .where(eq(steamApp.installed, true))

  return {
    totalApps: totalApps[0]?.count || 0,
    installedApps: installedApps[0]?.count || 0,
  }
}
