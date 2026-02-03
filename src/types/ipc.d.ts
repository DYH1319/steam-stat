/**
 * @Author: DYH1319
 * @Date: 2026-01-29 22:22:57
 * @LastEditors: DYH1319
 * @LastEditTime: 2026-01-29 22:25:19
 * @FilePath: src/types/ipc.d.ts
 */
interface Window {
  electron: ElectronAPI
}

interface ElectronAPI {
  // Steam API
  steamGetLoginUser: () => Promise<SteamUser[]>
  steamGetValidUseAppRecord: (param?: { steamIds?: string[], startDate?: number, endDate?: number }) => Promise<{ records: UseAppRecord[], lastUpdateTime: number }>
  steamGetUsersInRecord: () => Promise<SteamUser[]>
  steamGetRunningApps: () => Promise<{ apps: SteamApp[], lastUpdateTime: number }>
  steamEndUseAppRecording: () => Promise<boolean>
  steamDiscardUseAppRecording: () => Promise<boolean>

  // Job API
  jobGetUpdateAppRunningStatusJobStatus: () => Promise<UpdateAppRunningStatusJobStatus>

  // Setting API
  settingGet: () => Promise<AppSettings>
  settingUpdate: (param: Partial<AppSettings>) => Promise<boolean>

  // Updater API
  updaterGetStatus: () => Promise<UpdaterStatus>
  updaterCheck: () => void
  updaterDownload: () => void
  updaterQuitAndInstall: () => void
  updaterEventOnListener: (callback: (data: { updaterEvent: string, data?: any }) => void) => void
  updateEventRemoveListener: () => void

  // App Window API
  appQuit: () => void
  windowMinimizeToTray: () => void
  windowMinimize: () => void
  windowMaximize: () => Promise<boolean>
  windowClose: () => void
  windowIsMaximized: () => Promise<boolean>
}

interface SteamUser {
  id: number
  // steamId: bigint
  steamIdStr: string
  accountId: number
  accountName: string
  personaName?: string
  rememberPassword?: boolean
  wantsOfflineMode?: boolean
  skipOfflineModeWarning?: boolean
  allowAutoLogin?: boolean
  mostRecent?: boolean
  timestamp?: number
  avatarFull?: string
  avatarMedium?: string
  avatarSmall?: string
  animatedAvatar?: string
  avatarFrame?: string
  level?: number
  levelClass?: string
}

interface SteamApp {
  id: number
  appId: number
  name?: string
  nameLocalized: string
  installed: boolean
  installDir?: string
  installDirPath?: string
  appOnDisk?: number
  appOnDiskReal?: number
  isRunning: boolean
  type?: string
  developer?: string
  publisher?: string
  steamReleaseDate?: number
  isFreeApp?: boolean
  refreshTime: number
}

interface UseAppRecord {
  appId: number
  // steamId: bigint
  steamIdStr: string
  startTime: number
  endTime: number
  duration: number
  // SteamApp
  appName?: string
  appNameLocalized?: string
  // SteamUser
  userPersonaName?: string
}

interface UpdateAppRunningStatusJobStatus {
  isRunning: boolean
  lastUpdateTime: number
  intervalTime: number
}

interface AppSettings {
  autoStart: boolean
  silentStart: boolean
  autoUpdate: boolean
  language: 'zh-CN' | 'en-US'
  closeAction: 'exit' | 'minimize' | 'ask'
  updateAppRunningStatusJob: {
    enabled: boolean
    intervalSeconds: number
  }
}

interface UpdaterStatus {
  autoUpdateEnabled: boolean
  isChecking: boolean
  isDownloading: boolean
  checkUpdateInterval: number
  currentVersion: string
}
