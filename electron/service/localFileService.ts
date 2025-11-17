/**
 * 本地文件读写服务
 */
import type { AppmanifestAcf, LibraryfoldersVdf, LoginusersVdf } from '../types/localFile'
import fs from 'node:fs'
import path from 'node:path'
// @ts-expect-error kvparser
import { parse } from 'kvparser'

/**
 * 读取 {SteamPath}\config\loginusers.vdf 文件
 */
export async function readLoginusersVdf(steamPath: string): Promise<Record<string, LoginusersVdf>> {
  let loginusers: Record<string, LoginusersVdf> = {}
  if (steamPath) {
    const vdfPath = path.join(steamPath, 'config', 'loginusers.vdf')
    if (fs.existsSync(vdfPath)) {
      const content = fs.readFileSync(vdfPath, 'utf-8')
      loginusers = parse(content).users

      Object.keys(loginusers).forEach((key) => {
        const userInfo = loginusers[key]
        loginusers[key] = {
          ...userInfo,
          SteamID: BigInt(key),
          RememberPassword: Number(userInfo.RememberPassword),
          WantsOfflineMode: Number(userInfo.WantsOfflineMode),
          SkipOfflineModeWarning: Number(userInfo.SkipOfflineModeWarning),
          AllowAutoLogin: Number(userInfo.AllowAutoLogin),
          MostRecent: Number(userInfo.MostRecent),
          Timestamp: Number(userInfo.Timestamp),
        }
      })
    }
  }

  return loginusers
}

/**
 * 读取 {SteamPath}\config\libraryfolders.vdf 文件
 */
export async function readLibraryfoldersVdf(steamPath: string): Promise<Record<string, LibraryfoldersVdf>> {
  let libraryfolders: Record<string, LibraryfoldersVdf> = {}
  if (steamPath) {
    const vdfPath = path.join(steamPath, 'config', 'libraryfolders.vdf')
    if (fs.existsSync(vdfPath)) {
      const content = fs.readFileSync(vdfPath, 'utf-8')
      libraryfolders = parse(content).libraryfolders

      Object.keys(libraryfolders).forEach((key) => {
        const folderId = Number(key)
        const folderInfo = libraryfolders[folderId]
        libraryfolders[folderId] = {
          ...folderInfo,
          path: String(folderInfo.path).replaceAll('\\\\', '\\'),
          contentid: BigInt(folderInfo.contentid),
          totalsize: BigInt(folderInfo.totalsize),
          update_clean_bytes_tally: BigInt(folderInfo.update_clean_bytes_tally),
          time_last_update_verified: Number(folderInfo.time_last_update_verified),
          apps: Object.entries(folderInfo.apps).reduce((acc, [key, value]) => {
            acc[key] = BigInt(value)
            return acc
          }, {} as Record<string, bigint>),
        }
      })
    }
  }

  return libraryfolders
}

/**
 * 读取 {SteamLibraryPath}\steamapps\appmanifest_{appId}.acf 文件
 */
export async function readAppmanifestAcf(steamLibraryPathList: string[]): Promise<Record<string, AppmanifestAcf>> {
  const apps: Record<string, AppmanifestAcf> = {} as Record<string, AppmanifestAcf>

  for (const steamLibraryPath of steamLibraryPathList) {
    const steamAppsFolder = path.join(steamLibraryPath, 'steamapps')

    if (!fs.existsSync(steamAppsFolder)) {
      continue
    }

    const files = fs.readdirSync(steamAppsFolder)
    for (const file of files) {
      if (file.startsWith('appmanifest_') && file.endsWith('.acf')) {
        const manifestPath = path.join(steamAppsFolder, file)
        const content = fs.readFileSync(manifestPath, 'utf8')

        const data = parse(content)
        const appState = data.AppState

        apps[appState.appid] = {
          ...appState,
          appid: Number(appState.appid),
          universe: Number(appState.universe),
          StateFlags: Number(appState.StateFlags),
          LastUpdated: Number(appState.LastUpdated),
          LastPlayed: Number(appState.LastPlayed),
          SizeOnDisk: BigInt(appState.SizeOnDisk),
          StagingSize: BigInt(appState.StagingSize),
          buildid: Number(appState.buildid),
          LastOwner: BigInt(appState.LastOwner),
          DownloadType: Number(appState.DownloadType),
          UpdateResult: Number(appState.UpdateResult),
          BytesToDownload: appState.BytesToDownload !== undefined ? BigInt(appState.BytesToDownload) : undefined,
          BytesDownloaded: appState.BytesDownloaded !== undefined ? BigInt(appState.BytesDownloaded) : undefined,
          BytesToStage: appState.BytesToStage !== undefined ? BigInt(appState.BytesToStage) : undefined,
          BytesStaged: appState.BytesStaged !== undefined ? BigInt(appState.BytesStaged) : undefined,
          TargetBuildID: Number(appState.TargetBuildID),
          AutoUpdateBehavior: Number(appState.AutoUpdateBehavior),
          AllowOtherDownloadsWhileRunning: Number(appState.AllowOtherDownloadsWhileRunning),
          ScheduledAutoUpdate: Number(appState.ScheduledAutoUpdate),
          InstalledDepots: appState.InstalledDepots
            ? Object.entries(appState.InstalledDepots).reduce((acc, [key, value]) => {
                const depot = value as { manifest?: string | number, size?: string | number }
                if (depot && (depot.manifest !== undefined || depot.size !== undefined)) {
                  acc[key] = {
                    manifest: depot.manifest !== undefined ? BigInt(depot.manifest) : 0n,
                    size: depot.size !== undefined ? BigInt(depot.size) : 0n,
                  }
                }
                return acc
              }, {} as Record<string, { manifest: bigint, size: bigint }>)
            : {},
          SharedDepots: appState.SharedDepots
            ? Object.entries(appState.SharedDepots).reduce((acc, [key, value]) => {
                acc[key] = Number(value)
                return acc
              }, {} as Record<string, number>)
            : {},
          UserConfig: appState.UserConfig
            ? Object.entries(appState.UserConfig).reduce((acc, [key, value]) => {
                acc[key] = String(value)
                return acc
              }, {} as Record<string, string>)
            : {},
          MountedConfig: appState.MountedConfig
            ? Object.entries(appState.MountedConfig).reduce((acc, [key, value]) => {
                acc[key] = String(value)
                return acc
              }, {} as Record<string, string>)
            : {},
        }
      }
    }
  }
  return apps
}

// Test
// (async () => console.warn(await readLoginusersVdf('D:\\Program Files (x86)\\Steam')))()
// (async () => console.warn(await readLibraryfoldersVdf('D:\\Program Files (x86)\\Steam')))()
// (async () => console.dir(await readAppmanifestAcf(['D:\\Program Files (x86)\\Steam', 'E:\\SteamLibrary']), { depth: null, maxArrayLength: null, colors: true }))()
