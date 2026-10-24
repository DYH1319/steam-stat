<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import StatChartCard from '@/components/StatChartCard/index.vue'
import { useStatsContext } from '@/features/stats/context'
import dayjs from '@/utils/dayjs'
import { formatSessionDuration } from '@/utils/sessionDuration'

defineProps<{ title: string, icon?: string }>()

const { t } = useI18n()
const ctx = useStatsContext()
const router = useRouter()

const rows = computed(() =>
  [...ctx.drilled.value].sort((a, b) => b.startTime - a.startTime).slice(0, 200),
)

function viewInSessions() {
  const query: Record<string, string> = {}
  if (ctx.drill.value.appId != null) {
    query.appId = String(ctx.drill.value.appId)
  }
  if (ctx.drill.value.day) {
    query.start = ctx.drill.value.day
    query.end = ctx.drill.value.day
  }
  router.push({ path: '/sessions', query })
}
</script>

<template>
  <StatChartCard :title="title" :icon="icon" :loading="ctx.loading.value" :empty="rows.length === 0" :empty-text="t('stats.empty')">
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
            <th class="text-left">
              {{ t('history.columnStart') }}
            </th>
            <th class="text-left">
              {{ t('history.columnEnd') }}
            </th>
            <th class="text-right">
              {{ t('history.columnDuration') }}
            </th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="(p, i) in rows" :key="i">
            <td class="py-1">
              <RouterLink :to="`/game/${p.appId}`" class="hover:underline">
                {{ ctx.appName(p.appId) }}
              </RouterLink>
            </td>
            <td>{{ dayjs.unix(p.startTime).format('YYYY-MM-DD HH:mm') }}</td>
            <td>{{ dayjs.unix(p.endTime).format('YYYY-MM-DD HH:mm') }}</td>
            <td class="text-right">
              {{ formatSessionDuration(p.duration) }}
            </td>
          </tr>
        </tbody>
      </table>
    </div>
    <FaButton variant="outline" class="mt-3" @click="viewInSessions">
      {{ t('stats.viewInSessions') }}
    </FaButton>
  </StatChartCard>
</template>
