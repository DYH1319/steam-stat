import { sql } from 'drizzle-orm'
import { blob, check, index, integer, sqliteTable, text } from 'drizzle-orm/sqlite-core'

// 全局状态表（单行数据表）
export const globalStatus = sqliteTable('global_status', {
  // ID
  id: integer('id').notNull().unique().primaryKey().default(1),
  // Steam 安装路径
  steamPath: text('steam_path'),
  // Steam 可执行文件路径
  steamExePath: text('steam_exe_path'),
  // Steam 进程 PID
  steamPid: integer('steam_pid'),
  // steamclient.dll 文件路径
  steamClientDllPath: text('steam_client_dll_path'),
  // steamclient64.dll 文件路径
  steamClientDll64Path: text('steam_client_dll_64_path'),
  // 当前登录用户的 Steam ID
  activeUserSteamId: blob('active_user_steam_id', { mode: 'bigint' }),
  // Steam 对外显示的当前运行的 App ID（同时只有一个）
  runningAppId: integer('running_app_id'),
  // 刷新时间
  refreshTime: integer('refresh_time', { mode: 'timestamp' }).notNull(),
  // steamUser 表刷新时间
  steamUserRefreshTime: integer('steam_user_refresh_time', { mode: 'timestamp' }),
}, table => [
  // ID 约束
  check('global_status_check_id', sql`${table.id} == 1`),
])

// Steam 用户表
export const steamUser = sqliteTable('steam_user', {
  // ID
  id: integer('id').notNull().unique().primaryKey({ autoIncrement: true }),
  // Steam ID
  steamId: blob('steam_id', { mode: 'bigint' }).notNull().unique(),
  // Account ID
  accountId: integer('account_id').notNull().unique(),
  // 账号名
  accountName: text('account_name').notNull(),
  // 昵称
  personaName: text('persona_name'),
  // 是否记住密码
  rememberPassword: integer('remember_password', { mode: 'boolean' }),
  // 是否开启离线模式
  wantsOfflineMode: integer('wants_offline_mode', { mode: 'boolean' }),
  // 是否跳过离线模式警告
  skipOfflineModeWarning: integer('skip_offline_mode_warning', { mode: 'boolean' }),
  // 是否允许自动登录
  allowAutoLogin: integer('allow_auto_login', { mode: 'boolean' }),
  // 是否最近登录
  mostRecent: integer('most_recent', { mode: 'boolean' }),
  // 最近登录时间
  timestamp: integer('timestamp', { mode: 'timestamp' }),
  // 全尺寸头像（184x184）Base64
  avatarFull: text('avatar_full'),
  // 中等尺寸头像（64x64）Base64
  avatarMedium: text('avatar_medium'),
  // 小尺寸头像（32x32）Base64
  avatarSmall: text('avatar_small'),
  // 动画头像 Base64
  animatedAvatar: text('animated_avatar'),
  // 头像边框 Base64
  avatarFrame: text('avatar_frame'),
  // 等级
  level: integer('level'),
  // 等级样式类
  levelClass: text('level_class'),
})

// Steam 应用表
export const steamApp = sqliteTable('steam_app', {
  // ID
  id: integer('id').notNull().unique().primaryKey({ autoIncrement: true }),
  // App ID
  appId: integer('app_id').notNull().unique(),
  // 应用名称
  name: text('name'),
  // 应用本地化名称 JSON 对象
  nameLocalized: text('name_localized', { mode: 'json' }).notNull().default('{}'),
  // 是否已本地安装
  installed: integer('installed', { mode: 'boolean' }).notNull(),
  // 本地安装目录名称
  installDir: text('install_dir'),
  // 本地安装目录绝对路径
  installDirPath: text('install_dir_path'),
  // 应用文件占用大小
  appOnDisk: blob('app_on_disk', { mode: 'bigint' }),
  // 应用文件真实占用大小
  appOnDiskReal: blob('app_on_disk_real', { mode: 'bigint' }),
  // 是否正在运行
  isRunning: integer('is_running', { mode: 'boolean' }).notNull().default(false),
  // 应用类型
  type: text('type'),
  // 开发者
  developer: text('developer'),
  // 发布者
  publisher: text('publisher'),
  // 发布日期
  steamReleaseDate: integer('steam_release_date', { mode: 'timestamp' }),
  // 是否是免费应用
  isFreeApp: integer('is_free_app', { mode: 'boolean' }),
  // 刷新时间
  refreshTime: integer('refresh_time', { mode: 'timestamp' }).notNull(),
}, table => [
  // 普通索引
  index('steam_app_name_idx').on(table.name),
  index('steam_app_installed_idx').on(table.installed),
])

// 应用使用记录表
export const useAppRecord = sqliteTable('use_app_record', {
  // ID
  id: integer('id').notNull().unique().primaryKey({ autoIncrement: true }),
  // 使用的 App ID
  appId: integer('app_id').notNull(),
  // 使用 App 的 Steam ID
  steamId: blob('steam_id', { mode: 'bigint' }).notNull(),
  // 开始使用的 Unix 时间戳
  startTime: integer('start_time', { mode: 'timestamp' }).notNull(),
  // 结束使用的 Unix 时间戳
  endTime: integer('end_time', { mode: 'timestamp' }),
  // 持续使用时间
  duration: integer('duration'),
}, table => [
  // 外键索引
  index('use_app_record_app_id_idx').on(table.appId),
  index('use_app_record_steam_id_idx').on(table.steamId),
  // 时间索引
  index('use_app_record_start_time_idx').on(table.startTime),
  // 复合索引
  index('use_app_record_steam_id_app_id_idx').on(table.steamId, table.appId),
])

// 类型推导
export type GlobalStatus = typeof globalStatus.$inferSelect
export type NewGlobalStatus = typeof globalStatus.$inferInsert
export type SteamUser = typeof steamUser.$inferSelect
export type NewSteamUser = typeof steamUser.$inferInsert
export type SteamApp = typeof steamApp.$inferSelect
export type NewSteamApp = typeof steamApp.$inferInsert
export type UseAppRecord = typeof useAppRecord.$inferSelect
export type NewUseAppRecord = typeof useAppRecord.$inferInsert
