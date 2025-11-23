import type { SteamApp } from '../db/schema'
import * as steamApp from '../db/steamApp'
import * as updateAppRunningStatusJob from '../job/updateAppRunningStatusJob'
import * as localFileService from './localFileService'

/**
 * 初始化或更新 Steam 应用信息到数据库
 */
export async function initOrUpdateSteamApp(steamPath: string) {
  const libraryfoldersVdf = await localFileService.readLibraryfoldersVdf(steamPath)
  const appsAcf = await localFileService.readAppmanifestAcf(Object.values(libraryfoldersVdf).map(folder => folder.path))
  // const appinfo = await localFileService.readAppinfoVdf(steamPath)
  const appinfo = {}
  await steamApp.insertOrUpdateSteamAppBatch(libraryfoldersVdf, appsAcf, appinfo)
}

/**
 * 获取 Steam 应用信息
 */
export async function getSteamAppInfo(): Promise<SteamApp[]> {
  return steamApp.getAllApps()
}

/**
 * 刷新 Steam 应用信息（重新读取文件并写入数据库，返回最新数据）
 */
export async function refreshSteamAppInfo(steamPath: string): Promise<SteamApp[]> {
  await initOrUpdateSteamApp(steamPath)
  return steamApp.getAllApps()
}

/**
 * 获取已安装的 Steam 应用信息
 */
export async function getInstalledSteamAppInfo(): Promise<SteamApp[]> {
  return steamApp.getInstalledApps()
}

/**
 * 获取库文件夹路径数组
 */
export async function getLibraryFolders(steamPath: string): Promise<string[]> {
  return Object.values(await localFileService.readLibraryfoldersVdf(steamPath)).map(folder => folder.path)
}

/**
 * 更新应用运行状态
 */
export async function updateAppRunningStatus(appIds: number[]) {
  await steamApp.updateAppRunningStatus(appIds)
}

/**
 * 获取运行中的 Steam 应用信息
 */
export async function getRunningSteamAppInfo(): Promise<{ apps: SteamApp[], lastUpdateTime: number }> {
  const jobStatus = updateAppRunningStatusJob.getJobStatus()
  return { apps: await steamApp.getRunningApps(), lastUpdateTime: jobStatus.lastUpdateTime }
}
