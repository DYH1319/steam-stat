export interface LoginusersVdf {
  /**
   * Steam ID
   */
  SteamID: bigint
  /**
   * 账号名
   */
  AccountName: string
  /**
   * 昵称
   */
  PersonaName: string
  /**
   * 是否记住密码
   */
  RememberPassword: number
  /**
   * 是否离线模式
   */
  WantsOfflineMode: number
  /**
   * 跳过离线模式警告（暂无实际性作用）
   */
  SkipOfflineModeWarning: number
  /**
   * 是否自动登录
   */
  AllowAutoLogin: number
  /**
   * 是否最近登录
   */
  MostRecent: number
  /**
   * 最近登录时间
   */
  Timestamp: number
}

export interface LibraryfoldersVdf {
  /**
   * 库文件夹路径
   */
  path: string
  label: string
  contentid: bigint
  /**
   * 库总占用空间大小
   */
  totalsize: bigint
  update_clean_bytes_tally: bigint
  time_last_update_verified: number
  /**
   * 应用 ID -> 应用占用空间大小（不包括创意工坊）
   */
  apps: Record<string, bigint>
}

export interface AppmanifestAcf {
  /**
   * 应用 ID
   */
  appid: number
  universe: number
  /**
   * 启动器路径（Steam 可执行文件路径）
   */
  LauncherPath: string
  /**
   * 应用名称（英文名称）
   */
  name: string
  StateFlags: number
  /**
   * 应用安装目录（相对于 {SteamLibraryPath\steamapps\common} 的路径）
   */
  installdir: string
  LastUpdated: number
  /**
   * 应用最后游玩 Unix 时间戳
   */
  LastPlayed: number
  /**
   * 应用占用磁盘空间大小（不包含创意工坊文件占用空间）
   */
  SizeOnDisk: bigint
  StagingSize: bigint
  buildid: number
  LastOwner: bigint
  DownloadType: number
  UpdateResult: number
  BytesToDownload: bigint
  BytesDownloaded: bigint
  BytesToStage: bigint
  BytesStaged: bigint
  TargetBuildID: number
  AutoUpdateBehavior: number
  /**
   * 是否允许运行此应用时进行下载
   */
  AllowOtherDownloadsWhileRunning: number
  ScheduledAutoUpdate: number
  InstalledDepots: Record<string, { manifest: bigint, size: bigint }>
  SharedDepots: Record<string, string>
  /**
   * 用户配置
   * 例如：
   * {
   *   language: 'schinese',
   *   DisabledDLC: '2279721,2279720',
   *   optionaldlc: ''
   * }
   */
  UserConfig: Record<string, string>
  MountedConfig: Record<string, string>
}

export interface AppinfoVdf {
  /**
   * 应用 ID
   */
  appid: number
  /**
   * 应用名称（英文名称）
   */
  name?: string
  /**
   * 应用 logo 文件名
   */
  logo?: string
  /**
   * 应用小 logo 文件名
   */
  logo_small?: string
  /**
   * 应用 icon 文件名
   */
  icon?: string
  /**
   * 支持的操作系统列表
   */
  oslist?: Array<string>
  /**
   * 应用类型
   */
  type?: string
  /**
   * 应用名称（本地化名称）
   */
  name_localized?: Record<string, string>
  /**
   * 开发商
   */
  developer?: string
  /**
   * 发行商
   */
  publisher?: string
  /**
   * 应用分类 ID 列表
   */
  category?: Array<string>
  /**
   * 在 Steam 上的发布日期
   */
  steam_release_date?: number
  /**
   * 应用标签 ID 列表
   */
  store_tags?: Array<string>
  /**
   * 应用评分 ??
   */
  review_score?: number
  /**
   * 应用评分百分比 ??
   */
  review_percentage?: number
  /**
   * 是否是免费应用
   */
  isfreeapp?: number
}

export interface LocalconfigVdf {
  /**
   * 好友信息（包括自己的）
   */
  friends: Record<string, { name: string, avatar?: string, NameHistory: Array<string> }>
  apps: Record<string, { LastPlayed?: number, Playtime?: number, Playtime2wks?: number, LaunchOptions?: string, autocloud?: { lastlaunch?: number, lastexit?: number } }>
}
