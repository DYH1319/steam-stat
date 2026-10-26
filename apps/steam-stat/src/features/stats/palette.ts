/**
 * 由主题色（CSS 变量 --primary，OKLCH 'L C H'）派生的图表色板：
 * 以主题色色相为起点按黄金角旋转，保证多色之间区分度且随主题色变化。
 */
export interface Oklch {
  l: number
  c: number
  h: number
}

const GOLDEN_ANGLE = 137.508
const FALLBACK: Oklch = { l: 0.62, c: 0.16, h: 260 }

export function parseOklch(value: string): Oklch | null {
  const [color] = value.trim().replace(/^oklch\(/, '').replace(/\)$/, '').split('/')
  const [l, c, h] = color.trim().split(/\s+/).map(part => Number.parseFloat(part.replace(/deg$/, '')))
  if ([l, c, h].some(part => part === undefined || Number.isNaN(part))) {
    return null
  }
  return { l: value.includes('%') && l > 1 ? l / 100 : l, c, h }
}

function linearToSrgb(value: number): number {
  return value <= 0.0031308 ? 12.92 * value : 1.055 * value ** (1 / 2.4) - 0.055
}

function channel(value: number): string {
  return Math.round(Math.min(Math.max(value, 0), 1) * 255).toString(16).padStart(2, '0')
}

export function oklchToHex({ l, c, h }: Oklch): string {
  const rad = h * Math.PI / 180
  const a = c * Math.cos(rad)
  const b = c * Math.sin(rad)
  const l1 = (l + 0.3963377774 * a + 0.2158037573 * b) ** 3
  const m1 = (l - 0.1055613458 * a - 0.0638541728 * b) ** 3
  const s1 = (l - 0.0894841775 * a - 1.291485548 * b) ** 3
  const r = linearToSrgb(4.0767416621 * l1 - 3.3077115913 * m1 + 0.2309699292 * s1)
  const g = linearToSrgb(-1.2684380046 * l1 + 2.6097574011 * m1 - 0.3413193965 * s1)
  const bl = linearToSrgb(-0.0041960863 * l1 - 0.7034186147 * m1 + 1.707614701 * s1)
  return `#${channel(r)}${channel(g)}${channel(bl)}`
}

export function oklchToRgba(color: Oklch, alpha: number): string {
  const hex = oklchToHex(color)
  const value = Number.parseInt(hex.slice(1), 16)
  return `rgba(${(value >> 16) & 255}, ${(value >> 8) & 255}, ${value & 255}, ${alpha})`
}

/** 读取当前主题色；黑白主题（C≈0）回退到默认色相 */
export function readThemePrimary(): Oklch {
  if (typeof document === 'undefined') {
    return FALLBACK
  }
  const parsed = parseOklch(getComputedStyle(document.documentElement).getPropertyValue('--primary'))
  if (!parsed) {
    return FALLBACK
  }
  return parsed.c < 0.03 ? FALLBACK : parsed
}

/** 分类色板：count 种颜色 */
export function categoricalPalette(base: Oklch, count: number, dark: boolean): string[] {
  const l = dark ? 0.72 : 0.62
  const c = Math.min(Math.max(base.c, 0.12), 0.2)
  return Array.from({ length: count }, (_, i) => oklchToHex({
    l: i % 2 === 0 ? l : l + (dark ? -0.08 : 0.08),
    c,
    h: (base.h + i * GOLDEN_ANGLE) % 360,
  }))
}

/** 顺序色板（热力图）：从浅到主题色 */
export function sequentialPalette(base: Oklch, steps: number, dark: boolean): string[] {
  const c = Math.min(Math.max(base.c, 0.12), 0.2)
  return Array.from({ length: steps }, (_, i) => {
    const t = steps === 1 ? 1 : i / (steps - 1)
    return dark
      ? oklchToHex({ l: 0.3 + t * 0.42, c: 0.02 + t * (c - 0.02), h: base.h })
      : oklchToHex({ l: 0.95 - t * 0.38, c: 0.02 + t * (c - 0.02), h: base.h })
  })
}

/** 统计卡片渐变：主题色相 ± 偏移的两色 */
export function cardGradients(base: Oklch, count: number, dark: boolean): [string, string][] {
  const l = dark ? 0.5 : 0.62
  const c = Math.min(Math.max(base.c, 0.12), 0.19)
  return Array.from({ length: count }, (_, i) => {
    const h = (base.h + i * 48) % 360
    return [oklchToHex({ l, c, h }), oklchToHex({ l: l - 0.06, c, h: (h + 28) % 360 })]
  })
}

/** 同一视图内同一游戏颜色一致：按出现顺序分配 */
export function createColorMap(ids: number[], palette: string[]): Map<number, string> {
  const map = new Map<number, string>()
  ids.forEach((id, i) => {
    if (!map.has(id)) {
      map.set(id, palette[map.size % palette.length] ?? palette[i % palette.length])
    }
  })
  return map
}
