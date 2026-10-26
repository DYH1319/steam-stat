<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import StatChartCard from '@/components/StatChartCard/index.vue'
import { byApp, lifecycle } from '@/features/stats/aggregate'
import { lifecycleOption } from '@/features/stats/charts'
import { useStatsContext } from '@/features/stats/context'
import { useStatsStore } from '@/store/modules/stats'
import dayjs from '@/utils/dayjs'

const { t } = useI18n()
const ctx = useStatsContext()
const store = useStatsStore()

const ranked = computed(() => byApp(ctx.scoped.value))
const appOptions = computed(() => ranked.value.map(a => ({ value: a.appId, label: ctx.appName(a.appId) })))

const selected = ref<number[]>([])
const relative = ref(true)
const withBaseline = ref(false)

const effectiveApps = computed(() => {
  if (ctx.fixedAppId.value != null) {
    return [ctx.fixedAppId.value]
  }
  if (ctx.drill.value.appId != null) {
    return [ctx.drill.value.appId]
  }
  const valid = selected.value.filter(id => ranked.value.some(a => a.appId === id))
  return valid.length ? valid : ranked.value.slice(0, 3).map(a => a.appId)
})

function baselineOf(appId: number): number {
  if (!withBaseline.value) {
    return 0
  }
  const steamIds = store.steamIds.length ? new Set(store.steamIds) : null
  return ctx.data.value.baselines
    .filter(b => b.appId === appId && (!steamIds || steamIds.has(b.steamId)))
    .reduce((sum, b) => sum + b.baselineSeconds, 0)
}

const series = computed(() => effectiveApps.value.map(appId => ({ appId, points: lifecycle(ctx.scoped.value, appId, baselineOf(appId)) })))

const option = computed(() => series.value.some(s => s.points.length)
  ? lifecycleOption(series.value, relative.value, ctx.theme.value, ctx.fmt, ctx.appName, ctx.colorOf, t('stats.unit.dayShort'))
  : null)

function csv() {
  return [
    [t('stats.column.app'), t('stats.column.time'), t('stats.column.daysSinceFirst'), t('stats.column.cumulativeHours')],
    ...series.value.flatMap(s => s.points.map(p => [ctx.appName(s.appId), dayjs.unix(p.ts).format('YYYY-MM-DD HH:mm'), p.dayOffset.toFixed(2), (p.seconds / 3600).toFixed(2)])),
  ]
}
</script>

<template>
  <StatChartCard
    :title="t('stats.widget.lifecycle')"
    icon="i-mdi:chart-bell-curve-cumulative"
    :option="option"
    :csv="csv"
  >
    <template #actions>
      <FaSelect
        v-if="ctx.fixedAppId.value == null && ctx.drill.value.appId == null"
        v-model="selected"
        multiple
        :options="appOptions"
        :placeholder="t('stats.lifecycleCompare')"
        class="w-44"
      />
      <FaTooltip :text="t('stats.lifecycleRelative')">
        <FaButton size="icon-sm" :variant="relative ? 'secondary' : 'ghost'" @click="relative = !relative">
          <FaIcon name="i-mdi:align-horizontal-left" />
        </FaButton>
      </FaTooltip>
      <FaTooltip :text="t('stats.lifecycleBaseline')">
        <FaButton size="icon-sm" :variant="withBaseline ? 'secondary' : 'ghost'" @click="withBaseline = !withBaseline">
          <FaIcon name="i-mdi:history" />
        </FaButton>
      </FaTooltip>
    </template>
  </StatChartCard>
</template>
