export type ThemeColorName = 'black' | 'blue' | 'orange'

export interface ThemeColorDefinition {
  /** 展示用颜色（浅色模式） */
  color: string
  /** 展示用颜色（深色模式） */
  colorDark: string
  /** 浅色模式下的 CSS 变量覆盖 */
  light: Record<string, string>
  /** 深色模式下的 CSS 变量覆盖 */
  dark: Record<string, string>
}

/**
 * 所有可能被主题色覆盖的 CSS 变量 key
 * 用于在切换主题色时清除旧的覆盖
 */
export const THEME_COLOR_CSS_VARS = [
  '--primary',
  '--primary-foreground',
  '--ring',
  '--g-header-menu-active-bg',
  '--g-header-menu-active-color',
  '--g-main-sidebar-menu-active-bg',
  '--g-main-sidebar-menu-active-color',
  '--g-sub-sidebar-menu-active-bg',
  '--g-sub-sidebar-menu-active-color',
] as const

/**
 * 主题色定义
 * - black: 默认黑色主题（无覆盖，使用 UnoCSS 预设值）
 * - blue: 蓝色主题（浅色 #2563eb，深色 #3b82f6）
 * - orange: 橙色主题（浅色 #f97316，深色 #ea580c）
 */
export const themeColors: Record<ThemeColorName, ThemeColorDefinition> = {
  black: {
    color: '#18181b',
    colorDark: '#fafafa',
    light: {},
    dark: {},
  },
  blue: {
    color: '#2563eb',
    colorDark: '#3b82f6',
    light: {
      '--primary': '217 91% 53%',
      '--primary-foreground': '0 0% 98%',
      '--ring': '217 91% 53%',
    },
    dark: {
      '--primary': '217 91% 60%',
      '--primary-foreground': '0 0% 98%',
      '--ring': '217 91% 60%',
      '--g-header-menu-active-bg': 'hsl(var(--primary))',
      '--g-header-menu-active-color': 'hsl(var(--primary-foreground))',
      '--g-main-sidebar-menu-active-bg': 'hsl(var(--primary))',
      '--g-main-sidebar-menu-active-color': 'hsl(var(--primary-foreground))',
      '--g-sub-sidebar-menu-active-bg': 'hsl(var(--primary))',
      '--g-sub-sidebar-menu-active-color': 'hsl(var(--primary-foreground))',
    },
  },
  orange: {
    color: '#f97316',
    colorDark: '#ea580c',
    light: {
      '--primary': '25 95% 53%',
      '--primary-foreground': '0 0% 98%',
      '--ring': '25 95% 53%',
    },
    dark: {
      '--primary': '21 90% 48%',
      '--primary-foreground': '0 0% 98%',
      '--ring': '21 90% 48%',
      '--g-header-menu-active-bg': 'hsl(var(--primary))',
      '--g-header-menu-active-color': 'hsl(var(--primary-foreground))',
      '--g-main-sidebar-menu-active-bg': 'hsl(var(--primary))',
      '--g-main-sidebar-menu-active-color': 'hsl(var(--primary-foreground))',
      '--g-sub-sidebar-menu-active-bg': 'hsl(var(--primary))',
      '--g-sub-sidebar-menu-active-color': 'hsl(var(--primary-foreground))',
    },
  },
}

export const lightTheme = {
  'color-scheme': 'light',
  // shadcn
  '--background': '0 0% 100%',
  '--foreground': '240 10% 3.9%',
  '--card': '0 0% 100%',
  '--card-foreground': '240 10% 3.9%',
  '--popover': '0 0% 100%',
  '--popover-foreground': '240 10% 3.9%',
  '--primary': '240 5.9% 10%',
  '--primary-foreground': '0 0% 98%',
  '--secondary': '240 4.8% 95.9%',
  '--secondary-foreground': '240 5.9% 10%',
  '--muted': '240 4.8% 95.9%',
  '--muted-foreground': '240 3.8% 46.1%',
  '--accent': '240 4.8% 95.9%',
  '--accent-foreground': '240 5.9% 10%',
  '--destructive': '0 84.2% 60.2%',
  '--destructive-foreground': '0 0% 98%',
  '--border': '240 5.9% 90%',
  '--input': '240 5.9% 90%',
  '--ring': '240 5.9% 10%',
  // 主要区域
  '--g-main-area-bg': 'hsl(0 0% 95%)',
  // 头部
  '--g-header-bg': 'hsl(var(--background))',
  '--g-header-color': 'hsl(var(--foreground))',
  '--g-header-menu-color': 'hsl(var(--accent-foreground))',
  '--g-header-menu-hover-bg': 'hsl(var(--accent))',
  '--g-header-menu-hover-color': 'hsl(var(--accent-foreground))',
  '--g-header-menu-active-bg': 'hsl(var(--primary))',
  '--g-header-menu-active-color': 'hsl(var(--primary-foreground))',
  // 主导航
  '--g-main-sidebar-bg': 'hsl(var(--background))',
  '--g-main-sidebar-menu-color': 'hsl(var(--accent-foreground))',
  '--g-main-sidebar-menu-hover-bg': 'hsl(var(--accent))',
  '--g-main-sidebar-menu-hover-color': 'hsl(var(--accent-foreground))',
  '--g-main-sidebar-menu-active-bg': 'hsl(var(--primary))',
  '--g-main-sidebar-menu-active-color': 'hsl(var(--primary-foreground))',
  // 次导航
  '--g-sub-sidebar-bg': 'hsl(var(--background))',
  '--g-sub-sidebar-menu-color': 'hsl(var(--accent-foreground))',
  '--g-sub-sidebar-menu-hover-bg': 'hsl(var(--accent))',
  '--g-sub-sidebar-menu-hover-color': 'hsl(var(--accent-foreground))',
  '--g-sub-sidebar-menu-active-bg': 'hsl(var(--primary))',
  '--g-sub-sidebar-menu-active-color': 'hsl(var(--primary-foreground))',
  // 标签栏
  '--g-tabbar-bg': 'var(--g-main-area-bg)',
  '--g-tabbar-dividers-bg': 'hsl(var(--accent-foreground) / 50%)',
  '--g-tabbar-tab-color': 'hsl(var(--accent-foreground) / 50%)',
  '--g-tabbar-tab-hover-bg': 'hsl(var(--border))',
  '--g-tabbar-tab-hover-color': 'hsl(var(--accent-foreground) / 50%)',
  '--g-tabbar-tab-active-bg': 'hsl(var(--background))',
  '--g-tabbar-tab-active-color': 'hsl(var(--foreground))',
  // 工具栏
  '--g-toolbar-bg': 'hsl(var(--background))',
}

export const darkTheme = {
  'color-scheme': 'dark',
  // shadcn
  '--background': '240 10% 3.9%',
  '--foreground': '0 0% 98%',
  '--card': '240 10% 3.9%',
  '--card-foreground': '0 0% 98%',
  '--popover': '240 10% 3.9%',
  '--popover-foreground': '0 0% 98%',
  '--primary': '0 0% 98%',
  '--primary-foreground': '240 5.9% 10%',
  '--secondary': '240 3.7% 15.9%',
  '--secondary-foreground': '0 0% 98%',
  '--muted': '240 3.7% 15.9%',
  '--muted-foreground': '240 5% 64.9%',
  '--accent': '240 3.7% 15.9%',
  '--accent-foreground': '0 0% 98%',
  '--destructive': '0 62.8% 30.6%',
  '--destructive-foreground': '0 0% 98%',
  '--border': '240 3.7% 15.9%',
  '--input': '240 3.7% 15.9%',
  '--ring': '240 4.9% 83.9%',
  // 主要区域
  '--g-main-area-bg': 'hsl(var(--background))',
  // 头部
  '--g-header-bg': 'hsl(var(--background))',
  '--g-header-color': 'hsl(var(--foreground))',
  '--g-header-menu-color': 'hsl(var(--muted-foreground))',
  '--g-header-menu-hover-bg': 'hsl(var(--muted))',
  '--g-header-menu-hover-color': 'hsl(var(--muted-foreground))',
  '--g-header-menu-active-bg': 'hsl(var(--accent))',
  '--g-header-menu-active-color': 'hsl(var(--accent-foreground))',
  // 主导航
  '--g-main-sidebar-bg': 'hsl(var(--background))',
  '--g-main-sidebar-menu-color': 'hsl(var(--muted-foreground))',
  '--g-main-sidebar-menu-hover-bg': 'hsl(var(--muted))',
  '--g-main-sidebar-menu-hover-color': 'hsl(var(--muted-foreground))',
  '--g-main-sidebar-menu-active-bg': 'hsl(var(--accent))',
  '--g-main-sidebar-menu-active-color': 'hsl(var(--accent-foreground))',
  // 次导航
  '--g-sub-sidebar-bg': 'hsl(var(--background))',
  '--g-sub-sidebar-menu-color': 'hsl(var(--muted-foreground))',
  '--g-sub-sidebar-menu-hover-bg': 'hsl(var(--muted))',
  '--g-sub-sidebar-menu-hover-color': 'hsl(var(--muted-foreground))',
  '--g-sub-sidebar-menu-active-bg': 'hsl(var(--accent))',
  '--g-sub-sidebar-menu-active-color': 'hsl(var(--accent-foreground))',
  // 标签栏
  '--g-tabbar-bg': 'var(--g-main-area-bg)',
  '--g-tabbar-dividers-bg': 'hsl(var(--accent-foreground) / 50%)',
  '--g-tabbar-tab-color': 'hsl(var(--accent-foreground) / 50%)',
  '--g-tabbar-tab-hover-bg': 'hsl(var(--accent) / 50%)',
  '--g-tabbar-tab-hover-color': 'hsl(var(--accent-foreground) / 50%)',
  '--g-tabbar-tab-active-bg': 'hsl(var(--secondary))',
  '--g-tabbar-tab-active-color': 'hsl(var(--foreground))',
  // 工具栏
  '--g-toolbar-bg': 'hsl(var(--background))',
}
