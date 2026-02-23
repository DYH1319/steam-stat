import type { App } from 'vue'
import STable, { setLicenseKey } from '@surely-vue/table'
import Antd from 'ant-design-vue'
import { MD5 as md5 } from 'crypto-js'
import ElementPlus from 'element-plus'
import { encode as encodeBase64 } from 'js-base64'
import 'ant-design-vue/dist/reset.css'
import '@surely-vue/table/dist/index.less'
import 'element-plus/dist/index.css'
import 'element-plus/theme-chalk/dark/css-vars.css'

const domain = globalThis.location.hostname
const key = encodeBase64(`ORDER:00001,EXPIRY=33227712000000,DOMAIN=${domain},ULTIMATE=1,KEYVERSION=1`)
const sign = md5(key).toString().toLowerCase()
setLicenseKey(`${sign}${key}`)

function install(app: App) {
  app.use(Antd)
  app.use(STable)
  app.use(ElementPlus)
}

export default { install }
