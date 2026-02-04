import type { RouteRecordRaw } from 'vue-router'
import i18n from '@/i18n'
import App from '@/views/steam/app.vue'
import Status from '@/views/steam/status.vue'
import User from '@/views/steam/user.vue'
import UseRecord from '@/views/steam/useRecord.vue'

const t = i18n.global.t

function Layout() {
  return import('@/layouts/index.vue')
}

const routes: RouteRecordRaw = {
  path: '/steam',
  component: Layout,
  meta: {
    title: () => t('menu.steamData'),
    icon: 'i-mdi:local',
    defaultOpened: true,
  },
  children: [
    {
      path: '/status',
      name: 'steamStatus',
      component: Status,
      meta: {
        title: () => t('menu.steamStatus'),
        icon: 'i-tabler:brand-steam',
      },
    },
    {
      path: '/user',
      name: 'steamUser',
      component: User,
      meta: {
        title: () => t('menu.steamUser'),
        icon: 'i-mdi:user-group',
      },
    },
    {
      path: '/app',
      name: 'steamApp',
      component: App,
      meta: {
        title: () => t('menu.steamApp'),
        icon: 'i-iconamoon:apps',
      },
    },
    {
      path: '/useRecord',
      name: 'steamUseRecord',
      component: UseRecord,
      meta: {
        title: () => t('menu.steamUsage'),
        icon: 'i-uil:statistics',
      },
    },
  ],
}

export default routes
