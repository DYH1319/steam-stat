import type { RouteRecordRaw } from 'vue-router'
import i18n from '@/i18n'

const t = i18n.global.t

function Layout() {
  return import('@/layouts/index.vue')
}

const routes: RouteRecordRaw = {
  path: '/about',
  component: Layout,
  meta: {
    title: () => t('menu.about'),
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
