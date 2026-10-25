<script setup lang="ts">
import type { TableColumn } from '@fantastic-admin/components'
import type { StatSession } from '@/features/stats/model'
import { useI18n } from 'vue-i18n'
import StatChartCard from '@/components/StatChartCard/index.vue'
import { useStatsContext } from '@/features/stats/context'
import dayjs from '@/utils/dayjs'

const { t } = useI18n()
const ctx = useStatsContext()
const router = useRouter()

const page = ref(1)
const pageSize = ref(10)

const list = computed(() => [...ctx.sessions.value].reverse())
const rows = computed(() => list.value.slice((page.value - 1) * pageSize.value, page.value * pageSize.value))
watch(list, () => {
  page.value = 1
})

const columns = computed<TableColumn<StatSession>[]>(() => [
  { id: 'app', header: t('stats.column.app'), minWidth: 200 },
  { id: 'start', header: t('stats.column.start'), width: 160 },
  { id: 'end', header: t('stats.column.end'), width: 160 },
  { id: 'duration', header: t('stats.column.duration'), width: 110 },
  { id: 'idle', header: t('stats.column.idle'), width: 110 },
])

const title = computed(() => {
  const { appId, day } = ctx.drill.value
  const parts = [appId != null ? ctx.appName(appId) : null, day].filter(Boolean)
  return parts.length ? `${t('stats.widget.drillDetail')} · ${parts.join(' · ')}` : t('stats.widget.drillDetail')
})

function csv() {
  return [
    [t('stats.column.app'), 'AppID', 'SteamID', t('stats.column.start'), t('stats.column.end'), t('stats.column.minutes'), t('stats.column.idleMinutes')],
    ...list.value.map(s => [ctx.appName(s.appId), s.appId, s.steamId, dayjs.unix(s.start).format('YYYY-MM-DD HH:mm:ss'), dayjs.unix(s.end).format('YYYY-MM-DD HH:mm:ss'), (s.duration / 60).toFixed(1), (s.idle / 60).toFixed(1)]),
  ]
}
</script>

<template>
  <StatChartCard :title="title" icon="i-mdi:table-search" :description="t('stats.hint.drillDetail', { n: list.length })" :csv="csv">
    <template #actions>
      <FaButton variant="ghost" @click="router.push('/sessions')">
        <FaIcon name="i-mdi:timeline-clock-outline" />
        {{ t('stats.manageSessions') }}
      </FaButton>
    </template>
    <FaTable :columns="columns" :data="rows" stripe>
      <template #cell-app="{ row }">
        <button class="hover:text-primary" @click="ctx.setDrillApp(row.original.appId)">
          {{ ctx.appName(row.original.appId) }}
        </button>
      </template>
      <template #cell-start="{ row }">
        {{ dayjs.unix(row.original.start).format('YYYY-MM-DD HH:mm') }}
      </template>
      <template #cell-end="{ row }">
        {{ dayjs.unix(row.original.end).format('YYYY-MM-DD HH:mm') }}
      </template>
      <template #cell-duration="{ row }">
        {{ ctx.fmt(row.original.duration) }}
      </template>
      <template #cell-idle="{ row }">
        {{ ctx.fmt(row.original.idle) }}
      </template>
    </FaTable>
    <FaPagination v-if="list.length > pageSize" v-model:page="page" v-model:size="pageSize" :total="list.length" class="mt-2" />
  </StatChartCard>
</template>
