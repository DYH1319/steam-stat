import type { AppinfoVdf, AppmanifestAcf, LibraryfoldersVdf } from '../types/localFile'
import type { NewSteamApp, SteamApp } from './schema'
import { eq, inArray, notInArray, sql } from 'drizzle-orm'
// import { calculateDirectorySize } from '../util/utils'
import { getDatabase } from './connection'
import { steamApp } from './schema'

/**
 * 批量插入或更新 Steam 应用信息到数据库
 */
export async function insertOrUpdateSteamAppBatch(libraryfoldersVdf: Record<string, LibraryfoldersVdf>, appsAcf: Record<string, AppmanifestAcf>, _appinfo: Record<string, AppinfoVdf>) {
  const db = getDatabase()
  // 创建 appId -> 库文件夹路径映射
  const appIdToLibraryFolderMap = new Map<string, string>()
  Object.values(libraryfoldersVdf).forEach((folder) => {
    Object.keys(folder.apps).forEach((appId) => {
      appIdToLibraryFolderMap.set(appId, folder.path)
    })
  })

  const apps: NewSteamApp[] = Object.values(appsAcf).map((app) => {
    // const appInfoData = appinfo[app.appid]
    const installDirPath = `${appIdToLibraryFolderMap.get(String(app.appid))}\\steamapps\\common\\${app.installdir}`

    // 计算文件夹实际大小
    // const appOnDiskReal = calculateDirectorySize(installDirPath)
    const appOnDiskReal = undefined

    return {
      appId: app.appid,
      name: app.name,
      // nameLocalized: appInfoData.name_localized || {},
      nameLocalized: {},
      installed: true,
      installDir: app.installdir,
      installDirPath,
      appOnDisk: app.SizeOnDisk,
      appOnDiskReal,
      isRunning: false,
      // type: appInfoData.type,
      // developer: appInfoData.developer,
      // publisher: appInfoData.publisher,
      // steamReleaseDate: appInfoData.steam_release_date ? new Date(appInfoData.steam_release_date) : undefined,
      // isFreeApp: appInfoData.isfreeapp !== undefined ? (appInfoData.isfreeapp === 1) : false,
      type: undefined,
      developer: undefined,
      publisher: undefined,
      steamReleaseDate: undefined,
      isFreeApp: undefined,
      refreshTime: new Date(),
    }
  })

  if (apps.length === 0) {
    console.warn('[DB] 没有找到应用数据')
    return
  }

  // 查询数据库中已存在的 appId
  const appIds = apps.map(app => app.appId)
  const existingApps = await db.select({ appId: steamApp.appId })
    .from(steamApp)
    .where(inArray(steamApp.appId, appIds))

  const existingAppIds = new Set(existingApps.map(app => app.appId))

  // 分离新增和更新的应用
  const appsToInsert = apps.filter(app => !existingAppIds.has(app.appId))
  const appsToUpdate = apps.filter(app => existingAppIds.has(app.appId))

  let insertCount = 0
  let updateCount = 0

  try {
    // 插入新应用（只有新应用才会消耗自增 ID）
    if (appsToInsert.length > 0) {
      const insertResult = await db.insert(steamApp).values(appsToInsert)
      insertCount = insertResult.changes || 0
    }

    // 批量更新已存在的应用（不会影响自增 ID）
    // 使用同步事务批量执行（better-sqlite3 事务必须是同步的）
    if (appsToUpdate.length > 0) {
      db.transaction((tx) => {
        for (const app of appsToUpdate) {
          tx.update(steamApp)
            .set({
              name: app.name,
              nameLocalized: app.nameLocalized,
              installed: app.installed,
              installDir: app.installDir,
              installDirPath: app.installDirPath,
              appOnDisk: app.appOnDisk,
              appOnDiskReal: app.appOnDiskReal,
              isRunning: app.isRunning,
              type: app.type,
              developer: app.developer,
              publisher: app.publisher,
              steamReleaseDate: app.steamReleaseDate,
              isFreeApp: app.isFreeApp,
              refreshTime: app.refreshTime,
            })
            .where(eq(steamApp.appId, app.appId))
            .run()
          updateCount++
        }
      })
    }
  }
  catch (error) {
    console.error(`[DB] 保存应用失败:`, error)
  }

  // 更新卸载应用
  const updateUninstalledAppsResult = await db.update(steamApp)
    .set({ installed: false })
    .where(notInArray(steamApp.appId, appIds))

  console.warn(`[DB] 成功保存 ${insertCount + updateCount}/${apps.length} 个应用（新增: ${insertCount}, 更新: ${updateCount}）`)
  console.warn(`[DB] 成功更新 ${updateUninstalledAppsResult?.changes} 个卸载应用`)
}

/**
 * 获取所有应用
 */
export async function getAllApps(): Promise<SteamApp[]> {
  const db = getDatabase()
  return await db.select().from(steamApp)
}

/**
 * 获取已安装的应用
 */
export async function getInstalledApps(): Promise<SteamApp[]> {
  const db = getDatabase()
  return await db.select()
    .from(steamApp)
    .where(eq(steamApp.installed, true))
}

/**
 * 更新应用运行状态
 */
export async function updateAppRunningStatus(appIds: number[]) {
  const db = getDatabase()
  try {
    await db.update(steamApp)
      .set({ isRunning: true })
      .where(inArray(steamApp.appId, appIds))
    await db.update(steamApp)
      .set({ isRunning: false })
      .where(notInArray(steamApp.appId, appIds))
  }
  catch (error) {
    console.error(`[DB] 更新应用运行状态失败:`, error)
  }
}

/**
 * 获取运行中的应用
 */
export async function getRunningApps(): Promise<SteamApp[]> {
  const db = getDatabase()
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
