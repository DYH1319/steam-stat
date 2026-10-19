import type { RouteRecordRaw } from 'vue-router'
import i18n from '@/i18n'

const t = i18n.global.t

function Layout() {
  return import('@/layouts/index.vue')
}

// Steam 数据页面：均为顶级单页菜单项，keepAlive 在切换页签时保留页面状态。
// meta.dataSources 声明页面依赖的数据源，缺失时路由守卫重定向、菜单置灰。
const routes: RouteRecordRaw[] = [
  {
    path: '/user',
    component: Layout,
    meta: {
      title: () => t('menu.steamUser'),
      icon: 'i-mdi:user-group',
      dataSources: ['file.loginUsersVdf'],
    },
    children: [
      {
        path: '',
        name: 'steamUser',
        component: () => import('@/views/steam/user.vue'),
        meta: {
          menu: false,
          breadcrumb: false,
          keepAlive: true,
        },
      },
    ],
  },
  {
    path: '/app',
    component: Layout,
    meta: {
      title: () => t('menu.steamApp'),
      icon: 'i-iconamoon:apps',
      dataSources: ['file.libraryFoldersVdf', 'file.appManifests'],
    },
    children: [
      {
        path: '',
        name: 'steamApp',
        component: () => import('@/views/steam/app.vue'),
        meta: {
          menu: false,
          breadcrumb: false,
          keepAlive: true,
        },
      },
    ],
  },
  {
    path: '/useRecord',
    component: Layout,
    meta: {
      title: () => t('menu.steamUsage'),
      icon: 'i-uil:statistics',
      dataSources: ['job.appRunningStatus'],
    },
    children: [
      {
        path: '',
        name: 'steamUseRecord',
        component: () => import('@/views/steam/useRecord.vue'),
        meta: {
          menu: false,
          breadcrumb: false,
          keepAlive: true,
        },
      },
    ],
  },
  // 以下模块依赖 Steam 登录会话，尚未稳定，默认隐藏。
  // 需在「设置 → 实验性功能」中开启后才会注册路由与菜单。
  {
    path: '/friends',
    component: Layout,
    meta: {
      title: () => t('menu.steamFriends'),
      icon: 'i-mdi:account-group',
      experimental: true,
      dataSources: ['cm.session'],
    },
    children: [
      {
        path: '',
        name: 'steamFriends',
        component: () => import('@/views/steam/friends.vue'),
        meta: {
          menu: false,
          breadcrumb: false,
          keepAlive: true,
        },
      },
    ],
  },
  {
    path: '/library',
    component: Layout,
    meta: {
      title: () => t('menu.steamLibrary'),
      icon: 'i-mdi:library',
      experimental: true,
      dataSources: ['cm.session'],
    },
    children: [
      {
        path: '',
        name: 'steamLibrary',
        component: () => import('@/views/steam/library.vue'),
        meta: {
          menu: false,
          breadcrumb: false,
          keepAlive: true,
        },
      },
    ],
  },
  {
    path: '/achievements',
    component: Layout,
    meta: {
      title: () => t('menu.steamAchievements'),
      icon: 'i-mdi:trophy-variant',
      experimental: true,
      dataSources: ['cm.session'],
    },
    children: [
      {
        path: '',
        name: 'steamAchievements',
        component: () => import('@/views/steam/achievements.vue'),
        meta: {
          menu: false,
          breadcrumb: false,
          keepAlive: true,
        },
      },
    ],
  },
]

export default routes
