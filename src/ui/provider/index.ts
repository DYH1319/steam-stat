import type { App } from 'vue'
import STable from '@surely-vue/table'
import Antd from 'ant-design-vue'
import 'ant-design-vue/dist/reset.css'
import '@surely-vue/table/dist/index.less'

function install(app: App) {
  app.use(Antd)
  app.use(STable)
}

export default { install }
