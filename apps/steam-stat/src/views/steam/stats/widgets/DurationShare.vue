<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import StatChartCard from '@/components/StatChartCard/index.vue'
import { byApp } from '@/features/stats/aggregate'
import { shareOption } from '@/features/stats/charts'
import { useStatsContext } from '@/features/stats/context'

const { t } = useI18n()
const ctx = useStatsContext()
const type = ref<'pie' | 'bar' | 'treemap'>('pie')

const items = computed(() => {
  const list = byApp(ctx.drill.value.day ? ctx.sessions.value : ctx.base.value)
  const top = list.slice(0, ctx.topN.value).map(a => ({ appId: a.appId, name: ctx.appName(a.appId), value: a.seconds }))
  const rest = list.slice(ctx.topN.value).reduce((sum, a) => sum + a.seconds, 0)
  return rest > 0 ? [...top, { appId: -1, name: t('stats.other'), value: rest }] : top
})

const option = computed(() => items.value.length
  ? shareOption(items.value, type.value, ctx.theme.value, ctx.fmt, appId => appId < 0 ? (ctx.theme.value.dark ? '#52525b' : '#d4d4d8') : ctx.colorOf(appId))
  : null)

function onClick(params: any) {
  const appId = params?.data?.appId
  if (typeof appId === 'number' && appId > 0) {
    ctx.setDrillApp(appId)
  }
}

const csv = () => [[t('stats.column.app'), 'AppID', t('stats.column.duration'), t('stats.column.hours')], ...items.value.map(i => [i.name, i.appId > 0 ? i.appId : '', ctx.fmt(i.value), (i.value / 3600).toFixed(2)])]
</script>

<template>
  <StatChartCard
    v-model:type="type"
    :title="t('stats.widget.durationShare', { n: ctx.topN.value })"
    icon="i-mdi:chart-donut"
    :description="t('stats.hint.clickToDrill')"
    :option="option"
    :types="['pie', 'bar', 'treemap']"
    :csv="csv"
    @chart-click="onClick"
  />
</template>
