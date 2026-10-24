<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import StatChartCard from '@/components/StatChartCard/index.vue'
import { useStatsContext } from '@/features/stats/context'
import dayjs from '@/utils/dayjs'
import { formatSessionDuration } from '@/utils/sessionDuration'

defineProps<{ title: string, icon?: string }>()

const { t } = useI18n()
const ctx = useStatsContext()
</script>

<template>
  <StatChartCard :title="title" :icon="icon" :loading="ctx.loading.value" :empty="ctx.totals.value.length === 0" :empty-text="t('stats.empty')">
    <template #actions>
      <slot name="actions" />
    </template>
    <div class="max-h-80 overflow-auto">
      <table class="text-sm w-full">
        <thead class="text-muted-foreground bg-card top-0 sticky">
          <tr>
            <th class="py-1 text-left">
              {{ t('history.columnApp') }}
            </th>
            <th class="text-right">
              {{ t('stats.colHours') }}
            </th>
            <th class="text-right">
              {{ t('stats.colEffective') }}
            </th>
            <th class="text-right">
              {{ t('stats.colSessions') }}
            </th>
            <th class="text-right">
              {{ t('stats.colLastPlayed') }}
            </th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="app in ctx.totals.value" :key="app.appId" class="cursor-pointer hover:bg-accent" @click="$router.push(`/game/${app.appId}`)">
            <td class="py-1">
              <span class="mr-2 rounded-full h-2.5 w-2.5 inline-block" :style="{ background: ctx.appColor(app.appId) }" />{{ ctx.appName(app.appId) }}
            </td>
            <td class="text-right">
              {{ formatSessionDuration(app.seconds) }}
            </td>
            <td class="text-right">
              {{ formatSessionDuration(app.effectiveSeconds) }}
            </td>
            <td class="text-right">
              {{ app.count }}
            </td>
            <td class="text-right">
              {{ dayjs.unix(app.lastPlayed).format('YYYY-MM-DD') }}
            </td>
          </tr>
        </tbody>
      </table>
    </div>
  </StatChartCard>
</template>
