import type { ChartTheme } from '@/composables/useChartTheme'

export function axis(t: ChartTheme) {
  return {
    axisLine: { lineStyle: { color: t.border } },
    axisLabel: { color: t.mutedText },
    splitLine: { lineStyle: { color: t.border, opacity: 0.4 } },
  }
}

export function tip(t: ChartTheme, trigger = 'item') {
  return {
    trigger,
    backgroundColor: t.background,
    borderColor: t.border,
    textStyle: { color: t.text },
  }
}

export function toHours(seconds: number): number {
  return Math.round((seconds / 3600) * 10) / 10
}

export function shareOption(
  t: ChartTheme,
  type: string,
  items: { name: string, value: number, color?: string }[],
  onClick?: (index: number) => void,
) {
  const named = items.map(i => ({ name: i.name, value: toHours(i.value), itemStyle: i.color ? { color: i.color } : undefined }))
  const option: any = {
    tooltip: tip(t),
    ...(type === 'pie'
      ? { series: [{ type: 'pie', radius: ['35%', '70%'], data: named, label: { color: t.text } }] }
      : type === 'treemap'
        ? { series: [{ type: 'treemap', roam: false, data: named, label: { color: '#fff' } }] }
        : {
            grid: { left: 40, right: 16, top: 16, bottom: 28 },
            xAxis: { type: 'category', data: items.map(i => i.name), ...axis(t) },
            yAxis: { type: 'value', ...axis(t) },
            series: [{ type: 'bar', data: named }],
          }),
  }
  return { option, onClick: onClick ? (p: any) => onClick(p.dataIndex) : undefined }
}
