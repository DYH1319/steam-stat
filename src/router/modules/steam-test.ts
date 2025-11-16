import type { RouteRecordRaw } from 'vue-router'

function Layout() {
  return import('@/layouts/index.vue')
}

const routes: RouteRecordRaw = {
  path: '/steam-test',
  component: Layout,
  meta: {
    title: 'Steam 测试',
    icon: 'i-mdi:flask-outline',
  },
  children: [
    {
      path: '',
      name: 'steamTestIndex',
      component: () => import('@/views/steam-test/page.vue'),
      meta: {
        title: 'Steam 测试',
        icon: 'i-mdi:flask-outline',
      },
    },
    {
      path: 'new',
      name: 'steamTestNew',
      component: () => import('@/views/steam-test/page-new.vue'),
      meta: {
        title: 'Steam 测试（新）',
        icon: 'i-mdi:flask-outline',
      },
    },
    {
      path: 'loginTest',
      name: 'steamTestLoginTest',
      component: () => import('@/views/steam-test/loginTest.vue'),
      meta: {
        title: 'Steam 登录测试',
        icon: 'i-mdi:login-variant',
      },
    },
  ],
}

export default routes
