// 加载 iconify 图标
import { downloadAndInstall } from '@/iconify'
import icons from '@/iconify/index.json'
// 自定义指令
import directive from '@/utils/directive'
import { setExperimentalEnabled } from '@/utils/experimental'
// @ts-expect-error vue-ripple-directive
import Ripple from '../scripts/ripple.js'

import App from './App.vue'
import i18n from './i18n'
import router from './router'
import pinia from './store'
import uiProvider from './ui/provider'

import '@/utils/systemCopyright'
// 加载 svg 图标
import 'virtual:svg-icons-register'
// UnoCSS
import '@unocss/reset/tailwind-compat.css'
import 'virtual:uno.css'
// 全局样式
import '@/assets/styles/globals.css'

// 从主进程读取持久化设置。
// 必须在 app.use(router) 之前完成：vue-router 在安装时会触发首次导航，
// 而动态路由是在守卫中生成的，实验性功能开关需要在那之前就位。
async function applyPersistedSettings() {
  const electronApi = (window as Window).electron
  if (!electronApi) {
    return
  }
  try {
    const settings = await electronApi.settingGet()
    if (settings.language) {
      i18n.global.locale.value = settings.language
    }
    setExperimentalEnabled(settings.experimentalFeatures === true)
  }
  catch {
    // 读取失败时使用默认值继续启动，不阻塞应用
  }
}

async function bootstrap() {
  const app = createApp(App)
  app.use(pinia)

  await applyPersistedSettings()

  app.use(router)
  app.use(i18n)
  app.use(uiProvider)

  app.directive('ripple', Ripple)
  directive(app)

  if (icons.isOfflineUse) {
    for (const info of icons.collections) {
      downloadAndInstall(info).then()
    }
  }

  app.mount('#app')
}

bootstrap()
