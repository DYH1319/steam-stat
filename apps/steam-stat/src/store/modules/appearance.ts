import type { ThemeSettings } from '@fantastic-admin/settings'
import type { ThemeColorName } from '@/themes/colors'
import { THEME_COLOR_CSS_VARS, themeColors } from '@/themes/colors'

/**
 * 桌面端外观状态（主题色 / 语言 / 与主进程设置同步）。
 * v6 框架的 useAppSettingsStore 负责 colorScheme 与 radius 的实际应用，
 * 这里只保存应用自身的偏好并将其转发给框架 store。
 */
export const useAppearanceStore = defineStore('appearance', () => {
  const appSettingsStore = useAppSettingsStore()

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
    const scheme = appSettingsStore.currentColorScheme
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
  watch([themeColor, () => appSettingsStore.currentColorScheme], applyThemeColor, { immediate: true })

  // 颜色方案 / 圆角系数（转发给框架 store）
  function setColorScheme(scheme: NonNullable<ThemeSettings['colorScheme']>) {
    appSettingsStore.setColorScheme(scheme)
  }
  function setRadius(radius: number) {
    appSettingsStore.settings.theme.radius = radius
  }

  // 国际化
  const locale = ref<'zh-CN' | 'en-US'>('zh-CN')
  function setLocale(value?: 'zh-CN' | 'en-US') {
    locale.value = value ?? locale.value
  }

  // 从主进程设置文件初始化
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
      if (settings.radius != null) {
        setRadius(settings.radius)
      }
    })
  }

  return {
    themeColor,
    setThemeColor,
    setColorScheme,
    setRadius,
    locale,
    setLocale,
  }
})
