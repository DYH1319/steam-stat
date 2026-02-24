import type { Settings } from '#/global'
import type { RouteMeta } from 'vue-router'
import type { ThemeColorName } from '../../../themes'
import { cloneDeep } from 'es-toolkit'
import settingsDefault from '@/settings'
import { merge } from '@/utils/object'
import { THEME_COLOR_CSS_VARS, themeColors } from '../../../themes'

export const useSettingsStore = defineStore(
  // 唯一ID
  'settings',
  () => {
    const settings = ref(settingsDefault)

    const prefersColorScheme = window.matchMedia('(prefers-color-scheme: dark)')
    watch(() => settings.value.app.colorScheme, (val) => {
      if (val === '') {
        prefersColorScheme.addEventListener('change', updateTheme)
      }
      else {
        prefersColorScheme.removeEventListener('change', updateTheme)
      }
    }, {
      immediate: true,
    })

    const currentColorScheme = ref<Exclude<Settings.app['colorScheme'], ''>>()
    watch(() => settings.value.app.colorScheme, updateTheme, {
      immediate: true,
    })
    function updateTheme() {
      let colorScheme = settings.value.app.colorScheme
      if (colorScheme === '') {
        colorScheme = prefersColorScheme.matches ? 'dark' : 'light'
      }
      currentColorScheme.value = colorScheme
      switch (colorScheme) {
        case 'light':
          document.documentElement.classList.remove('dark')
          break
        case 'dark':
          document.documentElement.classList.add('dark')
          break
      }
    }

    watch(() => settings.value.app.radius, (val) => {
      document.documentElement.style.removeProperty('--radius')
      document.documentElement.style.setProperty('--radius', `${val}rem`)
    }, {
      immediate: true,
    })
    watch([
      () => settings.value.app.enableMournMode,
      () => settings.value.app.enableColorAmblyopiaMode,
    ], (val) => {
      document.documentElement.style.removeProperty('filter')
      if (val[0] && val[1]) {
        document.documentElement.style.setProperty('filter', 'grayscale(100%) invert(80%)')
      }
      else if (val[0]) {
        document.documentElement.style.setProperty('filter', 'grayscale(100%)')
      }
      else if (val[1]) {
        document.documentElement.style.setProperty('filter', 'invert(80%)')
      }
    }, {
      immediate: true,
    })

    watch(() => settings.value.menu.mode, (val) => {
      document.body.setAttribute('data-menu-mode', val)
    }, {
      immediate: true,
    })

    // 操作系统
    const os = ref<'mac' | 'windows' | 'linux' | 'other'>('other')
    const agent = navigator.userAgent.toLowerCase()
    switch (true) {
      case agent.includes('mac os'):
        os.value = 'mac'
        break
      case agent.includes('windows'):
        os.value = 'windows'
        break
      case agent.includes('linux'):
        os.value = 'linux'
        break
    }

    // 页面是否刷新
    const isReloading = ref(false)
    // 切换当前页面是否刷新
    function setIsReloading(value?: boolean) {
      isReloading.value = value ?? !isReloading.value
    }

    // 页面标题
    const title = ref<RouteMeta['title']>()
    // 记录页面标题
    function setTitle(_title: RouteMeta['title']) {
      title.value = _title
    }

    // 显示模式
    const mode = ref<'pc' | 'mobile'>('pc')
    // 设置显示模式
    function setMode(width: number) {
      if (settings.value.layout.enableMobileAdaptation) {
        // 先判断 UA 是否为移动端设备（手机&平板）
        if (/Android|webOS|iPhone|iPad|iPod|BlackBerry|IEMobile|Opera Mini/i.test(navigator.userAgent)) {
          mode.value = 'mobile'
        }
        else {
          // 如果是桌面设备，则根据页面宽度判断是否需要切换为移动端展示
          mode.value = width < 1024 ? 'mobile' : 'pc'
        }
      }
      else {
        mode.value = 'pc'
      }
    }

    // 切换侧边栏导航展开/收起
    function toggleSidebarCollapse() {
      settings.value.menu.subMenuCollapse = !settings.value.menu.subMenuCollapse
    }
    // 次导航是否收起（用于记录 pc 模式下最后的状态）
    const subMenuCollapseLastStatus = ref(settingsDefault.menu.subMenuCollapse)
    watch(() => settings.value.menu.subMenuCollapse, (val) => {
      if (mode.value === 'pc') {
        subMenuCollapseLastStatus.value = val
      }
    })
    watch(mode, (val) => {
      switch (val) {
        case 'pc':
          settings.value.menu.subMenuCollapse = subMenuCollapseLastStatus.value
          break
        case 'mobile':
          settings.value.menu.subMenuCollapse = true
          break
      }
      document.body.setAttribute('data-mode', val)
    }, {
      immediate: true,
    })

    // 设置主题颜色模式
    function setColorScheme(color: Required<Settings.app>['colorScheme']) {
      settings.value.app.colorScheme = color
    }

    // 设置圆角系数
    function setRadius(radius: number) {
      settings.value.app.radius = radius
    }

    // 主题色
    const themeColor = ref<ThemeColorName>('black')
    function setThemeColor(color: ThemeColorName) {
      themeColor.value = color
    }
    function applyThemeColor() {
      const root = document.documentElement
      const colorDef = themeColors[themeColor.value]
      if (!colorDef) {
        return
      }
      const scheme = currentColorScheme.value
      const overrides = scheme === 'dark' ? colorDef.dark : colorDef.light
      // 先清除所有可能的主题色覆盖
      for (const key of THEME_COLOR_CSS_VARS) {
        root.style.removeProperty(key)
      }
      // 应用新的覆盖
      for (const [key, value] of Object.entries(overrides)) {
        root.style.setProperty(key, value)
      }
    }
    watch([themeColor, currentColorScheme], applyThemeColor, { immediate: true })

    // 更新应用配置
    function updateSettings(data: Settings.all, fromBase = false) {
      settings.value = merge(data, fromBase ? cloneDeep(settingsDefault) : settings.value)
    }

    // 国际化
    const locale = ref<'zh-CN' | 'en-US'>('zh-CN')
    function setLocale(value?: 'zh-CN' | 'en-US') {
      locale.value = value ?? locale.value
    }

    // 从设置文件初始化
    const electronApi = (window as Window).electron
    if (electronApi) {
      electronApi.settingGet().then((settings) => {
        if (settings.language) {
          setLocale(settings.language)
        }
        if (settings.themeColor && settings.themeColor in themeColors) {
          setThemeColor(settings.themeColor as ThemeColorName)
        }
        if (settings.colorScheme) {
          setColorScheme(settings.colorScheme === 'system' ? '' : settings.colorScheme)
        }
        if (settings.radius) {
          setRadius(settings.radius)
        }
      })
    }

    return {
      settings,
      currentColorScheme,
      os,
      isReloading,
      setIsReloading,
      title,
      setTitle,
      mode,
      setMode,
      subMenuCollapseLastStatus,
      toggleSidebarCollapse,
      setColorScheme,
      setRadius,
      themeColor,
      setThemeColor,
      updateSettings,
      locale,
      setLocale,
    }
  },
)
