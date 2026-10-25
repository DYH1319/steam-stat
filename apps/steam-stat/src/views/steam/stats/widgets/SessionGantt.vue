<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import StatChartCard from '@/components/StatChartCard/index.vue'
import { ganttSegments } from '@/features/stats/aggregate'
import { ganttOption } from '@/features/stats/charts'
import { useStatsContext } from '@/features/stats/context'
import { dayKeysBetween } from '@/features/stats/time'
import dayjs from '@/utils/dayjs'

const MAX_DAYS = 31

const { t } = useI18n()
const ctx = useStatsContext()

const days = computed(() => {
  if (ctx.drill.value.day) {
    return [ctx.drill.value.day]
  }
  return dayKeysBetween(ctx.range.value.from, ctx.range.value.to).slice(-MAX_DAYS).reverse()
})

const segments = computed(() => ganttSegments(ctx.sessions.value, days.value, ctx.opts.value))

const option = computed(() => segments.value.length
  ? ganttOption(days.value, segments.value, ctx.theme.value, ctx.fmt, ctx.appName, ctx.colorOf, ctx.opts.value.dayBoundaryHour)
  : null)

const height = computed(() => Math.min(560, Math.max(160, days.value.length * 18 + 40)))

function onClick(params: any) {
  const appId = params?.value?.[3]
  if (typeof appId === 'number') {
    ctx.setDrillApp(appId)
  }
}

function csv() {
  return [
    [t('stats.column.app'), 'AppID', t('stats.column.start'), t('stats.column.end'), t('stats.column.minutes')],
    ...segments.value.map(s => [ctx.appName(s.appId), s.appId, dayjs.unix(s.start).format('YYYY-MM-DD HH:mm:ss'), dayjs.unix(s.end).format('YYYY-MM-DD HH:mm:ss'), ((s.end - s.start) / 60).toFixed(1)]),
  ]
}
</script>

<template>
  <StatChartCard
    :title="t('stats.widget.sessionGantt')"
    icon="i-mdi:chart-timeline"
    :description="t('stats.hint.gantt', { n: MAX_DAYS })"
    :option="option"
    :height="height"
    :csv="csv"
    @chart-click="onClick"
  />
</template>
