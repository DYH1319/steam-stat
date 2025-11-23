import type { RouteRecordRaw } from 'vue-router'

function Layout() {
  return import('@/layouts/index.vue')
}

const routes: RouteRecordRaw = {
  path: '/setting',
  component: Layout,
  meta: {
    title: '设置',
    icon: 'i-uil:setting',
  },
  children: [
    {
      path: '',
      name: 'settingIndex',
      component: () => import('@/views/setting/index.vue'),
      meta: {
        menu: false,
        breadcrumb: false,
      },
    },
  ],
}

export default routes
