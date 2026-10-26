/**
 * 分享图：纯 Canvas 绘制（不加载外部图片，避免跨域污染导致无法导出）。
 */
export type ShareTemplate = 'long' | 'square' | 'portrait'
export type ShareModule = 'summary' | 'topGames' | 'highlights' | 'routine'

export interface ShareModel {
  title: string
  subtitle: string
  account?: string
  summary: { label: string, value: string }[]
  topGames: { name: string, value: string, ratio: number }[]
  highlights: { label: string, value: string, hint?: string }[]
  /** 24 小时分布（0~1） */
  hours: number[]
  footer: string
}

export interface ShareOptions {
  template: ShareTemplate
  modules: ShareModule[]
  maskAccount: boolean
  maskGames: boolean
  gradient: [string, string]
  accent: string
  labels: { topGames: string, highlights: string, routine: string, maskedGame: string }
}

const SIZES: Record<ShareTemplate, { width: number, height: number | null }> = {
  long: { width: 1080, height: null },
  square: { width: 1080, height: 1080 },
  portrait: { width: 1080, height: 1920 },
}

const FONT = '"PingFang SC", "Microsoft YaHei", "Segoe UI", sans-serif'

function roundRect(ctx: CanvasRenderingContext2D, x: number, y: number, w: number, h: number, r: number) {
  ctx.beginPath()
  ctx.moveTo(x + r, y)
  ctx.arcTo(x + w, y, x + w, y + h, r)
  ctx.arcTo(x + w, y + h, x, y + h, r)
  ctx.arcTo(x, y + h, x, y, r)
  ctx.arcTo(x, y, x + w, y, r)
  ctx.closePath()
}

function ellipsis(ctx: CanvasRenderingContext2D, text: string, maxWidth: number): string {
  if (ctx.measureText(text).width <= maxWidth) {
    return text
  }
  let result = text
  while (result.length > 1 && ctx.measureText(`${result}…`).width > maxWidth) {
    result = result.slice(0, -1)
  }
  return `${result}…`
}

/** 计算各模块高度并绘制；返回画布 */
export function renderShareImage(model: ShareModel, options: ShareOptions, canvas = document.createElement('canvas')): HTMLCanvasElement {
  const size = SIZES[options.template]
  const compact = options.template === 'square'
  const pad = 72
  const width = size.width
  const inner = width - pad * 2
  const topGames = model.topGames.slice(0, compact ? 3 : 5)
  const highlights = model.highlights.slice(0, compact ? 2 : 4)

  const blocks: { module: ShareModule, height: number }[] = options.modules
    .filter(module => module !== 'topGames' || topGames.length)
    .filter(module => module !== 'highlights' || highlights.length)
    .map(module => ({
      module,
      height: module === 'summary'
        ? Math.ceil(model.summary.length / 2) * 130
        : module === 'topGames'
          ? 70 + topGames.length * 78
          : module === 'highlights'
            ? 70 + Math.ceil(highlights.length / 2) * 150
            : 260,
    }))
  const headerHeight = 230
  const footerHeight = 90
  const gap = 36
  const contentHeight = blocks.reduce((sum, block) => sum + block.height + gap, 0)
  const height = size.height ?? headerHeight + contentHeight + footerHeight + pad
  const scale = size.height ? Math.min(1, (size.height - headerHeight - footerHeight - pad) / Math.max(1, contentHeight)) : 1

  canvas.width = width
  canvas.height = height
  const ctx = canvas.getContext('2d')!

  const bg = ctx.createLinearGradient(0, 0, width, height)
  bg.addColorStop(0, options.gradient[0])
  bg.addColorStop(1, options.gradient[1])
  ctx.fillStyle = bg
  ctx.fillRect(0, 0, width, height)
  ctx.fillStyle = 'rgba(255,255,255,0.08)'
  ctx.beginPath()
  ctx.arc(width - 120, 120, 260, 0, Math.PI * 2)
  ctx.fill()

  ctx.fillStyle = '#fff'
  ctx.textBaseline = 'top'
  ctx.font = `600 30px ${FONT}`
  ctx.globalAlpha = 0.85
  ctx.fillText(model.subtitle, pad, pad)
  ctx.globalAlpha = 1
  ctx.font = `bold 64px ${FONT}`
  ctx.fillText(ellipsis(ctx, model.title, inner), pad, pad + 44)
  if (model.account) {
    ctx.font = `28px ${FONT}`
    ctx.globalAlpha = 0.8
    ctx.fillText(options.maskAccount ? '••••••' : model.account, pad, pad + 124)
    ctx.globalAlpha = 1
  }

  let y = headerHeight
  ctx.save()
  ctx.translate(pad, y)
  ctx.scale(scale, scale)
  y = 0
  const w = inner / scale
  for (const block of blocks) {
    ctx.fillStyle = 'rgba(255,255,255,0.14)'
    roundRect(ctx, 0, y, w, block.height, 28)
    ctx.fill()
    ctx.fillStyle = '#fff'
    if (block.module === 'summary') {
      model.summary.forEach((item, i) => {
        const x = (i % 2) * (w / 2) + 36
        const top = y + Math.floor(i / 2) * 130 + 28
        ctx.font = `26px ${FONT}`
        ctx.globalAlpha = 0.8
        ctx.fillText(item.label, x, top)
        ctx.globalAlpha = 1
        ctx.font = `bold 52px ${FONT}`
        ctx.fillText(ellipsis(ctx, item.value, w / 2 - 60), x, top + 38)
      })
    }
    else if (block.module === 'topGames') {
      ctx.font = `bold 32px ${FONT}`
      ctx.fillText(options.labels.topGames, 36, y + 24)
      topGames.forEach((game, i) => {
        const top = y + 76 + i * 78
        const label = options.maskGames ? `${options.labels.maskedGame} ${i + 1}` : game.name
        ctx.font = `28px ${FONT}`
        ctx.fillText(ellipsis(ctx, `${i + 1}. ${label}`, w * 0.6), 36, top)
        ctx.textAlign = 'right'
        ctx.fillText(game.value, w - 36, top)
        ctx.textAlign = 'left'
        ctx.fillStyle = 'rgba(255,255,255,0.22)'
        roundRect(ctx, 36, top + 40, w - 72, 12, 6)
        ctx.fill()
        ctx.fillStyle = options.accent
        roundRect(ctx, 36, top + 40, Math.max(12, (w - 72) * game.ratio), 12, 6)
        ctx.fill()
        ctx.fillStyle = '#fff'
      })
    }
    else if (block.module === 'highlights') {
      ctx.font = `bold 32px ${FONT}`
      ctx.fillText(options.labels.highlights, 36, y + 24)
      highlights.forEach((item, i) => {
        const x = (i % 2) * (w / 2) + 36
        const top = y + 80 + Math.floor(i / 2) * 150
        ctx.font = `24px ${FONT}`
        ctx.globalAlpha = 0.8
        ctx.fillText(item.label, x, top)
        ctx.globalAlpha = 1
        ctx.font = `bold 42px ${FONT}`
        ctx.fillText(ellipsis(ctx, item.value, w / 2 - 60), x, top + 34)
        if (item.hint) {
          ctx.font = `22px ${FONT}`
          ctx.globalAlpha = 0.75
          ctx.fillText(ellipsis(ctx, options.maskGames ? '' : item.hint, w / 2 - 60), x, top + 88)
          ctx.globalAlpha = 1
        }
      })
    }
    else {
      ctx.font = `bold 32px ${FONT}`
      ctx.fillText(options.labels.routine, 36, y + 24)
      const barWidth = (w - 72) / 24
      model.hours.forEach((ratio, hour) => {
        const barHeight = Math.max(4, ratio * 130)
        ctx.fillStyle = ratio > 0 ? options.accent : 'rgba(255,255,255,0.25)'
        roundRect(ctx, 36 + hour * barWidth + 3, y + 210 - barHeight, barWidth - 6, barHeight, 4)
        ctx.fill()
      })
      ctx.fillStyle = '#fff'
      ctx.font = `20px ${FONT}`
      for (const hour of [0, 6, 12, 18]) {
        ctx.fillText(`${hour}:00`, 36 + hour * barWidth, y + 222)
      }
    }
    y += block.height + gap
  }
  ctx.restore()

  ctx.fillStyle = '#fff'
  ctx.globalAlpha = 0.75
  ctx.font = `24px ${FONT}`
  ctx.fillText(model.footer, pad, height - footerHeight)
  ctx.globalAlpha = 1
  return canvas
}
