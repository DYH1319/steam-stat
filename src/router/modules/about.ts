import type { RouteRecordRaw } from 'vue-router'

function Layout() {
  return import('@/layouts/index.vue')
}

const routes: RouteRecordRaw = {
  path: '/about',
  component: Layout,
  meta: {
    title: '关于',
    icon: 'i-mdi:information-outline',
  },
  children: [
    {
      path: '',
      name: 'aboutIndex',
      component: () => import('@/views/about/index.vue'),
      meta: {
        menu: false,
        breadcrumb: false,
      },
    },
  ],
}

export default routes
