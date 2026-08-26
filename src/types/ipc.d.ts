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
  steamGetStatus: () => Promise<GlobalStatus | undefined>
  steamRefreshStatus: () => Promise<GlobalStatus | undefined>
  steamGetLibraryFolders: () => Promise<string[]>

  steamGetLoginUser: () => Promise<SteamUser[]>
  steamRefreshLoginUser: () => Promise<SteamUser[]>
  steamChangeLoginUser: (param: ChangeSteamUserDto) => Promise<boolean>
  steamUserUpdatedOnListener: (callback: () => void) => void
  steamUserUpdatedRemoveListener: () => void

  // Steam Login API
  steamLoginCredentialsStart: (param: { username: string, password: string, rememberMe: boolean }) => Promise<SteamLoginResult>
  steamLoginQrStart: (param: { rememberMe: boolean }) => Promise<SteamLoginResult>
  steamLoginTokenStart: (param: { tokenId: number }) => Promise<SteamLoginResult>
  steamLoginGuardCodeSubmit: (param: { code: string }) => Promise<boolean>
  steamLoginSwitchToUseCode: () => void
  steamLoginConfirmDevice: () => void
  steamLoginCancel: () => void
  steamLoginLoggedInUsersGet: () => Promise<string[]>
  steamLoginUserLogout: (param: { accountName: string }) => Promise<boolean>
  steamLoginSavedTokensGet: () => Promise<SteamLoginToken[]>
  steamLoginSavedTokenDelete: (param: { id: number }) => Promise<boolean>
  steamLoginUserSetPersonaState: (param: { accountName: string, personaState: number }) => Promise<boolean>
  steamLoginEventOnListener: (callback: (data: SteamLoginEvent) => void) => void
  steamLoginEventRemoveListener: () => void

  // Steam Friends API
  steamFriendsGetAll: () => Promise<SteamFriendData[]>
  steamFriendsGetForUser: (param: { accountName: string }) => Promise<SteamFriendData | null>
  steamFriendsGetCached: () => Promise<SteamFriendData[]>
  steamFriendsRequestFriendInfo: (param: { accountName: string, friendSteamId: string }) => void
  steamFriendsUpdateOnListener: (callback: (data: SteamFriendsUpdateEvent) => void) => void
  steamFriendsUpdateRemoveListener: () => void

  // Steam Friends Status Record API
  steamFriendsTrackStart: (param: { accountName: string, friendSteamIds: string[] }) => Promise<boolean>
  steamFriendsTrackStop: (param: { accountName: string, friendSteamIds: string[] }) => Promise<boolean>
  steamFriendsTrackGet: (param: { accountName: string }) => Promise<string[]>
  steamFriendsTrackGetAll: () => Promise<Record<string, string[]>>
  steamFriendsRecordsGet: (param?: { accountName?: string, friendSteamId?: string, changeType?: string, startTime?: number, endTime?: number, limit?: number }) => Promise<FriendStatusRecord[]>
  steamFriendsRecordsClear: (param?: { accountName?: string, friendSteamId?: string }) => Promise<number>

  // Steam Library API
  steamLibraryGetForUser: (param: { accountName: string }) => Promise<SteamOwnedGame[]>
  steamLibraryGetForAllUsers: () => Promise<Record<string, SteamOwnedGame[]>>
  steamLibrarySyncForUser: (param: { accountName: string }) => Promise<boolean>
  steamLibrarySyncForAllUsers: () => Promise<Record<string, boolean>>

  steamGetRunningApps: () => Promise<{ apps: SteamApp[], lastUpdateTime: number }>
  steamGetAppsInfo: (param?: { sortField?: string, sortOrder?: 'asc' | 'desc', filterInstalled?: boolean }) => Promise<SteamApp[]>
  steamRefreshAppsInfo: (param?: { sortField?: string, sortOrder?: 'asc' | 'desc', filterInstalled?: boolean }) => Promise<SteamApp[]>

  steamGetValidUseAppRecord: (param?: { steamIds?: string[], startDate?: number, endDate?: number }) => Promise<{ records: UseAppRecord[], lastUpdateTime: number }>
  steamGetUsersInRecord: () => Promise<SteamUser[]>
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
  updaterEventRemoveListener: () => void

  // App & Window API
  appQuit: () => void
  windowMinimizeToTray: () => void
  windowMinimize: () => void
  windowMaximize: () => Promise<boolean>
  windowClose: () => void
  windowIsMaximized: () => Promise<boolean>

  // Shell API
  shellOpenExternal: (url: string) => void
  shellOpenPath: (path: string) => void
}

interface GlobalStatus {
  id: number
  steamPath?: string
  steamExePath?: string
  steamPid?: number
  steamClientDllPath?: string
  steamClientDll64Path?: string
  activeUserSteamId?: string
  runningAppId?: number
  refreshTime: number
  steamUserRefreshTime?: number
  steamAppRefreshTime?: number
}

interface SteamUser {
  id: number
  steamId: string
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
}

interface UseAppRecord {
  appId: number
  steamId: string
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
  homePage: '/status' | '/user' | '/app' | '/useRecord'
  colorScheme: 'light' | 'dark' | 'system'
  themeColor: string
  radius: number
  zoomFactor: number
  /**
   * 是否启用实验性功能（Steam 登录 / 好友 / 游戏库等尚未稳定的模块）
   */
  experimentalFeatures: boolean
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

interface ChangeSteamUserDto extends SteamUser {
  offlineMode?: boolean
  personaState?: number
}

interface SteamLoginResult {
  success: boolean
  accountName?: string
  error?: string
  /**
   * 错误码（EResult 名称或后端自定义错误码），用于前端本地化错误提示
   */
  errorCode?: string
}

interface SteamLoginToken {
  id: number
  accountName: string
  createdAt: number
  /**
   * Refresh Token 过期时间（Unix 秒），解析失败时为 null
   */
  expiresAt?: number | null
}

interface SteamLoginEvent {
  /**
   * `reconnectFailed`：自动重连已放弃（凭证失效、账号异常或重试次数耗尽），需要用户手动重新登录
   */
  type: 'connecting' | 'authenticating' | 'guardCodeNeeded' | 'deviceConfirmationNeeded' | 'qrCode' | 'success' | 'error' | 'cancelled' | 'userDisconnected' | 'userReconnected' | 'reconnectFailed'
  data?: {
    guardType?: 'device' | 'email'
    email?: string
    previousCodeWasIncorrect?: boolean
    qrImageBase64?: string
    challengeUrl?: string
    accountName?: string
    message?: string
    /**
     * 错误码（EResult 名称或后端自定义错误码），用于前端本地化错误提示
     */
    errorCode?: string
  }
}

interface SteamFriendData {
  accountName: string
  currentUser: SteamFriendInfo
  friends: SteamFriendInfo[]
  lastUpdateTime: number
}

interface SteamFriendInfo {
  steamId: string
  personaName: string
  personaState: number
  personaStateFlags: number
  relationship: number
  gameName: string
  gameId: string
  avatarHash: string
  lastLogOff: number
  lastLogOn: number
  richPresence: string
  /**
   * Steam 等级（null / undefined 表示尚未获取到）
   */
  level?: number | null
}

interface SteamFriendsUpdateEvent {
  accountName: string
  data: SteamFriendData
}

interface FriendStatusRecord {
  id: number
  accountName: string
  friendSteamId: string
  friendPersonaName: string
  /**
   * 变化类型：state（在线状态）/ game（游戏）/ personaName（昵称）
   */
  changeType: 'state' | 'game' | 'personaName' | string
  /**
   * 变化前的值（JSON 字符串）
   */
  previousValue?: string
  /**
   * 变化后的值（JSON 字符串）
   */
  currentValue?: string
  timestamp: number
}

interface SteamOwnedGame {
  appId: number
  /**
   * 英文名称
   */
  name: string
  /**
   * 本地化名称（无本地化时与 name 相同）
   */
  nameLocalized: string
  /**
   * 总游玩时长（分钟）
   */
  playtimeForever: number
  /**
   * 最近两周游玩时长（分钟）
   */
  playtime2Weeks: number
  /**
   * 最后游玩时间（Unix 秒）
   */
  rtimeLastPlayed: number
  imgIconUrl: string
  hasCommunityVisibleStats: boolean
  contentDescriptorIds: number[]
  /**
   * 是否被本账号直接拥有（在本账号的库中）
   */
  isOwned: boolean
  /**
   * 是否来自 Steam 家庭共享库
   */
  isFamilyShared: boolean
  /**
   * 是否在本账号的愿望单中
   */
  isInWishlist: boolean
  /**
   * 家庭中拥有此游戏的成员 SteamID
   */
  ownerSteamIds: string[]
  /**
   * 家庭中拥有此游戏的成员昵称（与 ownerSteamIds 一一对应）
   */
  ownerNames: string[]
  /**
   * 成就总数（0 表示无成就或未获取到）
   */
  achievementTotal: number
  /**
   * 已解锁成就数
   */
  achievementUnlocked: number
  /**
   * 成就完成百分比（0-100）
   */
  achievementPercentage: number
}
