import type { RouteRecordRaw } from 'vue-router'
import i18n from '@/i18n'

const t = i18n.global.t

function Layout() {
  return import('@/layouts/index.vue')
}

const routes: RouteRecordRaw = {
  path: '/steam',
  component: Layout,
  meta: {
    title: () => t('menu.steamData'),
    icon: 'i-mdi:local',
    defaultOpened: true,
  },
  children: [
    {
      path: '/status',
      name: 'steamStatus',
      component: () => import('@/views/steam/status.vue'),
      meta: {
        title: () => t('menu.steamStatus'),
        icon: 'i-tabler:brand-steam',
      },
    },
    {
      path: '/user',
      name: 'steamUser',
      component: () => import('@/views/steam/user.vue'),
      meta: {
        title: () => t('menu.steamUser'),
        icon: 'i-mdi:user-group',
      },
    },
    {
      path: '/app',
      name: 'steamApp',
      component: () => import('@/views/steam/app.vue'),
      meta: {
        title: () => t('menu.steamApp'),
        icon: 'i-iconamoon:apps',
      },
    },
    {
      path: '/useRecord',
      name: 'steamUseRecord',
      component: () => import('@/views/steam/useRecord.vue'),
      meta: {
        title: () => t('menu.steamUsage'),
        icon: 'i-uil:statistics',
      },
    },
    // 以下模块依赖 Steam 登录会话，尚未稳定，默认隐藏。
    // 需在「设置 → 实验性功能」中开启后才会注册路由与菜单。
    {
      path: '/steamLogin',
      name: 'steamLogin',
      component: () => import('@/views/steam/login.vue'),
      meta: {
        title: () => t('menu.steamLogin'),
        icon: 'i-mdi:login',
        experimental: true,
      },
    },
    {
      path: '/friends',
      name: 'steamFriends',
      component: () => import('@/views/steam/friends.vue'),
      meta: {
        title: () => t('menu.steamFriends'),
        icon: 'i-mdi:account-group',
        experimental: true,
      },
    },
    {
      path: '/library',
      name: 'steamLibrary',
      component: () => import('@/views/steam/library.vue'),
      meta: {
        title: () => t('menu.steamLibrary'),
        icon: 'i-mdi:library',
        experimental: true,
      },
    },
  ],
}

export default routes
