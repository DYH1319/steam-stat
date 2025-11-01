import type { RouteRecordRaw } from 'vue-router'

function Layout() {
  return import('@/layouts/index.vue')
}

const routes: RouteRecordRaw = {
  path: '/process-monitor',
  component: Layout,
  meta: {
    title: '进程监控',
    icon: 'i-material-symbols:monitor-heart-outline',
  },
  children: [
    {
      path: '',
      name: 'processMonitorIndex',
      component: () => import('@/views/process-monitor/page.vue'),
      meta: {
        title: '进程监控',
        icon: 'i-material-symbols:monitor-heart-outline',
        menu: false,
        breadcrumb: false,
      },
    },
  ],
}

export default routes
