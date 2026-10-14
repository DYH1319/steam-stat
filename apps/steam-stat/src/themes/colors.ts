export type ThemeColorName = 'black' | 'blue' | 'orange' | 'green' | 'red' | 'yellow' | 'purple' | 'pink' | 'cyan'

export interface ThemeColorDefinition {
  /** 展示用颜色（浅色模式） */
  color: string
  /** 展示用颜色（深色模式） */
  colorDark: string
  /** 浅色模式下的 CSS 变量覆盖（OKLCH 'L C H'，与 packages/themes 同格式） */
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
 * 主题色定义（v6 起 CSS 变量为 OKLCH 'L C H' 三元组）
 * - black: 默认黑色主题（无覆盖，使用框架预设值）
 * - blue: 蓝色主题（浅色 #2563eb，深色 #3b82f6）
 * - orange: 橙色主题（浅色 #f97316，深色 #ea580c）
 * - green: 绿色主题（浅色 #16a34a，深色 #22c55e）
 * - red: 红色主题（浅色 #e11d48，深色 #e11d48）
 * - yellow: 黄色主题（浅色 #facc15，深色 #facc15）
 * - purple: 紫色主题（浅色 #7c3aed，深色 #6d28d9）
 * - pink: 粉色主题（浅色 #f37fb7，深色 #f37fb7）
 * - cyan: 青色主题（浅色 #58d1c9，深色 #58d1c9）
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
      '--primary': '0.5723 0.2162 260.2763',
      '--primary-foreground': '0.985 0 0',
      '--ring': '0.5723 0.2162 260.2763',
    },
    dark: {
      '--primary': '0.6261 0.1859 259.5957',
      '--primary-foreground': '0.985 0 0',
      '--ring': '0.6261 0.1859 259.5957',
      '--g-header-menu-active-bg': 'oklch(var(--primary))',
      '--g-header-menu-active-color': 'oklch(var(--primary-foreground))',
      '--g-main-sidebar-menu-active-bg': 'oklch(var(--primary))',
      '--g-main-sidebar-menu-active-color': 'oklch(var(--primary-foreground))',
      '--g-sub-sidebar-menu-active-bg': 'oklch(var(--primary))',
      '--g-sub-sidebar-menu-active-color': 'oklch(var(--primary-foreground))',
    },
  },
  orange: {
    color: '#f97316',
    colorDark: '#ea580c',
    light: {
      '--primary': '0.7065 0.186 48.1293',
      '--primary-foreground': '0.985 0 0',
      '--ring': '0.7065 0.186 48.1293',
    },
    dark: {
      '--primary': '0.6455 0.1918 41.7118',
      '--primary-foreground': '0.985 0 0',
      '--ring': '0.6455 0.1918 41.7118',
      '--g-header-menu-active-bg': 'oklch(var(--primary))',
      '--g-header-menu-active-color': 'oklch(var(--primary-foreground))',
      '--g-main-sidebar-menu-active-bg': 'oklch(var(--primary))',
      '--g-main-sidebar-menu-active-color': 'oklch(var(--primary-foreground))',
      '--g-sub-sidebar-menu-active-bg': 'oklch(var(--primary))',
      '--g-sub-sidebar-menu-active-color': 'oklch(var(--primary-foreground))',
    },
  },
  green: {
    color: '#16a34a',
    colorDark: '#22c55e',
    light: {
      '--primary': '0.623 0.1688 149.1777',
      '--primary-foreground': '0.985 0 0',
      '--ring': '0.623 0.1688 149.1777',
    },
    dark: {
      '--primary': '0.7205 0.192 149.4926',
      '--primary-foreground': '0.985 0 0',
      '--ring': '0.7205 0.192 149.4926',
      '--g-header-menu-active-bg': 'oklch(var(--primary))',
      '--g-header-menu-active-color': 'oklch(var(--primary-foreground))',
      '--g-main-sidebar-menu-active-bg': 'oklch(var(--primary))',
      '--g-main-sidebar-menu-active-color': 'oklch(var(--primary-foreground))',
      '--g-sub-sidebar-menu-active-bg': 'oklch(var(--primary))',
      '--g-sub-sidebar-menu-active-color': 'oklch(var(--primary-foreground))',
    },
  },
  red: {
    color: '#e11d48',
    colorDark: '#e11d48',
    light: {
      '--primary': '0.5872 0.2224 17.7203',
      '--primary-foreground': '0.985 0 0',
      '--ring': '0.5872 0.2224 17.7203',
    },
    dark: {
      '--primary': '0.5872 0.2224 17.7203',
      '--primary-foreground': '0.985 0 0',
      '--ring': '0.5872 0.2224 17.7203',
      '--g-header-menu-active-bg': 'oklch(var(--primary))',
      '--g-header-menu-active-color': 'oklch(var(--primary-foreground))',
      '--g-main-sidebar-menu-active-bg': 'oklch(var(--primary))',
      '--g-main-sidebar-menu-active-color': 'oklch(var(--primary-foreground))',
      '--g-sub-sidebar-menu-active-bg': 'oklch(var(--primary))',
      '--g-sub-sidebar-menu-active-color': 'oklch(var(--primary-foreground))',
    },
  },
  yellow: {
    color: '#facc15',
    colorDark: '#facc15',
    light: {
      '--primary': '0.8611 0.1734 91.9641',
      '--primary-foreground': '0.1405 0.0044 285.8238',
      '--ring': '0.8611 0.1734 91.9641',
    },
    dark: {
      '--primary': '0.8611 0.1734 91.9641',
      '--primary-foreground': '0.1405 0.0044 285.8238',
      '--ring': '0.8611 0.1734 91.9641',
      '--g-header-menu-active-bg': 'oklch(var(--primary))',
      '--g-header-menu-active-color': 'oklch(var(--primary-foreground))',
      '--g-main-sidebar-menu-active-bg': 'oklch(var(--primary))',
      '--g-main-sidebar-menu-active-color': 'oklch(var(--primary-foreground))',
      '--g-sub-sidebar-menu-active-bg': 'oklch(var(--primary))',
      '--g-sub-sidebar-menu-active-color': 'oklch(var(--primary-foreground))',
    },
  },
  purple: {
    color: '#7c3aed',
    colorDark: '#6d28d9',
    light: {
      '--primary': '0.5424 0.2454 293.016',
      '--primary-foreground': '0.985 0 0',
      '--ring': '0.5424 0.2454 293.016',
    },
    dark: {
      '--primary': '0.4865 0.2423 291.8661',
      '--primary-foreground': '0.985 0 0',
      '--ring': '0.4865 0.2423 291.8661',
      '--g-header-menu-active-bg': 'oklch(var(--primary))',
      '--g-header-menu-active-color': 'oklch(var(--primary-foreground))',
      '--g-main-sidebar-menu-active-bg': 'oklch(var(--primary))',
      '--g-main-sidebar-menu-active-color': 'oklch(var(--primary-foreground))',
      '--g-sub-sidebar-menu-active-bg': 'oklch(var(--primary))',
      '--g-sub-sidebar-menu-active-color': 'oklch(var(--primary-foreground))',
    },
  },
  pink: {
    color: '#f37fb7',
    colorDark: '#f37fb7',
    light: {
      '--primary': '0.7459 0.1523 351.0953',
      '--primary-foreground': '0.1405 0.0044 285.8238',
      '--ring': '0.7459 0.1523 351.0953',
    },
    dark: {
      '--primary': '0.7459 0.1523 351.0953',
      '--primary-foreground': '0.1405 0.0044 285.8238',
      '--ring': '0.7459 0.1523 351.0953',
      '--g-header-menu-active-bg': 'oklch(var(--primary))',
      '--g-header-menu-active-color': 'oklch(var(--primary-foreground))',
      '--g-main-sidebar-menu-active-bg': 'oklch(var(--primary))',
      '--g-main-sidebar-menu-active-color': 'oklch(var(--primary-foreground))',
      '--g-sub-sidebar-menu-active-bg': 'oklch(var(--primary))',
      '--g-sub-sidebar-menu-active-color': 'oklch(var(--primary-foreground))',
    },
  },
  cyan: {
    color: '#58d1c9',
    colorDark: '#58d1c9',
    light: {
      '--primary': '0.7924 0.1104 189.0817',
      '--primary-foreground': '0.1405 0.0044 285.8238',
      '--ring': '0.7924 0.1104 189.0817',
    },
    dark: {
      '--primary': '0.7924 0.1104 189.0817',
      '--primary-foreground': '0.1405 0.0044 285.8238',
      '--ring': '0.7924 0.1104 189.0817',
      '--g-header-menu-active-bg': 'oklch(var(--primary))',
      '--g-header-menu-active-color': 'oklch(var(--primary-foreground))',
      '--g-main-sidebar-menu-active-bg': 'oklch(var(--primary))',
      '--g-main-sidebar-menu-active-color': 'oklch(var(--primary-foreground))',
      '--g-sub-sidebar-menu-active-bg': 'oklch(var(--primary))',
      '--g-sub-sidebar-menu-active-color': 'oklch(var(--primary-foreground))',
    },
  },
}
