<script setup lang="ts">
import type { TableColumn, TableSortingState } from '@fantastic-admin/components'
import type { AppAggregate } from '@/features/stats/aggregate'
import { useI18n } from 'vue-i18n'
import GameImage from '@/components/GameImage/index.vue'
import StatChartCard from '@/components/StatChartCard/index.vue'
import { byApp } from '@/features/stats/aggregate'
import { useStatsContext } from '@/features/stats/context'
import dayjs from '@/utils/dayjs'

const { t } = useI18n()
const ctx = useStatsContext()
const router = useRouter()

const sorting = ref<TableSortingState>([{ id: 'seconds', desc: true }])
const page = ref(1)
const pageSize = ref(10)

const all = computed(() => byApp(ctx.drill.value.day ? ctx.sessions.value : ctx.base.value))
const total = computed(() => all.value.reduce((sum, a) => sum + a.seconds, 0) || 1)

const sorted = computed(() => {
  const { id, desc } = sorting.value[0] ?? { id: 'seconds', desc: true }
  const key = id as keyof AppAggregate
  return [...all.value].sort((a, b) => (desc ? -1 : 1) * ((a[key] as number) - (b[key] as number)))
})

const rows = computed(() => sorted.value.slice((page.value - 1) * pageSize.value, page.value * pageSize.value))

watch(all, () => {
  page.value = 1
})

const columns = computed<TableColumn<AppAggregate>[]>(() => [
  { id: 'rank', header: '#', width: 48 },
  { id: 'app', header: t('stats.column.app'), minWidth: 220 },
  { accessorKey: 'seconds', header: t('stats.column.duration'), width: 120, enableSorting: true },
  { id: 'share', header: t('stats.column.share'), width: 90 },
  { accessorKey: 'count', header: t('stats.column.sessions'), width: 90, enableSorting: true },
  { id: 'avg', header: t('stats.column.avg'), width: 110 },
  { accessorKey: 'first', header: t('stats.column.first'), width: 120, enableSorting: true },
  { accessorKey: 'last', header: t('stats.column.last'), width: 120, enableSorting: true },
  { id: 'actions', header: '', width: 56, align: 'center' },
])

function onSortingChange(value: TableSortingState) {
  sorting.value = value.length ? [value[0]] : [{ id: 'seconds', desc: true }]
}

function csv() {
  return [
    [t('stats.column.app'), 'AppID', t('stats.column.hours'), t('stats.column.sessions'), t('stats.column.first'), t('stats.column.last')],
    ...sorted.value.map(a => [ctx.appName(a.appId), a.appId, (a.seconds / 3600).toFixed(2), a.count, dayjs.unix(a.first).format('YYYY-MM-DD'), dayjs.unix(a.last).format('YYYY-MM-DD')]),
  ]
}
</script>

<template>
  <StatChartCard :title="t('stats.widget.gameRanking')" icon="i-mdi:format-list-numbered" :csv="csv">
    <FaTable
      row-key="appId"
      stripe
      sortable
      :columns="columns"
      :data="rows"
      :sorting="sorting"
      :enable-multi-sort="false"
      @sorting-change="onSortingChange"
    >
      <template #cell-rank="{ index }">
        {{ (page - 1) * pageSize + index + 1 }}
      </template>
      <template #cell-app="{ row }">
        <button class="text-left flex gap-2 items-center hover:text-primary" @click="ctx.setDrillApp(row.original.appId)">
          <GameImage :app-id="row.original.appId" :name="ctx.appName(row.original.appId)" />
          <span class="truncate">{{ ctx.appName(row.original.appId) }}</span>
        </button>
      </template>
      <template #cell-seconds="{ row }">
        {{ ctx.fmt(row.original.seconds) }}
      </template>
      <template #cell-share="{ row }">
        {{ ((row.original.seconds / total) * 100).toFixed(1) }}%
      </template>
      <template #cell-avg="{ row }">
        {{ ctx.fmt(row.original.seconds / row.original.count) }}
      </template>
      <template #cell-first="{ row }">
        {{ dayjs.unix(row.original.first).format('YYYY-MM-DD') }}
      </template>
      <template #cell-last="{ row }">
        {{ dayjs.unix(row.original.last).format('YYYY-MM-DD') }}
      </template>
      <template #cell-actions="{ row }">
        <FaTooltip :text="t('stats.openGame')">
          <FaButton size="icon-sm" variant="ghost" @click="router.push(`/game/${row.original.appId}`)">
            <FaIcon name="i-mdi:open-in-app" />
          </FaButton>
        </FaTooltip>
      </template>
    </FaTable>
    <FaPagination v-if="all.length > pageSize" v-model:page="page" v-model:size="pageSize" :total="all.length" class="mt-2" />
  </StatChartCard>
</template>
