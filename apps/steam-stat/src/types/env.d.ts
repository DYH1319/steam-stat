/// <reference types="vite/client" />
interface ImportMetaEnv {
  // Auto generate by env-parse
  /**
   * 接口请求地址，会设置到 axios 的 baseURL 参数上
   */
  readonly VITE_APP_API_BASEURL: string
  /**
   * 调试工具，可设置 eruda 或 vconsole，如果不需要开启则留空
   */
  readonly VITE_APP_DEBUG_TOOL: string
  /**
   * 是否禁用开发者工具，可防止被调试
   */
  readonly VITE_APP_DISABLE_DEVTOOL: boolean
  /**
   * 应用配置面板
   */
  readonly VITE_APP_SETTING: boolean
  /**
   * localStorage/sessionStorage 前缀
   */
  readonly VITE_APP_STORAGE_PREFIX: string
  /**
   * 页面标题
   */
  readonly VITE_APP_TITLE: string
  /**
   * 是否开启代理
   */
  readonly VITE_ENABLE_PROXY: boolean
  /**
   * 启用 turbo console
   */
  readonly VITE_ENABLE_TURBO_CONSOLE: boolean
  /**
   * 启用 Vue 开发工具
   */
  readonly VITE_ENABLE_VUE_DEVTOOLS: boolean
  /**
   * 启动编辑器，用于 vite-plugin-vue-devtools 和 unplugin-turbo-console
   */
  readonly VITE_LAUNCH_EDITOR: string
}
