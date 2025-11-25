import type { RouteRecordRaw } from 'vue-router'

function Layout() {
  return import('@/layouts/index.vue')
}

const routes: RouteRecordRaw = {
  path: '/steam',
  component: Layout,
  meta: {
    title: 'Steam 本地数据',
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
        title: 'Steam 状态',
        icon: 'i-tabler:brand-steam',
      },
    },
    {
      path: '/user',
      name: 'steamUser',
      component: () => import('@/views/steam/user.vue'),
      meta: {
        title: 'Steam 用户信息',
        icon: 'i-mdi:user-group',
      },
    },
    {
      path: '/app',
      name: 'steamApp',
      component: () => import('@/views/steam/app.vue'),
      meta: {
        title: 'Steam 应用信息',
        icon: 'i-iconamoon:apps',
      },
    },
    {
      path: '/useRecord',
      name: 'steamUseRecord',
      component: () => import('@/views/steam/useRecord.vue'),
      meta: {
        title: 'Steam 使用统计',
        icon: 'i-uil:statistics',
      },
    },
  ],
}

export default routes
