<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import StatChartCard from '@/components/StatChartCard/index.vue'
import { useChartTheme } from '@/composables/useChartTheme'
import { bucketSeries, logicalDayKey, logicalDayStart, nextDayKey } from '@/features/stats'
import { useStatsContext } from '@/features/stats/context'
import { useStatsStore } from '@/store/modules/stats'
import dayjs from '@/utils/dayjs'
import { axis, tip, toHours } from './helpers'

const props = defineProps<{ title: string, icon?: string, defaultGranularity?: 'hour' | 'day' | 'week' | 'month' }>()

const { t } = useI18n()
const ctx = useStatsContext()
const statsStore = useStatsStore()
const { theme } = useChartTheme()

const localGranularity = ref(props.defaultGranularity ?? 'day')
const granularity = computed<'hour' | 'day' | 'week' | 'month'>({
  get: () => props.defaultGranularity ? localGranularity.value : statsStore.prefs.granularity,
  set: v => props.defaultGranularity ? (localGranularity.value = v) : (statsStore.prefs.granularity = v),
})
const granularityOptions = ['hour', 'day', 'week', 'month'].map(g => ({ value: g, label: t(`stats.granularity_${g}`) }))

const range = computed<[number, number]>(() => {
  const r = statsStore.filters.dateRange
  const prefs = statsStore.prefs
  if (r) {
    return [logicalDayStart(r[0], prefs), logicalDayStart(nextDayKey(r[1]), prefs)]
  }
  const min = ctx.allSessions.value.reduce((m, s) => Math.min(m, s.startTime), Number.MAX_SAFE_INTEGER)
  const from = min === Number.MAX_SAFE_INTEGER ? dayjs().unix() - 86400 : logicalDayStart(logicalDayKey(min, prefs), prefs)
  return [from, dayjs().unix() + 3600]
})

const buckets = computed(() => bucketSeries(ctx.rangeScoped.value.map(p => p.session), granularity.value, range.value[0], range.value[1], statsStore.prefs))

const option = computed(() => ({
  tooltip: { ...tip(theme.value, 'axis') },
  grid: { left: 40, right: 16, top: 16, bottom: buckets.value.length > 31 ? 60 : 28 },
  xAxis: { type: 'category', data: buckets.value.map(b => b.label), ...axis(theme.value) },
  yAxis: { type: 'value', name: 'h', ...axis(theme.value) },
  dataZoom: buckets.value.length > 31 ? [{ type: 'inside' }, { type: 'slider' }] : [],
  series: [{ type: 'bar', data: buckets.value.map(b => toHours(b.seconds)), itemStyle: { color: theme.value.primary } }],
}))
const empty = computed(() => buckets.value.every(b => b.seconds === 0))

function csv() {
  return { headers: [t('stats.colPeriod'), 'hours'], rows: buckets.value.map(b => [b.key, toHours(b.seconds)]) }
}

function onChartClick(p: any) {
  const b = buckets.value[p.dataIndex]
  if (!b) {
    return
  }
  ctx.drill.value = { ...ctx.drill.value, day: b.key.length === 10 ? b.key : logicalDayKey(b.start, statsStore.prefs) }
}
</script>

<template>
  <StatChartCard :title="title" :icon="icon" :option="option" :loading="ctx.loading.value" :empty="empty" :empty-text="t('stats.empty')" :csv="csv" @chart-click="onChartClick">
    <template #header>
      <FaSelect v-model="granularity" :options="granularityOptions" class="w-24" />
    </template>
    <template #actions>
      <slot name="actions" />
    </template>
  </StatChartCard>
</template>
