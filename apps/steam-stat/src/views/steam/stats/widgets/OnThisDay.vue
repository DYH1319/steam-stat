<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import GameImage from '@/components/GameImage/index.vue'
import StatChartCard from '@/components/StatChartCard/index.vue'
import { onThisDay } from '@/features/stats/aggregate'
import { useStatsContext } from '@/features/stats/context'
import { useStatsStore } from '@/store/modules/stats'

const { t } = useI18n()
const ctx = useStatsContext()
const store = useStatsStore()

const entries = computed(() => onThisDay(ctx.scoped.value, store.todayKey, ctx.opts.value))
</script>

<template>
  <StatChartCard :title="t('stats.widget.onThisDay')" icon="i-mdi:calendar-today" :description="store.todayKey.slice(5)">
    <div v-if="entries.length" class="space-y-3">
      <button
        v-for="entry in entries"
        :key="entry.day"
        class="p-3 text-left rounded-md bg-muted/50 w-full block hover:bg-muted"
        @click="ctx.setDrillDay(entry.day)"
      >
        <div class="text-sm font-medium flex items-center justify-between">
          <span>{{ t('stats.yearsAgo', { n: Number(store.todayKey.slice(0, 4)) - entry.year, year: entry.year }) }}</span>
          <span class="tabular-nums">{{ ctx.fmt(entry.seconds) }}</span>
        </div>
        <div class="mt-2 flex flex-wrap gap-2">
          <div v-for="app in entry.apps.slice(0, 3)" :key="app.appId" class="text-xs flex gap-1.5 items-center">
            <GameImage :app-id="app.appId" :name="ctx.appName(app.appId)" />
            <span>{{ ctx.appName(app.appId) }} · {{ ctx.fmt(app.seconds) }}</span>
          </div>
        </div>
      </button>
    </div>
    <div v-else class="text-sm text-muted-foreground py-10 text-center">
      {{ t('stats.onThisDayEmpty') }}
    </div>
  </StatChartCard>
</template>
