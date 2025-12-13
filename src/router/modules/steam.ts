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
    // {
    //   path: '/basicTest',
    //   name: 'steamBasicTest',
    //   component: () => import('@/views/steam/test/basicTest.vue'),
    //   meta: {
    //     title: 'Steam 基本测试',
    //     icon: 'i-mdi:flask-outline',
    //   },
    // },
    // {
    //   path: '/loginTest',
    //   name: 'steamLoginTest',
    //   component: () => import('@/views/steam/test/loginTest.vue'),
    //   meta: {
    //     title: 'Steam 登录测试',
    //     icon: 'i-mdi:login-variant',
    //   },
    // },
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
  ],
}

export default routes
