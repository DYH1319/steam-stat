<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import StatChartCard from '@/components/StatChartCard/index.vue'
import { useStatsContext } from '@/features/stats/context'
import { formatSessionDuration } from '@/utils/sessionDuration'

defineProps<{ title: string, icon?: string }>()

const { t } = useI18n()
const ctx = useStatsContext()

const stats = computed(() => {
  const pieces = ctx.drilled.value
  const seconds = pieces.reduce((s, p) => s + p.duration, 0)
  const idle = pieces.reduce((s, p) => s + p.idleDuration, 0)
  const count = pieces.filter(p => p.isStartPiece).length
  return { seconds, idle, effective: Math.max(0, seconds - idle), count, games: new Set(pieces.map(p => p.appId)).size, avg: count ? seconds / count : 0 }
})
</script>

<template>
  <StatChartCard :title="title" :icon="icon" :loading="ctx.loading.value" :empty="ctx.drilled.value.length === 0" :empty-text="t('stats.empty')">
    <template #actions>
      <slot name="actions" />
    </template>
    <div class="gap-3 grid grid-cols-2 md:grid-cols-3 xl:grid-cols-6">
      <div
        v-for="(card, i) in [
          { label: t('stats.cardSessions'), value: String(stats.count) },
          { label: t('stats.cardGames'), value: String(stats.games) },
          { label: t('stats.cardTotal'), value: formatSessionDuration(stats.seconds) },
          { label: t('stats.cardAvg'), value: formatSessionDuration(stats.avg) },
          { label: t('stats.cardAfk'), value: formatSessionDuration(stats.idle) },
          { label: t('stats.cardEffective'), value: formatSessionDuration(stats.effective) },
        ]" :key="i" class="text-white p-4 rounded-lg" :style="{ background: `linear-gradient(135deg, ${ctx.palette.value[i % 12] ?? '#888'}, ${ctx.palette.value[(i + 3) % 12] ?? '#666'})` }"
      >
        <div class="text-xs opacity-80">
          {{ card.label }}
        </div>
        <div class="text-xl font-bold mt-1">
          {{ card.value }}
        </div>
      </div>
    </div>
  </StatChartCard>
</template>
