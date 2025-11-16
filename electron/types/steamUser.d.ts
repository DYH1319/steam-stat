export interface SteamLoginUserInfo {
  AccountID: number
  SteamID: bigint
  Steam2ID: string
  Steam3ID: string
  Steam64ID: string
  AccountName: string
  PersonaName: string
  RememberPassword: number
  WantsOfflineMode: number
  SkipOfflineModeWarning: number
  AllowAutoLogin: number
  MostRecent: number
  Timestamp: number
}

export interface SteamLoginUsersInfo {
  RunningAppID?: number
  AutoLoginUser?: string
  LastGameNameUsed?: string
  RememberPassword?: number | string
  ActiveUser?: number
  ActiveUserSteamID?: bigint
  ActiveUserSteam2ID?: string
  ActiveUserSteam3ID?: string
  ActiveUserSteam64ID?: string
  loginusers?: Record<string, SteamLoginUserInfo>
}
