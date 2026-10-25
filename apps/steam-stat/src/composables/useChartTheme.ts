import type { Oklch } from '@/features/stats/palette'
import { cardGradients, categoricalPalette, oklchToHex, parseOklch, readThemePrimary, sequentialPalette } from '@/features/stats/palette'

export interface ChartTheme {
  dark: boolean
  base: Oklch
  text: string
  subtext: string
  axis: string
  split: string
  background: string
  palette: (count: number) => string[]
  sequential: (steps: number) => string[]
  gradients: (count: number) => [string, string][]
}

function readVar(name: string, fallback: string): string {
  if (typeof document === 'undefined') {
    return fallback
  }
  const parsed = parseOklch(getComputedStyle(document.documentElement).getPropertyValue(name))
  return parsed ? oklchToHex(parsed) : fallback
}

/**
 * 图表主题：色板与文字 / 坐标轴颜色全部由当前主题（主题色 + 明暗）派生，切换主题时自动更新。
 */
export function useChartTheme() {
  const appSettingsStore = useAppSettingsStore()
  const appearanceStore = useAppearanceStore()
  const version = ref(0)

  watch(
    [() => appearanceStore.themeColor, () => appSettingsStore.currentColorScheme],
    () => nextTick(() => version.value++),
  )

  return computed<ChartTheme>(() => {
    void version.value
    const dark = appSettingsStore.currentColorScheme === 'dark'
    const base = readThemePrimary()
    return {
      dark,
      base,
      text: readVar('--foreground', dark ? '#fafafa' : '#18181b'),
      subtext: readVar('--muted-foreground', dark ? '#a1a1aa' : '#71717a'),
      axis: readVar('--border', dark ? '#3f3f46' : '#e4e4e7'),
      split: readVar('--border', dark ? '#27272a' : '#f4f4f5'),
      background: readVar('--card', dark ? '#18181b' : '#ffffff'),
      palette: count => categoricalPalette(base, Math.max(1, count), dark),
      sequential: steps => sequentialPalette(base, steps, dark),
      gradients: count => cardGradients(base, count, dark),
    }
  })
}
