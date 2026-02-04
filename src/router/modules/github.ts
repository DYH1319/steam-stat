import type { RouteRecordRaw } from 'vue-router'
import Layout from '@/layouts/index.vue'

const routes: RouteRecordRaw = {
  path: '/github',
  component: Layout,
  meta: {
    title: 'GitHub',
    icon: 'i-uil:github',
    link: 'https://github.com/DYH1319/steam-stat',
  },
}

export default routes
