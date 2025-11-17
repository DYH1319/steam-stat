import type { AppmanifestAcf, LibraryfoldersVdf } from '../types/localFile'
import type { NewSteamApp, SteamApp } from './schema'
import { eq, inArray, notInArray, sql } from 'drizzle-orm'
import { getDatabase } from './connection'
import { steamApp } from './schema'

const db = getDatabase()

/**
 * 批量插入或更新 Steam 应用信息到数据库
 */
export async function insertOrUpdateSteamAppBatch(appsAcf: Record<string, AppmanifestAcf>, libraryfoldersVdf: Record<string, LibraryfoldersVdf>) {
  // 创建 appId -> 库文件夹路径映射
  const appIdToLibraryFolderMap = new Map<string, string>()
  Object.values(libraryfoldersVdf).forEach((folder) => {
    Object.keys(folder.apps).forEach((appId) => {
      appIdToLibraryFolderMap.set(appId, folder.path)
    })
  })

  const apps: NewSteamApp[] = Object.values(appsAcf).map(app => ({
    appId: app.appid,
    name: app.name,
    nameLocalized: [],
    installed: true,
    installDir: app.installdir,
    installDirPath: `${appIdToLibraryFolderMap.get(String(app.appid))}\\${app.installdir}`,
    appOnDisk: app.SizeOnDisk,
    appOnDiskReal: undefined,
    isRunning: false,
    refreshTime: new Date(),
  }))

  if (apps.length === 0) {
    console.warn('[DB] 没有找到应用数据')
  }

  // 批量插入或更新应用
  let result
  try {
    result = await db.insert(steamApp)
      .values(apps)
      .onConflictDoUpdate({
        target: steamApp.appId,
        set: {
          name: sql`EXCLUDED.name`,
          nameLocalized: sql`EXCLUDED.name_localized`,
          installed: sql`EXCLUDED.installed`,
          installDir: sql`EXCLUDED.install_dir`,
          installDirPath: sql`EXCLUDED.install_dir_path`,
          appOnDisk: sql`EXCLUDED.app_on_disk`,
          appOnDiskReal: sql`EXCLUDED.app_on_disk_real`,
          isRunning: sql`EXCLUDED.is_running`,
          refreshTime: sql`EXCLUDED.refresh_time`,
        },
      })
  }
  catch (error) {
    console.error(`[DB] 保存应用失败:`, error)
  }

  // 更新卸载应用
  const updateUninstalledAppsResult = await db.update(steamApp)
    .set({ installed: false })
    .where(notInArray(steamApp.appId, apps.map(app => app.appId)))

  console.warn(`[DB] 成功保存 ${result?.changes}/${apps.length} 个应用`)
  console.warn(`[DB] 成功更新 ${updateUninstalledAppsResult?.changes} 个卸载应用`)
}

/**
 * 获取所有应用
 */
export async function getAllApps(): Promise<SteamApp[]> {
  return await db.select().from(steamApp)
}

/**
 * 获取已安装的应用
 */
export async function getInstalledApps(): Promise<SteamApp[]> {
  return await db.select()
    .from(steamApp)
    .where(eq(steamApp.installed, true))
}

/**
 * 更新应用运行状态
 */
export async function updateAppRunningStatus(appIds: number[], isRunning: boolean) {
  try {
    await db.update(steamApp)
      .set({ isRunning })
      .where(inArray(steamApp.appId, appIds))
  }
  catch (error) {
    console.error(`[DB] 更新应用运行状态失败:`, error)
  }
}

/**
 * 获取运行中的应用
 */
export async function getRunningApps(): Promise<SteamApp[]> {
  return await db.select()
    .from(steamApp)
    .where(eq(steamApp.isRunning, true))
}

// ===================================================================================

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
