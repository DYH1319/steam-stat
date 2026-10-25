<script setup lang="ts">
import type { TableColumn } from '@fantastic-admin/components'
import { useI18n } from 'vue-i18n'
import { toast } from 'vue-sonner'
import GameImage from '@/components/GameImage/index.vue'
import { useChartTheme } from '@/composables/useChartTheme'
import { useIpc } from '@/composables/useIpc'
import { useStatsStore } from '@/store/modules/stats'
import dayjs from '@/utils/dayjs'
import { downloadCsv } from '@/utils/download'

defineOptions({
  name: 'SteamHistory',
})

const MAX_FILE_BYTES = 5 * 1024 * 1024

const { t } = useI18n()
const router = useRouter()
const ipc = useIpc()
const statsStore = useStatsStore()
const theme = useChartTheme()

const tab = ref('reconcile')
const tabs = computed(() => [
  { value: 'reconcile', label: t('history.tab.reconcile'), icon: 'i-mdi:scale-balance' },
  { value: 'import', label: t('history.tab.import'), icon: 'i-mdi:file-import-outline' },
])

function time(ts?: number | null, format = 'YYYY-MM-DD HH:mm') {
  return ts ? dayjs.unix(ts).format(format) : '—'
}

// ---------- 基线与对账 ----------
const overview = ref<PlaytimeHistoryOverview | null>(null)
const overviewLoading = ref(false)
const capturing = ref(false)
const statusFilter = ref<'all' | PlaytimeReconciliation['status']>('all')
const keyword = ref('')
const page = ref(1)
const pageSize = ref(15)

async function loadOverview() {
  overviewLoading.value = true
  try {
    overview.value = await ipc.historyOverviewGet({})
  }
  catch (e: any) {
    toast.error(`${t('common.getFailed')}: ${e?.message || e}`)
  }
  finally {
    overviewLoading.value = false
  }
}

async function capture() {
  capturing.value = true
  try {
    const result = await ipc.historySnapshotCapture()
    if (result.success) {
      toast.success(t('history.captured', { observed: result.observed, inserted: result.inserted, sources: result.sources.join(' + ') }))
      await loadOverview()
      void statsStore.reload().catch(() => undefined)
    }
    else {
      toast.error(t(`history.captureError.${result.error ?? 'failed'}`))
    }
  }
  catch (e: any) {
    toast.error(`${t('common.actionFailed')}: ${e?.message || e}`)
  }
  finally {
    capturing.value = false
  }
}

const games = computed(() => {
  const list = overview.value?.games ?? []
  const word = keyword.value.trim().toLowerCase()
  return list.filter(game => (statusFilter.value === 'all' || game.status === statusFilter.value)
    && (!word || (game.appName ?? '').toLowerCase().includes(word) || String(game.appId).includes(word)))
})
const pagedGames = computed(() => games.value.slice((page.value - 1) * pageSize.value, page.value * pageSize.value))
watch([statusFilter, keyword], () => {
  page.value = 1
})

const summary = computed(() => {
  const list = overview.value?.games ?? []
  return [
    { key: 'games', value: String(list.length) },
    { key: 'official', value: statsStore.fmt(list.reduce((sum, g) => sum + g.officialMinutes * 60, 0)) },
    { key: 'baseline', value: statsStore.fmt(list.reduce((sum, g) => sum + g.baselineSeconds, 0)) },
    { key: 'untracked', value: statsStore.fmt(list.reduce((sum, g) => sum + Math.max(0, g.untrackedSeconds), 0)) },
    { key: 'flagged', value: String(list.filter(g => g.status !== 'matched').length) },
  ]
})
const gradients = computed(() => theme.value.gradients(summary.value.length))

const statusOptions = computed(() => (['all', 'matched', 'untracked', 'overcounted'] as const).map(value => ({ value, label: t(`history.status.${value}`) })))

const gameColumns = computed<TableColumn<PlaytimeReconciliation>[]>(() => [
  { id: 'app', header: t('stats.column.app'), minWidth: 220 },
  { id: 'user', header: t('history.column.user'), width: 120 },
  { id: 'official', header: t('history.column.official'), width: 110 },
  { id: 'baseline', header: t('history.column.baseline'), width: 120 },
  { id: 'delta', header: t('history.column.delta'), width: 120 },
  { id: 'local', header: t('history.column.local'), width: 120 },
  { id: 'untracked', header: t('history.column.untracked'), width: 120 },
  { id: 'status', header: t('history.column.status'), width: 110 },
  { id: 'observed', header: t('history.column.observed'), width: 150 },
  { id: 'actions', header: '', width: 56, align: 'center' },
])

function statusVariant(status: PlaytimeReconciliation['status']) {
  return status === 'matched' ? 'secondary' : status === 'untracked' ? 'default' : 'destructive'
}

function signed(seconds: number) {
  return `${seconds < 0 ? '-' : seconds > 0 ? '+' : ''}${statsStore.fmt(Math.abs(seconds))}`
}

const discrepancyColumns = computed<TableColumn<PlaytimeDiscrepancy>[]>(() => [
  { id: 'app', header: t('stats.column.app'), minWidth: 200 },
  { id: 'range', header: t('history.column.window'), width: 290 },
  { id: 'official', header: t('history.column.officialDelta'), width: 120 },
  { id: 'local', header: t('history.column.local'), width: 110 },
  { id: 'diff', header: t('history.column.untracked'), width: 120 },
])

function exportReconcile() {
  downloadCsv('steam-stat-reconcile.csv', [
    ['AppID', t('stats.column.app'), 'SteamID', t('history.column.official'), t('history.column.baseline'), t('history.column.delta'), t('history.column.local'), t('history.column.untracked'), t('history.column.status')],
    ...games.value.map(g => [g.appId, g.appName ?? '', g.steamId, (g.officialMinutes / 60).toFixed(2), (g.baselineSeconds / 3600).toFixed(2), (g.officialDeltaSeconds / 3600).toFixed(2), (g.localTrackedSeconds / 3600).toFixed(2), (g.untrackedSeconds / 3600).toFixed(2), g.status]),
  ])
}

// ---------- CSV 导入 ----------
const users = ref<SteamUser[]>([])
const defaultSteamId = ref<string | undefined>()
const fileName = ref('')
const content = ref('')
const skipDuplicates = ref(true)
const skipOverlaps = ref(true)
const preview = ref<SessionImportPreview | null>(null)
const previewing = ref(false)
const committing = ref(false)
const batches = ref<SessionImportBatch[]>([])
const fileInput = ref<HTMLInputElement>()

const userOptions = computed(() => users.value.map(user => ({
  value: user.steamId,
  label: `${user.personaName || user.accountName} (${user.accountName})`,
})))

async function loadImportSources() {
  try {
    users.value = await ipc.steamGetLoginUser()
    if (!defaultSteamId.value) {
      defaultSteamId.value = users.value.find(user => user.mostRecent)?.steamId ?? users.value[0]?.steamId
    }
    batches.value = await ipc.historyImportBatchesGet()
  }
  catch (e: any) {
    toast.error(`${t('common.getFailed')}: ${e?.message || e}`)
  }
}

async function onFileChange(event: Event) {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  input.value = ''
  if (!file) {
    return
  }
  if (file.size > MAX_FILE_BYTES) {
    toast.error(t('history.import.tooLarge', { size: '5 MB' }))
    return
  }
  fileName.value = file.name
  content.value = await file.text()
  await runPreview()
}

function request(): SessionImportRequest {
  return {
    fileName: fileName.value,
    content: content.value,
    defaultSteamId: defaultSteamId.value ?? null,
    skipDuplicates: skipDuplicates.value,
    skipOverlaps: skipOverlaps.value,
  }
}

async function runPreview() {
  if (!content.value) {
    return
  }
  previewing.value = true
  try {
    preview.value = await ipc.historyImportPreview(request())
  }
  catch (e: any) {
    toast.error(`${t('history.import.previewFailed')}: ${e?.message || e}`)
  }
  finally {
    previewing.value = false
  }
}

watch(defaultSteamId, () => {
  void runPreview()
})

const importable = computed(() => {
  const p = preview.value
  if (!p || p.fatalError) {
    return 0
  }
  return p.newRows + (skipDuplicates.value ? 0 : p.duplicateRows) + (skipOverlaps.value ? 0 : p.overlapRows)
})

async function commit() {
  committing.value = true
  try {
    const result = await ipc.historyImportCommit(request())
    if (result.success) {
      toast.success(t('history.import.done', { inserted: result.inserted, skipped: result.skipped }))
      preview.value = null
      content.value = ''
      fileName.value = ''
      batches.value = await ipc.historyImportBatchesGet()
      void statsStore.reload().catch(() => undefined)
      void loadOverview()
    }
    else {
      toast.error(t(`history.import.error.${result.error ?? 'failed'}`))
    }
  }
  catch (e: any) {
    toast.error(`${t('common.actionFailed')}: ${e?.message || e}`)
  }
  finally {
    committing.value = false
  }
}

function revert(batch: SessionImportBatch) {
  useFaModal().confirm({
    title: t('common.confirm'),
    content: t('history.import.revertConfirm', { name: batch.fileName, count: batch.remainingCount }),
    onConfirm: async () => {
      try {
        const count = await ipc.historyImportBatchDelete({ id: batch.id })
        toast.success(t('history.import.reverted', { count }))
        batches.value = await ipc.historyImportBatchesGet()
        void statsStore.reload().catch(() => undefined)
      }
      catch (e: any) {
        toast.error(`${t('common.actionFailed')}: ${e?.message || e}`)
      }
    },
  })
}

function downloadTemplate() {
  const now = dayjs()
  downloadCsv('steam-stat-sessions-template.csv', [
    ['app_id', 'app_name', 'steam_id', 'start_time', 'end_time', 'duration_minutes', 'note', 'tags'],
    [570, 'Dota 2', defaultSteamId.value ?? '', now.subtract(1, 'day').format('YYYY-MM-DD 20:00'), now.subtract(1, 'day').format('YYYY-MM-DD 22:30'), '', '', 'ranked;friends'],
    [730, 'Counter-Strike 2', '', now.subtract(2, 'day').format('YYYY-MM-DD 21:00'), '', 95, 'imported', ''],
  ])
}

const errorColumns = computed<TableColumn<SessionImportRowError>[]>(() => [
  { accessorKey: 'line', header: t('history.import.line'), width: 80 },
  { id: 'code', header: t('history.import.reason'), width: 220 },
  { id: 'value', header: t('history.import.value'), minWidth: 160 },
])

const sampleColumns = computed<TableColumn<SessionImportRow>[]>(() => [
  { accessorKey: 'line', header: t('history.import.line'), width: 70 },
  { id: 'app', header: t('stats.column.app'), minWidth: 180 },
  { id: 'start', header: t('stats.column.start'), width: 150 },
  { id: 'end', header: t('stats.column.end'), width: 150 },
  { id: 'duration', header: t('stats.column.duration'), width: 100 },
  { id: 'status', header: t('history.column.status'), width: 100 },
])

const batchColumns = computed<TableColumn<SessionImportBatch>[]>(() => [
  { accessorKey: 'fileName', header: t('history.import.file'), minWidth: 200 },
  { id: 'createdAt', header: t('history.import.importedAt'), width: 160 },
  { id: 'count', header: t('history.import.count'), width: 120 },
  { id: 'actions', header: '', width: 110, align: 'center' },
])

onMounted(() => {
  void loadOverview()
  void loadImportSources()
})
</script>

<template>
  <div>
    <FaPageMain class="mb-0">
      <AppPageHeader icon="i-mdi:database-import-outline" :title="t('menu.steamHistory')" :description="t('history.subtitle')">
        <template #actions>
          <span v-if="overview?.lastCapturedAt" class="text-xs text-muted-foreground">
            {{ t('history.lastCaptured', { time: time(overview.lastCapturedAt) }) }}
          </span>
          <FaButton variant="outline" :loading="overviewLoading" @click="loadOverview">
            <FaIcon name="i-mdi:refresh" />
            {{ t('common.refreshData') }}
          </FaButton>
          <FaButton :loading="capturing" @click="capture">
            <FaIcon name="i-mdi:camera-timer" />
            {{ t('history.captureNow') }}
          </FaButton>
        </template>
      </AppPageHeader>

      <FaTabs v-model="tab" :list="tabs" list-class="justify-start w-fit" content-class="pt-4">
        <template #reconcile>
          <FaAlert class="mb-4" :description="t('history.reconcileHelp')" />

          <div class="mb-4 gap-3 grid grid-cols-2 lg:grid-cols-5">
            <div
              v-for="(card, index) in summary"
              :key="card.key"
              class="text-white p-4 rounded-lg"
              :style="{ backgroundImage: `linear-gradient(135deg, ${gradients[index][0]}, ${gradients[index][1]})` }"
            >
              <div class="text-sm opacity-90">
                {{ t(`history.summary.${card.key}`) }}
              </div>
              <div class="text-2xl font-bold tabular-nums">
                {{ card.value }}
              </div>
            </div>
          </div>

          <div v-if="overviewLoading && !overview" class="rounded-lg bg-muted h-64 animate-pulse" />
          <div v-else-if="!overview?.games.length" class="text-muted-foreground py-14 text-center flex flex-col gap-3 items-center">
            <FaIcon name="i-mdi:camera-timer" class="opacity-50 size-14" />
            <span>{{ t('history.noSnapshots') }}</span>
            <FaButton :loading="capturing" @click="capture">
              {{ t('history.captureNow') }}
            </FaButton>
          </div>
          <template v-else>
            <div class="mb-3 flex flex-wrap gap-3 items-center">
              <FaSelect v-model="statusFilter" :options="statusOptions" class="w-36" />
              <FaInput v-model="keyword" :placeholder="t('history.searchPlaceholder')" clearable class="w-56" />
              <FaButton variant="outline" @click="exportReconcile">
                <FaIcon name="i-mdi:file-delimited-outline" />
                {{ t('stats.exportCsv') }}
              </FaButton>
            </div>
            <FaTable :columns="gameColumns" :data="pagedGames" stripe>
              <template #cell-app="{ row }">
                <div class="flex gap-2 items-center">
                  <GameImage :app-id="row.original.appId" :name="row.original.appName ?? ''" />
                  <span class="truncate">{{ row.original.appName || `App ${row.original.appId}` }}</span>
                </div>
              </template>
              <template #cell-user="{ row }">
                {{ row.original.userPersonaName || row.original.steamId }}
              </template>
              <template #cell-official="{ row }">
                {{ statsStore.fmt(row.original.officialMinutes * 60) }}
              </template>
              <template #cell-baseline="{ row }">
                <FaTooltip :text="t('history.baselineAt', { time: time(row.original.baselineAt) })">
                  <span>{{ statsStore.fmt(row.original.baselineSeconds) }}</span>
                </FaTooltip>
              </template>
              <template #cell-delta="{ row }">
                {{ statsStore.fmt(row.original.officialDeltaSeconds) }}
              </template>
              <template #cell-local="{ row }">
                {{ statsStore.fmt(row.original.localTrackedSeconds) }}
              </template>
              <template #cell-untracked="{ row }">
                <span :class="row.original.status === 'untracked' ? 'text-primary font-medium' : row.original.status === 'overcounted' ? 'text-destructive' : ''">
                  {{ signed(row.original.untrackedSeconds) }}
                </span>
              </template>
              <template #cell-status="{ row }">
                <FaTag :variant="statusVariant(row.original.status)">
                  {{ t(`history.status.${row.original.status}`) }}
                </FaTag>
              </template>
              <template #cell-observed="{ row }">
                <FaTooltip :text="t('history.sourceLabel', { source: row.original.source, count: row.original.snapshotCount })">
                  <span>{{ time(row.original.lastObservedAt) }}</span>
                </FaTooltip>
              </template>
              <template #cell-actions="{ row }">
                <FaButton size="icon-sm" variant="ghost" @click="router.push(`/game/${row.original.appId}`)">
                  <FaIcon name="i-mdi:open-in-app" />
                </FaButton>
              </template>
            </FaTable>
            <FaPagination v-if="games.length > pageSize" v-model:page="page" v-model:size="pageSize" :total="games.length" class="mt-2" />

            <div class="mt-6">
              <div class="font-semibold mb-1 flex gap-2 items-center">
                <FaIcon name="i-mdi:devices" class="text-primary" />
                {{ t('history.discrepancyTitle') }}
              </div>
              <div class="text-xs text-muted-foreground mb-3">
                {{ t('history.discrepancyHelp') }}
              </div>
              <FaTable :columns="discrepancyColumns" :data="overview.discrepancies.slice(0, 100)" stripe>
                <template #cell-app="{ row }">
                  {{ row.original.appName || `App ${row.original.appId}` }}
                </template>
                <template #cell-range="{ row }">
                  {{ time(row.original.from) }} ~ {{ time(row.original.to) }}
                </template>
                <template #cell-official="{ row }">
                  {{ statsStore.fmt(row.original.officialSeconds) }}
                </template>
                <template #cell-local="{ row }">
                  {{ statsStore.fmt(row.original.localSeconds) }}
                </template>
                <template #cell-diff="{ row }">
                  <span class="text-primary font-medium">{{ signed(row.original.officialSeconds - row.original.localSeconds) }}</span>
                </template>
                <template #empty>
                  <div class="text-sm text-muted-foreground py-8 text-center">
                    {{ t('history.noDiscrepancy') }}
                  </div>
                </template>
              </FaTable>
            </div>
          </template>
        </template>

        <template #import>
          <FaAlert class="mb-4" :description="t('history.import.help')" />
          <div class="mb-4 flex flex-wrap gap-3 items-center">
            <FaSelect v-model="defaultSteamId" :options="userOptions" :placeholder="t('history.import.defaultUser')" class="w-60" />
            <label class="text-sm flex gap-2 items-center">
              <FaSwitch v-model="skipDuplicates" />
              {{ t('history.import.skipDuplicates') }}
            </label>
            <label class="text-sm flex gap-2 items-center">
              <FaSwitch v-model="skipOverlaps" />
              {{ t('history.import.skipOverlaps') }}
            </label>
            <FaButton variant="outline" @click="downloadTemplate">
              <FaIcon name="i-mdi:file-download-outline" />
              {{ t('history.import.template') }}
            </FaButton>
            <FaButton :loading="previewing" @click="fileInput?.click()">
              <FaIcon name="i-mdi:file-upload-outline" />
              {{ t('history.import.choose') }}
            </FaButton>
            <input ref="fileInput" type="file" accept=".csv,.tsv,.txt,text/csv" class="hidden" @change="onFileChange">
          </div>

          <div v-if="preview" class="mb-6 p-4 border rounded-lg bg-card space-y-4">
            <div class="flex flex-wrap gap-2 items-center justify-between">
              <div class="font-semibold flex gap-2 items-center">
                <FaIcon name="i-mdi:file-eye-outline" class="text-primary" />
                {{ fileName }}
              </div>
              <FaButton :disabled="importable === 0" :loading="committing" @click="commit">
                <FaIcon name="i-mdi:database-import" />
                {{ t('history.import.commit', { n: importable }) }}
              </FaButton>
            </div>
            <FaAlert v-if="preview.fatalError" variant="destructive" :description="t(`history.import.fatal.${preview.fatalError}`)" />
            <template v-else>
              <div class="flex flex-wrap gap-2">
                <FaTag v-for="m in preview.mapping" :key="m.field" variant="outline">
                  {{ t(`history.import.field.${m.field}`) }} ← {{ m.column }}
                </FaTag>
              </div>
              <div class="text-sm gap-3 grid grid-cols-2 md:grid-cols-6">
                <div><span class="text-muted-foreground">{{ t('history.import.total') }}</span> <b>{{ preview.totalRows }}</b></div>
                <div><span class="text-muted-foreground">{{ t('history.import.new') }}</span> <b>{{ preview.newRows }}</b></div>
                <div><span class="text-muted-foreground">{{ t('history.import.duplicate') }}</span> <b>{{ preview.duplicateRows }}</b></div>
                <div><span class="text-muted-foreground">{{ t('history.import.overlap') }}</span> <b>{{ preview.overlapRows }}</b></div>
                <div><span class="text-muted-foreground">{{ t('history.import.errors') }}</span> <b>{{ preview.errorRows }}</b></div>
                <div><span class="text-muted-foreground">{{ t('history.import.newTime') }}</span> <b>{{ statsStore.fmt(preview.newSeconds) }}</b></div>
              </div>
              <div v-if="preview.minStartTime" class="text-xs text-muted-foreground">
                {{ t('history.import.span', { from: time(preview.minStartTime), to: time(preview.maxEndTime), apps: preview.appCount }) }}
              </div>
              <FaTable :columns="sampleColumns" :data="preview.sample" stripe>
                <template #cell-app="{ row }">
                  {{ row.original.appName || `App ${row.original.appId}` }}
                </template>
                <template #cell-start="{ row }">
                  {{ time(row.original.startTime) }}
                </template>
                <template #cell-end="{ row }">
                  {{ time(row.original.endTime) }}
                </template>
                <template #cell-duration="{ row }">
                  {{ statsStore.fmt(row.original.duration) }}
                </template>
                <template #cell-status="{ row }">
                  <FaTag :variant="row.original.status === 'new' ? 'default' : 'secondary'">
                    {{ t(`history.import.${row.original.status}`) }}
                  </FaTag>
                </template>
              </FaTable>
              <template v-if="preview.errors.length">
                <div class="text-sm font-medium">
                  {{ t('history.import.errorList', { n: preview.errorRows }) }}
                </div>
                <FaTable :columns="errorColumns" :data="preview.errors" stripe>
                  <template #cell-code="{ row }">
                    {{ t(`history.import.code.${row.original.code}`) }}
                  </template>
                  <template #cell-value="{ row }">
                    <span class="font-mono">{{ row.original.value ?? '' }}</span>
                  </template>
                </FaTable>
              </template>
            </template>
          </div>

          <div class="font-semibold mb-2 flex gap-2 items-center">
            <FaIcon name="i-mdi:history" class="text-primary" />
            {{ t('history.import.batches') }}
          </div>
          <FaTable :columns="batchColumns" :data="batches" stripe>
            <template #cell-createdAt="{ row }">
              {{ time(row.original.createdAt) }}
            </template>
            <template #cell-count="{ row }">
              {{ row.original.remainingCount }} / {{ row.original.rowCount }}
            </template>
            <template #cell-actions="{ row }">
              <FaButton variant="ghost" :disabled="row.original.remainingCount === 0" @click="revert(row.original)">
                <FaIcon name="i-mdi:undo" />
                {{ t('history.import.revert') }}
              </FaButton>
            </template>
            <template #empty>
              <div class="text-sm text-muted-foreground py-8 text-center">
                {{ t('history.import.noBatches') }}
              </div>
            </template>
          </FaTable>
        </template>
      </FaTabs>
    </FaPageMain>
  </div>
</template>
