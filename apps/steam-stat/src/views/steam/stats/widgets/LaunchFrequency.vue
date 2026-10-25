<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import StatChartCard from '@/components/StatChartCard/index.vue'
import { byApp } from '@/features/stats/aggregate'
import { countOption } from '@/features/stats/charts'
import { useStatsContext } from '@/features/stats/context'

const { t } = useI18n()
const ctx = useStatsContext()

const items = computed(() => byApp(ctx.sessions.value)
  .sort((a, b) => b.count - a.count)
  .slice(0, ctx.topN.value)
  .map(a => ({ appId: a.appId, name: ctx.appName(a.appId), count: a.count })))

const option = computed(() => items.value.length ? countOption(items.value, ctx.theme.value, ctx.colorOf, t('stats.unit.times')) : null)

function onClick(params: any) {
  if (typeof params?.data?.appId === 'number') {
    ctx.setDrillApp(params.data.appId)
  }
}

const csv = () => [[t('stats.column.app'), 'AppID', t('stats.column.sessions')], ...items.value.map(i => [i.name, i.appId, i.count])]
</script>

<template>
  <StatChartCard
    :title="t('stats.widget.launchFrequency', { n: ctx.topN.value })"
    icon="i-mdi:rocket-launch-outline"
    :option="option"
    :csv="csv"
    @chart-click="onClick"
  />
</template>
