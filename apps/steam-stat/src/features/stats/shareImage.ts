import type { ChartTheme } from '@/composables/useChartTheme'

export interface ShareImageModule {
  id: 'summary' | 'topGames' | 'chart' | 'records'
  label: string
}

export interface ShareImageData {
  title: string
  subtitle?: string
  accountName?: string
  stats: { label: string, value: string }[]
  topGames?: { name: string, value: number, color: string }[]
  chart?: { label: string, value: number }[]
  records?: { label: string, value: string }[]
}

export type ShareTemplate = 'long' | 'square' | 'portrait'

export interface ShareImageOptions {
  template: ShareTemplate
  modules: ShareImageModule['id'][]
  hideAccountName: boolean
  hideGameNames: boolean
  theme: ChartTheme
  data: ShareImageData
}

const SIZES: Record<ShareTemplate, { width: number, height?: number }> = {
  long: { width: 1080 },
  square: { width: 1080, height: 1080 },
  portrait: { width: 1080, height: 1920 },
}

/** Canvas 2D 绘制分享图，返回 PNG dataURL（无额外依赖） */
export async function renderShareImage(options: ShareImageOptions): Promise<string> {
  const { template, modules, hideAccountName, hideGameNames, theme, data } = options
  const { width } = SIZES[template]
  const pad = 48
  const contentW = width - pad * 2

  // 计算高度
  let height = pad * 2 + 140
  if (modules.includes('summary')) {
    height += Math.ceil(data.stats.length / 2) * 96
  }
  if (modules.includes('topGames') && data.topGames?.length) {
    height += 60 + data.topGames.length * 52
  }
  if (modules.includes('chart') && data.chart?.length) {
    height += 300
  }
  if (modules.includes('records') && data.records?.length) {
    height += 40 + data.records.length * 48
  }
  if (SIZES[template].height) {
    height = Math.max(height, SIZES[template].height!)
  }

  const canvas = document.createElement('canvas')
  canvas.width = width
  canvas.height = height
  const g = canvas.getContext('2d')!

  // 背景渐变
  const grad = g.createLinearGradient(0, 0, width, height)
  grad.addColorStop(0, theme.primary)
  grad.addColorStop(1, theme.background)
  g.fillStyle = grad
  g.fillRect(0, 0, width, height)

  // 标题
  let y = pad + 56
  g.fillStyle = theme.text
  g.font = 'bold 48px sans-serif'
  g.fillText(data.title, pad, y)
  y += 42
  g.font = '26px sans-serif'
  g.fillStyle = theme.mutedText
  const sub = [data.subtitle, hideAccountName ? null : data.accountName].filter(Boolean).join(' · ')
  if (sub) {
    g.fillText(sub, pad, y)
  }
  y += 56

  if (modules.includes('summary')) {
    const cols = 2
    data.stats.forEach((s, i) => {
      const x = pad + (i % cols) * (contentW / cols)
      const yy = y + Math.floor(i / cols) * 96
      g.fillStyle = theme.mutedText
      g.font = '24px sans-serif'
      g.fillText(s.label, x, yy)
      g.fillStyle = theme.text
      g.font = 'bold 40px sans-serif'
      g.fillText(s.value, x, yy + 44)
    })
    y += Math.ceil(data.stats.length / cols) * 96 + 16
  }

  if (modules.includes('topGames') && data.topGames?.length) {
    const max = Math.max(...data.topGames.map(gm => gm.value), 1)
    data.topGames.forEach((gm, i) => {
      const yy = y + i * 52
      const name = hideGameNames ? `Game ${i + 1}` : gm.name
      g.fillStyle = theme.text
      g.font = '24px sans-serif'
      g.fillText(name.length > 24 ? `${name.slice(0, 24)}…` : name, pad, yy + 26)
      g.fillStyle = gm.color
      g.fillRect(pad + 340, yy + 6, (contentW - 460) * (gm.value / max), 30)
      g.fillStyle = theme.mutedText
      g.fillText(`${Math.round(gm.value / 36) / 100}h`, pad + 350 + (contentW - 460) * (gm.value / max), yy + 28)
    })
    y += data.topGames.length * 52 + 8
  }

  if (modules.includes('chart') && data.chart?.length) {
    const chartH = 220
    const max = Math.max(...data.chart.map(c => c.value), 1)
    const barW = Math.min(60, contentW / data.chart.length - 8)
    const step = contentW / data.chart.length
    data.chart.forEach((c, i) => {
      const h = (c.value / max) * chartH
      g.fillStyle = theme.palette[i % theme.palette.length]
      g.fillRect(pad + i * step + (step - barW) / 2, y + chartH - h, barW, h)
      if (data.chart!.length <= 16) {
        g.fillStyle = theme.mutedText
        g.font = '18px sans-serif'
        g.fillText(c.label, pad + i * step, y + chartH + 24)
      }
    })
    y += chartH + 48
  }

  if (modules.includes('records') && data.records?.length) {
    data.records.forEach((r, i) => {
      const yy = y + i * 48
      g.fillStyle = theme.mutedText
      g.font = '24px sans-serif'
      g.fillText(r.label, pad, yy + 28)
      g.fillStyle = theme.text
      g.font = 'bold 26px sans-serif'
      g.fillText(r.value, pad + 340, yy + 28)
    })
  }

  return canvas.toDataURL('image/png')
}
