import { nextTick, onMounted, ref, watch } from 'vue'
import { buildPalette, oklchToHex, parseOklchTriplet } from '@/features/stats/palette'
import { useAppearanceStore } from '@/store/modules/appearance'

export interface ChartTheme {
  palette: string[]
  primary: string
  text: string
  mutedText: string
  border: string
  background: string
}

function readVar(name: string): string {
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim()
}

function varHex(name: string, fallback: string): string {
  const raw = readVar(name)
  const oklch = parseOklchTriplet(raw)
  return oklch ? oklchToHex(oklch.l, oklch.c, oklch.h) : (raw || fallback)
}

/** 主题派生的图表配色：跟随主题色与明暗模式变化 */
export function useChartTheme() {
  const theme = ref<ChartTheme>({
    palette: [],
    primary: '#2563eb',
    text: '#18181b',
    mutedText: '#71717a',
    border: '#e4e4e7',
    background: '#ffffff',
  })

  const appearanceStore = useAppearanceStore()
  const appSettingsStore = useAppSettingsStore()

  async function recompute() {
    await nextTick()
    const dark = appSettingsStore.currentColorScheme === 'dark'
    const primary = varHex('--primary', dark ? '#fafafa' : '#18181b')
    theme.value = {
      palette: buildPalette(readVar('--primary'), 12, dark),
      primary,
      text: varHex('--foreground', dark ? '#fafafa' : '#18181b'),
      mutedText: varHex('--muted-foreground', '#71717a'),
      border: varHex('--border', dark ? '#27272a' : '#e4e4e7'),
      background: varHex('--card', dark ? '#09090b' : '#ffffff'),
    }
  }

  onMounted(recompute)
  watch(
    () => [appearanceStore.themeColor, appSettingsStore.currentColorScheme],
    recompute,
    { flush: 'post' },
  )

  function baseOption() {
    return {
      backgroundColor: 'transparent',
      textStyle: { color: theme.value.text },
      tooltip: {
        backgroundColor: theme.value.background,
        borderColor: theme.value.border,
        textStyle: { color: theme.value.text },
      },
    }
  }

  return { theme, baseOption }
}
