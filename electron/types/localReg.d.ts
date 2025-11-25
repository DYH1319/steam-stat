export interface SteamReg {
  AlreadyRetriedOfflineMode: number
  AutologinUser: string
  AutologinUser_steamchina: string
  /**
   * 使用的语言
   */
  Language: string
  LastGameNameUsed: string
  PseudoUUID: string
  Rate: string
  RememberPassword: number
  Restart: number
  /**
   * 对好友显示的运行的单个 App 的 Id，没有运行任何 App 时会置 0
   */
  RunningAppID: number
  Skin?: string
  SourceModInstallPath: string
  StartupModeTmp: number
  StartupModeTmplsValid: number
  /**
   * Steam 可执行文件路径
   */
  SteamExe: string
  /**
   * Steam 安装路径
   */
  SteamPath: string
  SuppressAutoRun: number
}

export interface SteamActiveProcessReg {
  /**
   * 当前登录用户的 AccountID，没有登录用户时会置 0
   */
  ActiveUser: number
  /**
   * Steam 进程 ID，Steam 未运行时会置 0
   */
  pid: number
  /**
   * steamclient.dll 文件路径
   */
  SteamClientDll: string
  /**
   * steamclient64.dll 文件路径
   */
  SteamClientDll64: string
  Universe: string
}
