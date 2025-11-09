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
        menu: false,
        breadcrumb: false,
      },
    },
  ],
}

export default routes

