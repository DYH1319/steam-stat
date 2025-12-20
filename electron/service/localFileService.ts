/**
 * 本地文件读写服务
 */
import type { AppinfoVdf, AppmanifestAcf, LibraryfoldersVdf, LocalconfigVdf, LoginusersVdf } from '../types/localFile'
import fs from 'node:fs'
import path from 'node:path'
// @ts-expect-error kvparser
import { parse } from 'kvparser'
import { getCSharpParserPath, runCommand } from '../util/utils'

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
          AccountName: String(userInfo.AccountName),
          PersonaName: String(userInfo.PersonaName),
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

/**
 * 读取 {SteamPath}\appcache\appinfo.vdf 文件
 *
 * 此方法会：
 * 1. 使用 C# 解析器（SteamAppInfoParser.exe）解密 Steam 的二进制 appinfo.vdf 文件
 * 2. 将解密后的数据解析为 VDF 文本格式
 * 3. 提取应用信息并转换为 AppinfoVdf 格式
 * 4. 返回以 appid 为 key 的 Record 对象
 *
 * @param steamPath Steam 安装路径
 * @returns 应用信息对象，key 为 appid，value 为 AppinfoVdf
 *
 * @example
 * const appinfo = await readAppinfoVdf('D:\\Program Files (x86)\\Steam')
 * console.log(appinfo['730']) // Counter-Strike 2 的信息
 */
export async function readAppinfoVdf(steamPath: string): Promise<Record<string, AppinfoVdf>> {
  const appinfo: Record<string, AppinfoVdf> = {}

  if (!steamPath) {
    return appinfo
  }

  const vdfPath = path.join(steamPath, 'appcache', 'appinfo.vdf')
  if (!fs.existsSync(vdfPath)) {
    return appinfo
  }

  try {
    // 获取 C# 解析器路径
    const { exePath, exeDir } = getCSharpParserPath()

    if (!fs.existsSync(exePath)) {
      console.error('[readAppinfoVdf] C# 解析器不存在:', exePath)
      return appinfo
    }

    // 执行 C# 解析器解密 appinfo.vdf
    const result = await runCommand(exePath, [`"${vdfPath}"`], exeDir)

    if (!result.success) {
      console.error('[readAppinfoVdf] C# 解析器执行失败:', result.error)
      return appinfo
    }

    // 读取生成的文本文件
    const outputPath = path.join(exeDir, 'appinfo_text.vdf')
    if (!fs.existsSync(outputPath)) {
      console.error('[readAppinfoVdf] 未找到生成的文本文件:', outputPath)
      return appinfo
    }

    const rawContent = fs.readFileSync(outputPath, 'utf-8')

    // 清理临时文件
    try {
      fs.unlinkSync(outputPath)
    }
    catch {
      // 忽略删除失败
    }

    // 在内容前后添加 "app" 包裹结构
    const wrappedContent = `"app"\n{\n${rawContent}\n}`

    // 解析 VDF 格式
    const parsed = parse(wrappedContent).app

    // 遍历所有 appid，提取并转换为 AppinfoVdf 格式
    if (parsed && typeof parsed === 'object') {
      Object.keys(parsed).forEach((appid) => {
        const appData = parsed[appid]
        if (appData && typeof appData === 'object') {
          const common = appData.common || {}
          const extended = appData.extended || {}

          appinfo[appid.substring(4)] = {
            appid: Number(appid.substring(4)),
            name: common.name ? String(common.name) : undefined,
            logo: common.logo ? String(common.logo) : undefined,
            logo_small: common.logo_small ? String(common.logo_small) : undefined,
            icon: common.icon ? String(common.icon) : undefined,
            oslist: common.oslist ? String(common.oslist).split(',').filter(Boolean) : undefined,
            type: common.type ? String(common.type) : undefined,
            name_localized: common.name_localized ? common.name_localized : undefined,
            developer: extended.developer ? String(extended.developer) : undefined,
            publisher: extended.publisher ? String(extended.publisher) : undefined,
            category: common.category
              ? Object.keys(common.category).filter(key => common.category[key]).map(key => key.substring(9))
              : undefined,
            steam_release_date: common.steam_release_date ? Number(common.steam_release_date) : undefined,
            store_tags: common.store_tags
              ? Object.values(common.store_tags)
              : undefined,
            review_score: common.review_score ? Number(common.review_score) : undefined,
            review_percentage: common.review_percentage ? Number(common.review_percentage) : undefined,
            isfreeapp: extended.isfreeapp ? Number(extended.isfreeapp) : undefined,
          }
        }
      })
    }
  }
  catch (error) {
    console.error('[readAppinfoVdf] 解析失败:', error)
  }

  return appinfo
}

/**
 * 读取 {SteamPath}\userdata\{AccountID}\config\localconfig.vdf 文件
 */
export async function readLocalconfigVdf(steamPath: string, accountID: string): Promise<LocalconfigVdf> {
  let localconfig: LocalconfigVdf = {} as LocalconfigVdf

  const localconfigPath = path.join(steamPath, 'userdata', accountID, 'config', 'localconfig.vdf')

  if (!fs.existsSync(localconfigPath)) {
    return localconfig
  }

  const content = fs.readFileSync(localconfigPath, 'utf8')

  const data = parse(content)
  const localConfigData = data.UserLocalConfigStore

  localconfig = {
    friends: Object.entries(localConfigData.friends).reduce((acc, [key, value]) => {
      const friends = value as { name: string, avatar?: string, NameHistory: Array<string> }
      if (friends !== undefined && friends.name !== undefined && friends.NameHistory !== undefined) {
        acc[key] = {
          name: friends.name,
          avatar: friends.avatar,
          NameHistory: Object.values(friends.NameHistory),
        }
      }
      return acc
    }, {} as Record<string, { name: string, avatar?: string, NameHistory: Array<string> }>),
    apps: Object.entries(localConfigData.Software.Valve.Steam.apps).reduce((acc, [key, value]) => {
      const apps = value as { LastPlayed?: number, Playtime?: number, Playtime2wks?: number, LaunchOptions?: string, autocloud?: { lastlaunch?: number, lastexit?: number } }
      if (apps !== undefined && (apps.LastPlayed !== undefined || apps.Playtime !== undefined || apps.Playtime2wks !== undefined || apps.LaunchOptions !== undefined || apps.autocloud !== undefined)) {
        acc[key] = {
          LastPlayed: apps.LastPlayed !== undefined ? Number(apps.LastPlayed) : undefined,
          Playtime: apps.Playtime !== undefined ? Number(apps.Playtime) : undefined,
          Playtime2wks: apps.Playtime2wks !== undefined ? Number(apps.Playtime2wks) : undefined,
          LaunchOptions: apps.LaunchOptions !== undefined ? String(apps.LaunchOptions) : undefined,
          autocloud: apps.autocloud !== undefined
            ? {
                lastlaunch: apps.autocloud.lastlaunch !== undefined ? Number(apps.autocloud.lastlaunch) : undefined,
                lastexit: apps.autocloud.lastexit !== undefined ? Number(apps.autocloud.lastexit) : undefined,
              }
            : undefined,
        }
      }
      return acc
    }, {} as Record<string, { LastPlayed?: number, Playtime?: number, Playtime2wks?: number, LaunchOptions?: string, autocloud?: { lastlaunch?: number, lastexit?: number } }>),
  }

  return localconfig
}

// Test
// (async () => console.warn(await readLoginusersVdf('D:\\Program Files (x86)\\Steam')))()
// (async () => console.warn(await readLibraryfoldersVdf('D:\\Program Files (x86)\\Steam')))()
// (async () => console.dir(await readAppmanifestAcf(['D:\\Program Files (x86)\\Steam', 'E:\\SteamLibrary']), { depth: null, maxArrayLength: null, colors: true }))()
// (async () => console.dir((await readAppinfoVdf('D:\\Program Files (x86)\\Steam'))['730'], { depth: null, maxArrayLength: null, colors: true }))()
// (async () => console.dir((await readLocalconfigVdf('D:\\Program Files (x86)\\Steam', '968168182')), { depth: null, maxArrayLength: null, colors: true }))()
