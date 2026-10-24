<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import StatChartCard from '@/components/StatChartCard/index.vue'
import { useChartTheme } from '@/composables/useChartTheme'
import { hourWeekMatrix } from '@/features/stats'
import { useStatsContext } from '@/features/stats/context'
import { useStatsStore } from '@/store/modules/stats'
import { tip, toHours } from './helpers'

defineProps<{ title: string, icon?: string }>()

const { t } = useI18n()
const ctx = useStatsContext()
const statsStore = useStatsStore()
const { theme } = useChartTheme()

const matrix = computed(() => hourWeekMatrix(ctx.drilled.value.map(p => p.session), statsStore.prefs.weekStartsOn))
const max = computed(() => matrix.value.reduce((m, row) => row.reduce((x, v) => Math.max(x, v), m), 1))
const days = computed(() => statsStore.prefs.weekStartsOn === 1
  ? ['一', '二', '三', '四', '五', '六', '日']
  : ['日', '一', '二', '三', '四', '五', '六'])

const option = computed(() => ({
  tooltip: { ...tip(theme.value), formatter: (p: any) => `${days.value[p.data[1]]} ${p.data[0]}:00 · ${toHours(p.data[2])}h` },
  grid: { left: 40, right: 16, top: 16, bottom: 40 },
  xAxis: { type: 'category', data: Array.from({ length: 24 }, (_, i) => `${i}`), axisLabel: { color: theme.value.mutedText }, axisLine: { lineStyle: { color: theme.value.border } } },
  yAxis: { type: 'category', data: days.value, axisLabel: { color: theme.value.mutedText }, axisLine: { lineStyle: { color: theme.value.border } } },
  visualMap: { min: 0, max: max.value, inRange: { color: [theme.value.background, theme.value.primary] }, show: false },
  series: [{ type: 'heatmap', data: matrix.value.flatMap((row, y) => row.map((s, x) => [x, y, s])) }],
}))
const empty = computed(() => ctx.drilled.value.length === 0)

function csv() {
  const rows: (string | number)[][] = []
  matrix.value.forEach((row, y) => row.forEach((s, x) => rows.push([days.value[y], x, toHours(s)])))
  return { headers: ['day', 'hour', 'hours'], rows }
}
</script>

<template>
  <StatChartCard :title="title" :icon="icon" :option="option" :loading="ctx.loading.value" :empty="empty" :empty-text="t('stats.empty')" :csv="csv">
    <template #actions>
      <slot name="actions" />
    </template>
  </StatChartCard>
</template>
