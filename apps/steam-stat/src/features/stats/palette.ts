/** OKLCH 'L C H' 三元组与主题派生调色板 */
export function parseOklchTriplet(value: string): { l: number, c: number, h: number } | null {
  const parts = value.trim().split(/\s+/)
  if (parts.length < 3) {
    return null
  }
  const [l, c, h] = parts.map(Number)
  if ([l, c, h].some(Number.isNaN)) {
    return null
  }
  return { l, c, h }
}

/** OKLab -> 线性 sRGB -> gamma -> #rrggbb */
export function oklchToHex(l: number, c: number, h: number): string {
  const hr = (h * Math.PI) / 180
  const a = c * Math.cos(hr)
  const b = c * Math.sin(hr)
  const l_ = l + 0.3963377774 * a + 0.2158037573 * b
  const m_ = l - 0.1055613458 * a - 0.0638541728 * b
  const s_ = l - 0.0894841775 * a - 1.2914855480 * b
  const l3 = l_ ** 3
  const m3 = m_ ** 3
  const s3 = s_ ** 3
  let r = +4.0767416621 * l3 - 3.3077115913 * m3 + 0.2309699292 * s3
  let g = -1.2684380046 * l3 + 2.6097574011 * m3 - 0.3413193965 * s3
  let bl = -0.0041960863 * l3 - 0.7034186147 * m3 + 1.7076147010 * s3
  const gamma = (x: number) => x <= 0.0031308 ? 12.92 * x : 1.055 * x ** (1 / 2.4) - 0.055
  const to255 = (x: number) => Math.round(Math.min(1, Math.max(0, gamma(x))) * 255)
  r = to255(r)
  g = to255(g)
  bl = to255(bl)
  return `#${[r, g, bl].map(v => v.toString(16).padStart(2, '0')).join('')}`
}

/** 从主题主色出发按黄金角旋转生成 count 个图表色 */
export function buildPalette(primaryTriplet: string, count: number, dark: boolean): string[] {
  const primary = parseOklchTriplet(primaryTriplet)
  let baseH = primary?.h ?? 260
  let baseC = primary?.c ?? 0.14
  if (baseC < 0.03) {
    baseH = 260
    baseC = 0.14
  }
  baseC = Math.min(0.2, Math.max(0.1, baseC))
  const lMin = dark ? 0.58 : 0.52
  const lMax = dark ? 0.78 : 0.72
  const colors: string[] = []
  for (let i = 0; i < count; i++) {
    const h = (baseH + i * 137.508) % 360
    // 亮度在区间内交替微调，相邻色区分更稳
    const t = (i % 4) / 3
    const l = lMin + (lMax - lMin) * (0.2 + 0.6 * t)
    colors.push(oklchToHex(l, baseC, h))
  }
  return colors
}
