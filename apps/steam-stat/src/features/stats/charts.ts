import type { GanttSegment, LifecyclePoint, SeriesResult } from './aggregate'
import type { StatSession } from './model'
import type { ChartTheme } from '@/composables/useChartTheme'
import type { EChartsOption } from '@/utils/echarts'
import dayjs from '@/utils/dayjs'

type Fmt = (seconds: number) => string

export interface ShareItem {
  appId: number
  name: string
  value: number
}

function axisCommon(theme: ChartTheme) {
  return {
    axisLine: { lineStyle: { color: theme.axis } },
    axisLabel: { color: theme.subtext },
    splitLine: { lineStyle: { color: theme.split, type: 'dashed' as const } },
  }
}

function tooltipCommon(theme: ChartTheme) {
  return {
    backgroundColor: theme.background,
    borderColor: theme.axis,
    textStyle: { color: theme.text },
    confine: true,
  }
}

/** 时长占比：饼图 / 柱状图 / 矩形树图 */
export function shareOption(items: ShareItem[], type: 'pie' | 'bar' | 'treemap', theme: ChartTheme, fmt: Fmt, colorOf: (appId: number) => string): EChartsOption {
  const data = items.map(item => ({ name: item.name, value: item.value, appId: item.appId, itemStyle: { color: colorOf(item.appId) } }))
  const total = items.reduce((sum, item) => sum + item.value, 0) || 1
  const tooltip = {
    ...tooltipCommon(theme),
    trigger: 'item' as const,
    formatter: (p: any) => `${p.name}<br/>${fmt(p.value)} (${((p.value / total) * 100).toFixed(1)}%)`,
  }
  if (type === 'bar') {
    return {
      tooltip,
      grid: { left: 8, right: 16, top: 8, bottom: 8, containLabel: true },
      xAxis: { type: 'value', ...axisCommon(theme), axisLabel: { color: theme.subtext, formatter: (v: number) => fmt(v) } },
      yAxis: { type: 'category', inverse: true, data: data.map(d => d.name), ...axisCommon(theme), axisLabel: { color: theme.subtext, width: 140, overflow: 'truncate' } },
      series: [{ type: 'bar', data, barMaxWidth: 22, itemStyle: { borderRadius: [0, 6, 6, 0] } }],
    } as EChartsOption
  }
  if (type === 'treemap') {
    return {
      tooltip,
      series: [{
        type: 'treemap',
        data,
        roam: false,
        nodeClick: false,
        breadcrumb: { show: false },
        label: { color: '#fff', formatter: (p: any) => `${p.name}\n${fmt(p.value)}` },
        itemStyle: { borderColor: theme.background, borderWidth: 2, gapWidth: 2 },
      }],
    } as EChartsOption
  }
  return {
    tooltip,
    legend: { type: 'scroll', orient: 'vertical', right: 0, top: 'middle', textStyle: { color: theme.text }, pageTextStyle: { color: theme.subtext } },
    series: [{
      type: 'pie',
      radius: ['42%', '72%'],
      center: ['35%', '50%'],
      itemStyle: { borderColor: theme.background, borderWidth: 2, borderRadius: 6 },
      label: { show: false },
      emphasis: { label: { show: true, fontWeight: 'bold', color: theme.text, formatter: (p: any) => `${p.name}\n${fmt(p.value)}` } },
      data,
    }],
  } as EChartsOption
}

/** 时长趋势：按应用堆叠的柱状图，长区间带 dataZoom */
export function trendOption(
  series: SeriesResult,
  splitApps: number[],
  theme: ChartTheme,
  fmt: Fmt,
  appName: (appId: number) => string,
  colorOf: (appId: number) => string,
  otherLabel: string,
  labelFormatter: (key: string) => string = key => key,
): EChartsOption {
  const other = series.values.map((total, i) => total - splitApps.reduce((sum, appId) => sum + (series.byApp.get(appId)?.[i] ?? 0), 0))
  const bars = [
    ...splitApps.map(appId => ({
      name: appName(appId),
      type: 'bar' as const,
      stack: 'total',
      data: series.byApp.get(appId) ?? [],
      itemStyle: { color: colorOf(appId) },
      barMaxWidth: 28,
    })),
    {
      name: otherLabel,
      type: 'bar' as const,
      stack: 'total',
      data: other.map(v => Math.max(0, v)),
      itemStyle: { color: theme.dark ? '#52525b' : '#d4d4d8', borderRadius: [4, 4, 0, 0] },
      barMaxWidth: 28,
    },
  ]
  return {
    tooltip: {
      ...tooltipCommon(theme),
      trigger: 'axis',
      axisPointer: { type: 'shadow' },
      formatter: (params: any) => {
        const list = (Array.isArray(params) ? params : [params]).filter((p: any) => p.value > 0)
        const total = list.reduce((sum: number, p: any) => sum + p.value, 0)
        return [`<b>${labelFormatter(params[0]?.axisValue ?? '')}</b> · ${fmt(total)}`, ...list.map((p: any) => `${p.marker}${p.seriesName}: ${fmt(p.value)}`)].join('<br/>')
      },
    },
    legend: { type: 'scroll', bottom: 0, textStyle: { color: theme.text }, pageTextStyle: { color: theme.subtext } },
    grid: { left: 8, right: 16, top: 16, bottom: series.keys.length > 60 ? 64 : 36, containLabel: true },
    xAxis: { type: 'category', data: series.keys, ...axisCommon(theme), axisLabel: { color: theme.subtext, formatter: labelFormatter } },
    yAxis: { type: 'value', ...axisCommon(theme), axisLabel: { color: theme.subtext, formatter: (v: number) => fmt(v) } },
    dataZoom: series.keys.length > 60
      ? [{ type: 'inside' }, { type: 'slider', height: 16, bottom: 28, borderColor: theme.axis, textStyle: { color: theme.subtext } }]
      : [],
    series: bars,
  } as EChartsOption
}

/** 启动次数 TOP N */
export function countOption(items: { appId: number, name: string, count: number }[], theme: ChartTheme, colorOf: (appId: number) => string, unit: string): EChartsOption {
  return {
    tooltip: { ...tooltipCommon(theme), trigger: 'item', formatter: (p: any) => `${p.name}<br/>${p.value} ${unit}` },
    grid: { left: 8, right: 24, top: 8, bottom: 8, containLabel: true },
    xAxis: { type: 'value', minInterval: 1, ...axisCommon(theme) },
    yAxis: { type: 'category', inverse: true, data: items.map(i => i.name), ...axisCommon(theme), axisLabel: { color: theme.subtext, width: 140, overflow: 'truncate' } },
    series: [{
      type: 'bar',
      barMaxWidth: 20,
      label: { show: true, position: 'right', color: theme.subtext },
      data: items.map(i => ({ value: i.count, appId: i.appId, itemStyle: { color: colorOf(i.appId), borderRadius: [0, 6, 6, 0] } })),
    }],
  } as EChartsOption
}

/** GitHub 式年度热力图 */
export function yearHeatmapOption(year: number, entries: [string, number][], theme: ChartTheme, fmt: Fmt, firstDay: 0 | 1, dayLabels: string[], monthLabels: string[]): EChartsOption {
  const max = Math.max(3600, ...entries.map(([, v]) => v))
  return {
    tooltip: { ...tooltipCommon(theme), formatter: (p: any) => `${p.value[0]}<br/>${fmt(p.value[1])}` },
    visualMap: {
      min: 0,
      max,
      show: false,
      inRange: { color: theme.sequential(6) },
    },
    calendar: {
      top: 24,
      left: 36,
      right: 8,
      bottom: 4,
      cellSize: ['auto', 14],
      range: String(year),
      firstDay,
      itemStyle: { color: theme.dark ? '#27272a' : '#f4f4f5', borderColor: theme.background, borderWidth: 3 },
      splitLine: { show: false },
      yearLabel: { show: false },
      dayLabel: { color: theme.subtext, nameMap: dayLabels },
      monthLabel: { color: theme.subtext, nameMap: monthLabels },
    },
    series: [{ type: 'heatmap', coordinateSystem: 'calendar', data: entries.filter(([, v]) => v > 0) }],
  } as EChartsOption
}

/** 24 小时 × 星期 作息热力图 */
export function hourWeekOption(matrix: number[][], weekdayLabels: string[], theme: ChartTheme, fmt: Fmt): EChartsOption {
  const data: [number, number, number][] = []
  matrix.forEach((row, weekday) => row.forEach((seconds, hour) => data.push([hour, weekday, seconds])))
  const max = Math.max(60, ...data.map(d => d[2]))
  return {
    tooltip: { ...tooltipCommon(theme), formatter: (p: any) => `${weekdayLabels[p.value[1]]} ${String(p.value[0]).padStart(2, '0')}:00<br/>${fmt(p.value[2])}` },
    grid: { left: 8, right: 8, top: 8, bottom: 8, containLabel: true },
    xAxis: { type: 'category', data: Array.from({ length: 24 }, (_, h) => String(h).padStart(2, '0')), splitArea: { show: false }, ...axisCommon(theme) },
    yAxis: { type: 'category', data: weekdayLabels, inverse: true, ...axisCommon(theme) },
    visualMap: { min: 0, max, show: false, inRange: { color: theme.sequential(6) } },
    series: [{ type: 'heatmap', data, itemStyle: { borderColor: theme.background, borderWidth: 2, borderRadius: 3 } }],
  } as EChartsOption
}

/** 会话甘特图：每天一行，每段游玩一个色块 */
export function ganttOption(
  days: string[],
  segments: GanttSegment[],
  theme: ChartTheme,
  fmt: Fmt,
  appName: (appId: number) => string,
  colorOf: (appId: number) => string,
  boundaryHour: number,
): EChartsOption {
  const index = new Map(days.map((day, i) => [day, i]))
  const data = segments.map(s => ({
    value: [index.get(s.day) ?? 0, s.from, s.to, s.appId, s.start, s.end],
    itemStyle: { color: colorOf(s.appId) },
  }))
  return {
    tooltip: {
      ...tooltipCommon(theme),
      formatter: (p: any) => {
        const [, , , appId, start, end] = p.value
        return `<b>${appName(appId)}</b><br/>${dayjs.unix(start).format('MM-DD HH:mm')} ~ ${dayjs.unix(end).format('HH:mm')}<br/>${fmt(end - start)}`
      },
    },
    grid: { left: 8, right: 16, top: 8, bottom: 8, containLabel: true },
    xAxis: {
      type: 'value',
      min: 0,
      max: 86400,
      interval: 3 * 3600,
      ...axisCommon(theme),
      axisLabel: { color: theme.subtext, formatter: (v: number) => `${String((Math.floor(v / 3600) + boundaryHour) % 24).padStart(2, '0')}:00` },
    },
    yAxis: { type: 'category', data: days.map(day => day.slice(5)), inverse: true, ...axisCommon(theme) },
    dataZoom: days.length > 14 ? [{ type: 'inside', yAxisIndex: 0 }, { type: 'slider', yAxisIndex: 0, width: 12, right: 0 }] : [],
    series: [{
      type: 'custom',
      encode: { x: [1, 2], y: 0 },
      data,
      renderItem: (_params: any, api: any) => {
        const row = api.value(0)
        const start = api.coord([api.value(1), row])
        const end = api.coord([api.value(2), row])
        const height = Math.max(4, api.size([0, 1])[1] * 0.6)
        return {
          type: 'rect',
          shape: { x: start[0], y: start[1] - height / 2, width: Math.max(2, end[0] - start[0]), height, r: 3 },
          style: { fill: api.visual('color') },
        }
      },
    }],
  } as EChartsOption
}

/** 游戏生命周期：累计时长曲线，可按「首次启动后天数」叠加对比 */
export function lifecycleOption(
  series: { appId: number, points: LifecyclePoint[] }[],
  relative: boolean,
  theme: ChartTheme,
  fmt: Fmt,
  appName: (appId: number) => string,
  colorOf: (appId: number) => string,
  dayUnit: string,
): EChartsOption {
  return {
    tooltip: {
      ...tooltipCommon(theme),
      trigger: 'axis',
      formatter: (params: any) => (Array.isArray(params) ? params : [params])
        .map((p: any) => `${p.marker}${p.seriesName}: ${fmt(p.value[1])}`)
        .join('<br/>'),
    },
    legend: { type: 'scroll', bottom: 0, textStyle: { color: theme.text } },
    grid: { left: 8, right: 16, top: 16, bottom: 36, containLabel: true },
    xAxis: relative
      ? { type: 'value', ...axisCommon(theme), axisLabel: { color: theme.subtext, formatter: (v: number) => `${v.toFixed(0)}${dayUnit}` } }
      : { type: 'time', ...axisCommon(theme) },
    yAxis: { type: 'value', ...axisCommon(theme), axisLabel: { color: theme.subtext, formatter: (v: number) => fmt(v) } },
    dataZoom: [{ type: 'inside' }],
    series: series.map(item => ({
      name: appName(item.appId),
      type: 'line',
      step: 'end',
      showSymbol: false,
      lineStyle: { width: 2, color: colorOf(item.appId) },
      itemStyle: { color: colorOf(item.appId) },
      areaStyle: series.length === 1 ? { color: colorOf(item.appId), opacity: 0.15 } : undefined,
      data: item.points.map(p => [relative ? p.dayOffset : p.ts * 1000, p.seconds]),
    })),
  } as EChartsOption
}

/** 最近 N 次会话时长 */
export function recentSessionsOption(sessions: StatSession[], theme: ChartTheme, fmt: Fmt, appName: (appId: number) => string, colorOf: (appId: number) => string): EChartsOption {
  return {
    tooltip: {
      ...tooltipCommon(theme),
      trigger: 'axis',
      formatter: (params: any) => {
        const s = sessions[params[0].dataIndex]
        return `${dayjs.unix(s.start).format('YYYY-MM-DD HH:mm')}<br/>${appName(s.appId)}<br/>${fmt(s.duration)}`
      },
    },
    grid: { left: 8, right: 16, top: 16, bottom: 8, containLabel: true },
    xAxis: { type: 'category', data: sessions.map(s => dayjs.unix(s.start).format('MM/DD HH:mm')), ...axisCommon(theme) },
    yAxis: { type: 'value', ...axisCommon(theme), axisLabel: { color: theme.subtext, formatter: (v: number) => fmt(v) } },
    series: [{
      type: 'line',
      smooth: true,
      symbolSize: 6,
      lineStyle: { width: 2, color: theme.palette(1)[0] },
      areaStyle: { color: theme.palette(1)[0], opacity: 0.12 },
      data: sessions.map(s => ({ value: s.duration, itemStyle: { color: colorOf(s.appId) } })),
    }],
  } as EChartsOption
}

/** 横向柱状：按名称汇总时长 */
export function namedBarOption(items: { name: string, seconds: number }[], theme: ChartTheme, fmt: Fmt): EChartsOption {
  const colors = theme.palette(items.length)
  return {
    tooltip: { ...tooltipCommon(theme), trigger: 'item', formatter: (p: any) => `${p.name}<br/>${fmt(p.value)}` },
    grid: { left: 8, right: 24, top: 8, bottom: 8, containLabel: true },
    xAxis: { type: 'value', ...axisCommon(theme), axisLabel: { color: theme.subtext, formatter: (v: number) => fmt(v) } },
    yAxis: { type: 'category', inverse: true, data: items.map(i => i.name), ...axisCommon(theme), axisLabel: { color: theme.subtext, width: 140, overflow: 'truncate' } },
    series: [{ type: 'bar', barMaxWidth: 20, data: items.map((i, index) => ({ value: i.seconds, itemStyle: { color: colors[index], borderRadius: [0, 6, 6, 0] } })) }],
  } as EChartsOption
}

/** 简单分布柱状图（小时 / 星期 / 每日） */
export function distributionOption(labels: string[], values: number[], theme: ChartTheme, fmt: Fmt): EChartsOption {
  return {
    tooltip: { ...tooltipCommon(theme), trigger: 'axis', formatter: (p: any) => `${p[0].axisValue}<br/>${fmt(p[0].value)}` },
    grid: { left: 8, right: 8, top: 16, bottom: 8, containLabel: true },
    xAxis: { type: 'category', data: labels, ...axisCommon(theme) },
    yAxis: { type: 'value', ...axisCommon(theme), axisLabel: { color: theme.subtext, formatter: (v: number) => fmt(v) } },
    series: [{
      type: 'bar',
      barMaxWidth: 24,
      data: values,
      itemStyle: { color: theme.palette(1)[0], borderRadius: [6, 6, 0, 0] },
    }],
  } as EChartsOption
}
