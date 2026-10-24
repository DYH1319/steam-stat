<script setup lang="ts">
import type { GanttSegment } from '@/features/stats'
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import StatChartCard from '@/components/StatChartCard/index.vue'
import { useChartTheme } from '@/composables/useChartTheme'
import { ganttRows } from '@/features/stats'
import { useStatsContext } from '@/features/stats/context'
import { useStatsStore } from '@/store/modules/stats'
import dayjs from '@/utils/dayjs'
import { tip } from './helpers'

defineProps<{ title: string, icon?: string }>()

const { t } = useI18n()
const ctx = useStatsContext()
const statsStore = useStatsStore()
const { theme } = useChartTheme()

const rows = computed(() => {
  const prefs = statsStore.prefs
  const toKey = statsStore.filters.dateRange?.[1] ?? dayjs().format('YYYY-MM-DD')
  let fromKey = toKey
  for (let i = 0; i < 29; i++) {
    fromKey = dayjs(fromKey, 'YYYY-MM-DD').subtract(1, 'day').format('YYYY-MM-DD')
  }
  return ganttRows(ctx.scoped.value, fromKey, toKey, prefs).reverse()
})

// 每段一条数据：[startHour, endHour, rowIndex, appId, realStart, realEnd]
const data = computed(() => rows.value.flatMap((row, y) =>
  row.segments.map((s: GanttSegment) => [s.startOffset / 3600, s.endOffset / 3600, y, s.appId, s.realStart, s.realEnd]),
))

const option = computed(() => ({
  tooltip: {
    ...tip(theme.value),
    formatter: (p: any) => {
      const d = p.data
      return `${ctx.appName(d[3])} · ${dayjs.unix(d[4]).format('HH:mm')}–${dayjs.unix(d[5]).format('HH:mm')}`
    },
  },
  grid: { left: 90, right: 16, top: 16, bottom: 28 },
  xAxis: { type: 'value', min: 0, max: 24, interval: 4, axisLabel: { color: theme.value.mutedText, formatter: (v: number) => `${(v + statsStore.prefs.dayStartHour) % 24}h` }, axisLine: { lineStyle: { color: theme.value.border } } },
  yAxis: { type: 'category', data: rows.value.map(r => r.day.slice(5)), axisLabel: { color: theme.value.mutedText }, axisLine: { lineStyle: { color: theme.value.border } } },
  dataZoom: rows.value.length > 14 ? [{ type: 'inside', yAxisIndex: 0 }] : [],
  series: [{
    type: 'custom',
    renderItem: (_p: any, api: any) => {
      const start = api.value(0)
      const end = api.value(1)
      const y = api.value(2)
      const [x1, cy] = api.coord([start, y])
      const [x2] = api.coord([end, y])
      const height = api.size([0, 1])[1] * 0.6
      return {
        type: 'rect',
        shape: { x: x1, y: cy - height / 2, width: Math.max(2, x2 - x1), height },
        style: { fill: ctx.appColor(api.value(3)) },
      }
    },
    data: data.value,
  }],
}))
const empty = computed(() => data.value.length === 0)

function csv() {
  return {
    headers: ['day', 'app', 'start', 'end'],
    rows: data.value.map(d => [rows.value[d[2]]?.day, ctx.appName(d[3]), dayjs.unix(d[4]).format('YYYY-MM-DD HH:mm'), dayjs.unix(d[5]).format('YYYY-MM-DD HH:mm')]),
  }
}
</script>

<template>
  <StatChartCard :title="title" :icon="icon" :option="option" :loading="ctx.loading.value" :empty="empty" :empty-text="t('stats.empty')" :csv="csv">
    <template #actions>
      <slot name="actions" />
    </template>
  </StatChartCard>
</template>
