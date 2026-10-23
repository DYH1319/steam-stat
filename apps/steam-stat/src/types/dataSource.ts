/**
 * 数据源标识：9 个后端本地数据源 + 渲染层推导的 CM 会话。
 * 用于路由 meta.dataSources 声明页面依赖。
 */
export type DataSourceId = SteamLocalDataSource['id'] | 'cm.session'
