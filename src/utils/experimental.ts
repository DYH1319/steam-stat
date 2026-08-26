import type { Route } from '#/global'

/**
 * 实验性功能开关。
 *
 * 尚未稳定的模块（Steam 登录 / 好友 / 游戏库）通过路由 `meta.experimental = true` 标记，
 * 开关关闭时这些路由不会被注册，因此菜单、导航搜索、标签栏都不会出现它们。
 *
 * 该值由 `main.ts` 在 `app.use(router)` 之前设置——vue-router 会在安装时执行首次导航，
 * 而动态路由正是在守卫里生成的，所以必须早于安装赋值。
 */
const enabled = ref(false)

export function isExperimentalEnabled() {
  return enabled.value
}

export function setExperimentalEnabled(value: boolean) {
  enabled.value = value
}

function isExperimentalRoute(route: { meta?: Record<string, any> }) {
  return route.meta?.experimental === true
}

function filterChildren(routes: any[]): any[] {
  return routes
    .filter(route => !isExperimentalRoute(route))
    .map(route => (route.children?.length ? { ...route, children: filterChildren(route.children) } : route))
}

/**
 * 递归剔除被标记为实验性的路由。开关开启时原样返回。
 */
export function filterExperimentalRoutes(routes: Route.recordMainRaw[]): Route.recordMainRaw[] {
  if (enabled.value) {
    return routes
  }
  return routes.map(main => ({
    ...main,
    children: filterChildren(main.children ?? []),
  }))
}

/**
 * 收集所有实验性路由的 path，供「主页设置指向了已隐藏页面」这类兜底判断使用。
 */
export function collectExperimentalPaths(routes: Route.recordMainRaw[]): string[] {
  const paths: string[] = []
  function walk(items: any[]) {
    items.forEach((item) => {
      if (isExperimentalRoute(item) && typeof item.path === 'string') {
        paths.push(item.path)
      }
      if (item.children?.length) {
        walk(item.children)
      }
    })
  }
  routes.forEach(main => walk(main.children ?? []))
  return paths
}
