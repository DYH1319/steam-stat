import type { RouteRecordRaw } from 'vue-router'
import i18n from '@/i18n'

const t = i18n.global.t

function Layout() {
  return import('@/layouts/index.vue')
}

const routes: RouteRecordRaw = {
  path: '/setting',
  component: Layout,
  meta: {
    title: () => t('menu.settings'),
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
