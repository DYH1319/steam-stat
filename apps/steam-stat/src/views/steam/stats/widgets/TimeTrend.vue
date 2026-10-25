<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import StatChartCard from '@/components/StatChartCard/index.vue'
import { byApp, timeSeries } from '@/features/stats/aggregate'
import { trendOption } from '@/features/stats/charts'
import { useStatsContext } from '@/features/stats/context'
import { useStatsStore } from '@/store/modules/stats'

const { t } = useI18n()
const ctx = useStatsContext()
const store = useStatsStore()

const granularityOptions = computed(() => (['hour', 'day', 'week', 'month'] as const).map(value => ({ value, label: t(`stats.granularity.${value}`) })))

const effectiveGranularity = computed(() => {
  // 小时粒度仅在 ≤ 14 天时可用，避免数据点过多
  const days = ctx.drill.value.day ? 1 : Math.round((Date.parse(ctx.range.value.to) - Date.parse(ctx.range.value.from)) / 86400000) + 1
  return store.granularity === 'hour' && days > 14 ? 'day' : store.granularity
})

const range = computed(() => ctx.drill.value.day ? { from: ctx.drill.value.day, to: ctx.drill.value.day } : ctx.range.value)

const series = computed(() => {
  const sessions = ctx.sessions.value
  const splitApps = byApp(sessions).slice(0, 5).map(a => a.appId)
  return { splitApps, result: timeSeries(sessions, range.value.from, range.value.to, effectiveGranularity.value, ctx.opts.value, splitApps) }
})

function label(key: string) {
  if (effectiveGranularity.value === 'hour') {
    return `${key.slice(5)}:00`
  }
  if (effectiveGranularity.value === 'week') {
    return t('stats.weekOf', { date: key.slice(5) })
  }
  return effectiveGranularity.value === 'month' ? key : key.slice(5)
}

const option = computed(() => series.value.result.values.some(v => v > 0)
  ? trendOption(series.value.result, series.value.splitApps, ctx.theme.value, ctx.fmt, ctx.appName, ctx.colorOf, t('stats.other'), label)
  : null)

function onClick(params: any) {
  if (effectiveGranularity.value === 'day' && typeof params?.name === 'string') {
    ctx.setDrillDay(params.name)
  }
}

const csv = () => [[t('stats.column.period'), t('stats.column.hours')], ...series.value.result.keys.map((key, i) => [key, (series.value.result.values[i] / 3600).toFixed(2)])]
</script>

<template>
  <StatChartCard
    :title="t('stats.widget.timeTrend')"
    icon="i-mdi:chart-bar"
    :description="effectiveGranularity === 'day' ? t('stats.hint.clickDayToDrill') : undefined"
    :option="option"
    :csv="csv"
    @chart-click="onClick"
  >
    <template #actions>
      <FaSelect v-model="store.granularity" :options="granularityOptions" class="w-24" />
    </template>
  </StatChartCard>
</template>
