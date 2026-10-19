import type { RouteRecordRaw } from 'vue-router'
import i18n from '@/i18n'

const t = i18n.global.t

function Layout() {
  return import('@/layouts/index.vue')
}

const routes: RouteRecordRaw = {
  path: '/data-source',
  component: Layout,
  meta: {
    title: () => t('menu.dataSource'),
    icon: 'i-mdi:database-cog-outline',
    defaultOpened: true,
  },
  children: [
    {
      path: '/data-source/local',
      name: 'dataSourceLocal',
      component: () => import('@/views/dataSource/local.vue'),
      meta: {
        title: () => t('menu.dataSourceLocal'),
        icon: 'i-mdi:harddisk',
      },
    },
    {
      path: '/data-source/web-api',
      name: 'dataSourceWebApi',
      component: () => import('@/views/dataSource/webApi.vue'),
      meta: {
        title: () => t('menu.dataSourceWebApi'),
        icon: 'i-mdi:web',
      },
    },
    {
      path: '/data-source/cm',
      name: 'dataSourceCm',
      component: () => import('@/views/dataSource/cm.vue'),
      meta: {
        title: () => t('menu.dataSourceCm'),
        icon: 'i-tabler:brand-steam',
      },
    },
  ],
}

export default routes
