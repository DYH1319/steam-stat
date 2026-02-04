import type { RouteRecordRaw } from 'vue-router'

const routes: RouteRecordRaw = {
  path: '/github',
  component: () => import('@/layouts/index.vue'),
  meta: {
    title: 'GitHub',
    icon: 'i-uil:github',
    link: 'https://github.com/DYH1319/steam-stat',
  },
}

export default routes
