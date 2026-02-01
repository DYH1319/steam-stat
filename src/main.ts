// 加载 iconify 图标
import { downloadAndInstall } from '@/iconify'
import icons from '@/iconify/index.json'
// 自定义指令
import directive from '@/utils/directive'
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

const app = createApp(App)
app.use(pinia)
app.use(router)
app.use(i18n)
app.use(uiProvider)

app.directive('ripple', Ripple)
directive(app)

// 从 electron 获取保存的语言设置
const electronApi = (window as any).electron
if (electronApi) {
  electronApi.settingGet().then((settings: any) => {
    if (settings.language) {
      i18n.global.locale.value = settings.language
    }
  })
}
if (icons.isOfflineUse) {
  for (const info of icons.collections) {
    downloadAndInstall(info).then()
  }
}

app.mount('#app')
