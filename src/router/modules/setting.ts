import type { RouteRecordRaw } from 'vue-router'
import i18n from '@/i18n'
import Setting from '@/views/setting/index.vue'

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
      component: Setting,
      meta: {
        menu: false,
        breadcrumb: false,
      },
    },
  ],
}

export default routes
