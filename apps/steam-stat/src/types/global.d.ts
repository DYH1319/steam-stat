import 'vue-router'

declare module 'vue-router' {
  interface RouteMeta {
    /**
     * 标记为实验性功能。未在「设置 → 实验性功能」中开启时，该路由不会被注册。
     * 见 src/utils/experimental.ts
     */
    experimental?: boolean
  }
}

export {}
