import type { App } from 'vue'
import Antd from 'ant-design-vue'
import ElementPlus from 'element-plus'
import 'ant-design-vue/dist/reset.css'
import 'element-plus/dist/index.css'
import 'element-plus/theme-chalk/dark/css-vars.css'

function install(app: App) {
  app.use(Antd)
  app.use(ElementPlus)
}

export default { install }
