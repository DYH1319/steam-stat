<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import StatChartCard from '@/components/StatChartCard/index.vue'
import { comparePeriods } from '@/features/stats'
import { useStatsContext } from '@/features/stats/context'
import { useStatsStore } from '@/store/modules/stats'
import dayjs from '@/utils/dayjs'
import { formatSessionDuration } from '@/utils/sessionDuration'

defineProps<{ title: string, icon?: string }>()

const { t } = useI18n()
const ctx = useStatsContext()
const statsStore = useStatsStore()

const data = computed(() => comparePeriods(ctx.allSessions.value, dayjs().unix(), statsStore.prefs))
const cards = computed(() => [
  { label: t('stats.weekVsLast'), ...delta(data.value.week) },
  { label: t('stats.monthVsLast'), ...delta(data.value.month) },
  { label: t('stats.monthVsYear'), ...delta(data.value.monthYoY) },
])

function delta(c: { current: { seconds: number, sessions: number, games: number }, previous: { seconds: number, sessions: number, games: number } }) {
  const isNew = c.previous.seconds <= 0
  const pct = isNew ? 0 : Math.round((c.current.seconds - c.previous.seconds) / c.previous.seconds * 100)
  return {
    current: formatSessionDuration(c.current.seconds),
    previous: formatSessionDuration(c.previous.seconds),
    sessions: `${c.current.sessions} / ${c.previous.sessions}`,
    games: `${c.current.games} / ${c.previous.games}`,
    type: pct >= 0 ? 'up' as const : 'down' as const,
    pct: isNew ? null : Math.abs(pct),
    isNew,
  }
}
</script>

<template>
  <StatChartCard :title="title" :icon="icon" :loading="ctx.loading.value">
    <template #actions>
      <slot name="actions" />
    </template>
    <div class="gap-3 grid grid-cols-1 md:grid-cols-3">
      <div v-for="card in cards" :key="card.label" class="p-4 border rounded-lg">
        <div class="text-sm text-muted-foreground">
          {{ card.label }}
        </div>
        <div class="mt-2 flex gap-2 items-end">
          <span class="text-2xl font-bold">{{ card.current }}</span>
          <FaTrend v-if="card.pct != null" :type="card.type" :value="`${card.pct}%`" />
          <FaTag v-else variant="secondary">
            {{ t('stats.new') }}
          </FaTag>
        </div>
        <div class="text-xs text-muted-foreground mt-1">
          {{ t('stats.prevPeriod') }} {{ card.previous }} · {{ t('stats.sessionsGames', { s: card.sessions, g: card.games }) }}
        </div>
      </div>
    </div>
  </StatChartCard>
</template>
