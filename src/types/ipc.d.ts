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
  steamGetValidUseAppRecord: (param?: { steamIds?: string[], startDate?: number, endDate?: number }) => Promise<SteamGetValidUseAppRecordResponse>
  steamGetUsersInRecord: () => Promise<SteamUser[]>

  // Job API
  jobGetUpdateAppRunningStatusJobStatus: () => Promise<{ isRunning: boolean, lastUpdateTime: number, intervalTime: number }>

  // Setting API
  settingsGet: () => Promise<AppSettings>
}

interface SteamGetValidUseAppRecordResponse {
  records: UseAppRecord[]
  lastUpdateTime: number
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
