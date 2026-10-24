<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import StatChartCard from '@/components/StatChartCard/index.vue'
import { useChartTheme } from '@/composables/useChartTheme'
import { splitByLogicalDay } from '@/features/stats'
import { useStatsContext } from '@/features/stats/context'
import { useStatsStore } from '@/store/modules/stats'
import dayjs from '@/utils/dayjs'
import { tip, toHours } from './helpers'

defineProps<{ title: string, icon?: string }>()

const { t } = useI18n()
const ctx = useStatsContext()
const statsStore = useStatsStore()
const { theme } = useChartTheme()
const year = ref(dayjs().year())

const byDay = computed(() => splitByLogicalDay(ctx.scoped.value, statsStore.prefs))
const years = computed(() => [...new Set([...byDay.value.keys()].map(k => Number(k.slice(0, 4))))].sort((a, b) => b - a).map(y => ({ value: y, label: String(y) })))
const data = computed(() => [...byDay.value.entries()].filter(([k]) => k.startsWith(String(year.value))).map(([k, s]) => [k, toHours(s)] as [string, number]))
const max = computed(() => data.value.reduce((m, d) => Math.max(m, d[1]), 1))

const option = computed(() => ({
  tooltip: { ...tip(theme.value), formatter: (p: any) => `${p.data[0]} · ${p.data[1]}h` },
  visualMap: { min: 0, max: max.value, inRange: { color: [theme.value.background, theme.value.primary] }, show: false },
  calendar: { range: year.value, cellSize: ['auto', 14], splitLine: { lineStyle: { color: theme.value.border } }, dayLabel: { color: theme.value.mutedText }, monthLabel: { color: theme.value.mutedText }, yearLabel: { show: false } },
  series: [{ type: 'heatmap', coordinateSystem: 'calendar', data: data.value }],
}))
const empty = computed(() => data.value.length === 0)

function csv() {
  return { headers: ['day', 'hours'], rows: data.value.map(d => [d[0], d[1]]) }
}

function onChartClick(p: any) {
  if (p.data?.[0]) {
    ctx.drill.value = { ...ctx.drill.value, day: p.data[0] }
  }
}
</script>

<template>
  <StatChartCard :title="title" :icon="icon" :option="option" :loading="ctx.loading.value" :empty="empty" :empty-text="t('stats.empty')" :csv="csv" @chart-click="onChartClick">
    <template #header>
      <FaSelect v-model="year" :options="years" class="w-28" />
    </template>
    <template #actions>
      <slot name="actions" />
    </template>
  </StatChartCard>
</template>
