import { blob, index, integer, sqliteTable, text } from 'drizzle-orm/sqlite-core'

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
}, table => [
  // 普通索引
  index('steam_user_account_name_idx').on(table.accountName),
])

// Steam 应用表
export const steamApp = sqliteTable('steam_app', {
  // ID
  id: integer('id').notNull().unique().primaryKey({ autoIncrement: true }),
  // App ID
  appId: integer('app_id').notNull().unique(),
  // 应用名称
  name: text('name'),
  // 应用本地化名称 JSON 数组
  nameLocalized: text('name_localized', { mode: 'json' }).notNull().default('[]'),
  // 是否已本地安装
  installed: integer('installed', { mode: 'boolean' }).notNull(),
  // 本地安装目录名称
  installDir: text('install_dir'),
  // 本地安装目录绝对路径
  installDirPath: text('install_dir_path'),
  // 应用文件占用大小
  appOnDisk: integer('app_on_disk'),
  // 应用文件真实占用大小
  appOnDiskReal: integer('app_on_disk_real'),
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
export type SteamUser = typeof steamUser.$inferSelect
export type NewSteamUser = typeof steamUser.$inferInsert
export type SteamApp = typeof steamApp.$inferSelect
export type NewSteamApp = typeof steamApp.$inferInsert
export type UseAppRecord = typeof useAppRecord.$inferSelect
export type NewUseAppRecord = typeof useAppRecord.$inferInsert
