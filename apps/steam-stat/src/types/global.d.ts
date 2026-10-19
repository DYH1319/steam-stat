import type { DataSourceId } from '@/types/dataSource'
import 'vue-router'

declare module 'vue-router' {
  interface RouteMeta {
    /**
     * 标记为实验性功能。未在「设置 → 实验性功能」中开启时，该路由不会被注册。
     * 见 src/utils/experimental.ts
     */
    experimental?: boolean
    /**
     * 页面依赖的数据源。数据源缺失 / 解析失败时路由守卫会重定向到对应的数据源页，
     * 菜单项会置灰提示。见 src/store/modules/dataSource.ts
     */
    dataSources?: DataSourceId[]
  }
}

export {}
