<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import StatChartCard from '@/components/StatChartCard/index.vue'
import { hourWeekMatrix } from '@/features/stats/aggregate'
import { hourWeekOption } from '@/features/stats/charts'
import { useStatsContext } from '@/features/stats/context'

const { t } = useI18n()
const ctx = useStatsContext()

const weekdayLabels = computed(() => {
  const sundayFirst = t('stats.weekdaysShortSunday').split(',')
  return ctx.opts.value.weekStart === 1 ? [...sundayFirst.slice(1), sundayFirst[0]] : sundayFirst
})

const matrix = computed(() => hourWeekMatrix(ctx.sessions.value, ctx.opts.value))
const hasData = computed(() => matrix.value.some(row => row.some(v => v > 0)))
const option = computed(() => hasData.value ? hourWeekOption(matrix.value, weekdayLabels.value, ctx.theme.value, ctx.fmt) : null)

function csv() {
  return [
    [t('stats.column.weekday'), ...Array.from({ length: 24 }, (_, h) => `${h}:00`)],
    ...matrix.value.map((row, i) => [weekdayLabels.value[i], ...row.map(v => (v / 3600).toFixed(2))]),
  ]
}
</script>

<template>
  <StatChartCard
    :title="t('stats.widget.hourWeekHeatmap')"
    icon="i-mdi:clock-outline"
    :option="option"
    :height="260"
    :csv="csv"
  />
</template>
