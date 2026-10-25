<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import StatChartCard from '@/components/StatChartCard/index.vue'
import { recentSessionsOption } from '@/features/stats/charts'
import { useStatsContext } from '@/features/stats/context'
import dayjs from '@/utils/dayjs'

const LIMIT = 50

const { t } = useI18n()
const ctx = useStatsContext()

const recent = computed(() => ctx.sessions.value.slice(-LIMIT))
const option = computed(() => recent.value.length ? recentSessionsOption(recent.value, ctx.theme.value, ctx.fmt, ctx.appName, ctx.colorOf) : null)

function csv() {
  return [
    [t('stats.column.start'), t('stats.column.app'), t('stats.column.minutes')],
    ...recent.value.map(s => [dayjs.unix(s.start).format('YYYY-MM-DD HH:mm:ss'), ctx.appName(s.appId), (s.duration / 60).toFixed(1)]),
  ]
}
</script>

<template>
  <StatChartCard
    :title="t('stats.widget.recentSessions', { n: LIMIT })"
    icon="i-mdi:chart-line"
    :option="option"
    :csv="csv"
  />
</template>
