<script setup lang="ts">
import type { TableColumn } from '@fantastic-admin/components'
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { toast } from 'vue-sonner'
import { useDataSourceStore } from '@/store/modules/dataSource'
import dayjs from '@/utils/dayjs'
import { formatSessionDuration } from '@/utils/sessionDuration'

defineOptions({
  name: 'SteamHistory',
})

const { t, locale } = useI18n()
const electronApi = (window as Window).electron
const dataSourceStore = useDataSourceStore()

const loading = ref(false)
const reconciling = ref<PlaytimeReconcileResult | null>(null)

const filterAccount = ref<string>('all')
const filterStatus = ref<string>('all')
const keyword = ref('')
const page = ref(1)
const pageSize = ref(20)

const intervalsVisible = ref(false)
const intervalsItem = ref<PlaytimeReconcileItem | null>(null)

function appDisplayName(app: { appId: number, appName?: string | null, appNameLocalized?: string | null }): string {
  if (app.appNameLocalized) {
    try {
      const map = JSON.parse(app.appNameLocalized) as Record<string, string>
      const localized = map[locale.value] ?? map['zh-CN'] ?? map['en-US']
      if (localized) {
        return localized
      }
    }
    catch {
      // 非法 JSON 时回退原名
    }
  }
  return app.appName || `App ${app.appId}`
}

const items = computed(() => reconciling.value?.items ?? [])

const accountOptions = computed(() => {
  const seen = new Map<string, string>()
  for (const item of items.value) {
    if (!seen.has(item.steamId)) {
      seen.set(item.steamId, item.userPersonaName || item.steamId)
    }
  }
  return [
    { value: 'all', label: t('history.statusAll') },
    ...[...seen.entries()].map(([value, label]) => ({ value, label })),
  ]
})

const statusOptions = computed(() => [
  { value: 'all', label: t('history.statusAll') },
  { value: 'matched', label: t('history.statusMatched') },
  { value: 'elsewhere', label: t('history.statusElsewhere') },
  { value: 'localExceeds', label: t('history.statusLocalExceeds') },
  { value: 'baselineOnly', label: t('history.statusBaselineOnly') },
])

const filteredItems = computed(() => {
  const kw = keyword.value.trim().toLowerCase()
  return items.value.filter((item) => {
    if (filterAccount.value !== 'all' && item.steamId !== filterAccount.value) {
      return false
    }
    if (filterStatus.value !== 'all' && item.status !== filterStatus.value) {
      return false
    }
    if (kw) {
      const name = appDisplayName(item).toLowerCase()
      if (!name.includes(kw) && !String(item.appId).includes(kw)) {
        return false
      }
    }
    return true
  })
})

const pagedItems = computed(() =>
  filteredItems.value.slice((page.value - 1) * pageSize.value, page.value * pageSize.value),
)

const summary = computed(() => ({
  officialMinutes: items.value.reduce((sum, i) => sum + i.officialMinutes, 0),
  preTrackingSeconds: items.value.reduce((sum, i) => sum + i.preTrackingSeconds, 0),
  localSeconds: items.value.reduce((sum, i) => sum + i.localSinceBaselineSeconds, 0),
  unaccountedSeconds: items.value.reduce((sum, i) => sum + Math.max(0, i.unaccountedSeconds), 0),
}))

function formatHours(secondsOrMinutes: number, isMinutes = false): string {
  const hours = (isMinutes ? secondsOrMinutes * 60 : secondsOrMinutes) / 3600
  return hours >= 100 ? `${Math.round(hours)}h` : `${hours.toFixed(1)}h`
}

function relativeTime(unix?: number | null): string {
  if (!unix) {
    return '—'
  }
  const seconds = dayjs().unix() - unix
  if (seconds < 60) {
    return `${seconds}s`
  }
  if (seconds < 3600) {
    return `${Math.floor(seconds / 60)}m`
  }
  if (seconds < 86400) {
    return `${Math.floor(seconds / 3600)}h`
  }
  return `${Math.floor(seconds / 86400)}d`
}

const columns = computed<TableColumn<PlaytimeReconcileItem>[]>(() => [
  { id: 'app', header: t('history.columnApp'), minWidth: 160 },
  { id: 'account', header: t('history.columnAccount'), width: 140 },
  { id: 'source', header: t('history.columnSource'), width: 110 },
  { id: 'baseline', header: t('history.columnBaseline'), width: 150 },
  { id: 'official', header: t('history.columnOfficial'), width: 110 },
  { id: 'preInstall', header: t('history.columnPreInstall'), width: 110 },
  { id: 'localSince', header: t('history.columnLocalSince'), width: 120 },
  { id: 'unaccounted', header: t('history.columnUnaccounted'), width: 110 },
  { id: 'status', header: t('history.columnStatus'), width: 110 },
  { id: 'actions', header: t('history.columnActions'), width: 150, align: 'center', fixed: 'right' },
])

function statusVariant(status: PlaytimeReconcileItem['status']) {
  return status === 'elsewhere'
    ? 'destructive'
    : status === 'localExceeds' ? 'default' : 'secondary'
}

function statusLabel(status: PlaytimeReconcileItem['status']) {
  switch (status) {
    case 'elsewhere': return t('history.statusElsewhere')
    case 'localExceeds': return t('history.statusLocalExceeds')
    case 'baselineOnly': return t('history.statusBaselineOnly')
    default: return t('history.statusMatched')
  }
}

async function fetchReconciliation() {
  loading.value = true
  try {
    reconciling.value = await electronApi.historyReconcileGet()
  }
  catch (e: any) {
    toast.error(`${t('history.getFailed')}: ${e?.message || e}`)
  }
  finally {
    loading.value = false
  }
}

const snapshotRunning = ref(false)
async function runSnapshot() {
  if (snapshotRunning.value) {
    return
  }
  snapshotRunning.value = true
  try {
    const result = await electronApi.historySnapshotRun({
      source: 'all',
      refreshCm: dataSourceStore.statusOf('cm.session') === 'ok',
    })
    toast.success(t('history.snapshotDone', {
      inserted: result.inserted,
      updated: result.updated,
      baselines: result.baselinesCreated,
      skipped: result.skippedRunning,
      stale: result.stale,
    }))
    if (result.errors.length > 0) {
      toast.warning(t('history.snapshotErrors', { errors: result.errors.join(', ') }))
    }
    await fetchReconciliation()
  }
  catch (e: any) {
    toast.error(`${t('history.snapshotFailed')}: ${e?.message || e}`)
  }
  finally {
    snapshotRunning.value = false
  }
}

function resetBaseline(steamId: string | null, appIds: number[] | null) {
  useFaModal().confirm({
    title: t('common.confirm'),
    content: appIds ? t('history.resetBaselineConfirm') : t('history.resetAllBaselineConfirm'),
    onConfirm: async () => {
      try {
        const count = await electronApi.historyBaselineReset({ steamId, appIds })
        toast.success(t('history.resetBaselineDone', { count }))
        await fetchReconciliation()
      }
      catch (e: any) {
        toast.error(`${t('common.actionFailed')}: ${e?.message || e}`)
      }
    },
  })
}

function showIntervals(item: PlaytimeReconcileItem) {
  intervalsItem.value = item
  intervalsVisible.value = true
}

// ---- CSV 导入 ----

const importFile = ref<SessionImportFile | null>(null)
const importDefaultSteamId = ref<string>('')
const importSkipDuplicates = ref(true)
const importPreview = ref<SessionImportResult | null>(null)
const importRunning = ref(false)
const importUsers = ref<SteamUser[]>([])

const importUserOptions = computed(() =>
  importUsers.value.map(user => ({
    value: user.steamId,
    label: `${user.personaName ?? user.accountName} (${user.accountName})`,
  })),
)

const importBatches = ref<SessionImportBatch[]>([])

const previewColumns = computed<TableColumn<SessionImportPreviewRow>[]>(() => [
  { accessorKey: 'appId', header: 'AppId', width: 90 },
  { id: 'appName', header: t('history.columnApp'), minWidth: 140 },
  { id: 'account', header: t('history.columnAccount'), width: 140 },
  { accessorKey: 'startTime', header: t('history.columnStart'), width: 160 },
  { accessorKey: 'endTime', header: t('history.columnEnd'), width: 160 },
  { accessorKey: 'duration', header: t('history.columnDuration'), width: 100 },
  { id: 'flags', header: '', width: 90 },
])

const errorColumns = computed<TableColumn<SessionImportRowError>[]>(() => [
  { accessorKey: 'line', header: t('history.columnLine'), width: 80 },
  { accessorKey: 'code', header: t('history.columnError'), minWidth: 160 },
])

function rowErrorLabel(code: SessionImportRowError['code']) {
  switch (code) {
    case 'invalidAppId': return t('history.rowErrorInvalidAppId')
    case 'invalidSteamId': return t('history.rowErrorInvalidSteamId')
    case 'invalidStart': return t('history.rowErrorInvalidStart')
    case 'invalidEnd': return t('history.rowErrorInvalidEnd')
    case 'invalidRange': return t('history.rowErrorInvalidRange')
    case 'future': return t('history.rowErrorFuture')
    default: return code
  }
}

function importErrorLabel(error?: string | null) {
  switch (error) {
    case 'fileNotFound': return t('history.errorFileNotFound')
    case 'fileTooLarge': return t('history.errorFileTooLarge')
    case 'missingColumns': return t('history.errorMissingColumns')
    case 'empty': return t('history.errorEmpty')
    case 'importFailed': return t('history.importFailed')
    default: return error || t('history.importFailed')
  }
}

function formatFileSize(bytes: number): string {
  if (bytes >= 1024 * 1024) {
    return `${(bytes / 1024 / 1024).toFixed(1)} MB`
  }
  return `${Math.max(1, Math.round(bytes / 1024))} KB`
}

function downloadTemplate() {
  const content = `steamId,appId,start,end,durationSeconds,note,tags\n${SteamIdExample()},570,2024-01-01T20:00:00+08:00,2024-01-01T21:30:00+08:00,,example,tag1|tag2\n`
  const blob = new Blob([content], { type: 'text/csv' })
  const url = URL.createObjectURL(blob)
  const anchor = document.createElement('a')
  anchor.href = url
  anchor.download = 'sessions-template.csv'
  anchor.click()
  URL.revokeObjectURL(url)
}

function SteamIdExample() {
  return importUsers.value[0]?.steamId ?? '76561198000000000'
}

async function pickFile() {
  try {
    const file = await electronApi.historyImportPickFile()
    if (file) {
      importFile.value = file
      importPreview.value = null
    }
  }
  catch (e: any) {
    toast.error(`${t('common.actionFailed')}: ${e?.message || e}`)
  }
}

async function runImport(dryRun: boolean) {
  const file = importFile.value
  if (!file || importRunning.value) {
    return
  }
  importRunning.value = true
  try {
    const result = await electronApi.historyImportRun({
      token: file.token,
      dryRun,
      defaultSteamId: importDefaultSteamId.value || null,
      skipDuplicates: importSkipDuplicates.value,
    })
    if (result.error) {
      toast.error(importErrorLabel(result.error))
      importPreview.value = null
      return
    }
    if (dryRun) {
      importPreview.value = result
    }
    else {
      toast.success(t('history.importDone', { count: result.imported }))
      importFile.value = null
      importPreview.value = null
      await Promise.all([fetchBatches(), fetchReconciliation()])
    }
  }
  catch (e: any) {
    toast.error(`${t('history.importFailed')}: ${e?.message || e}`)
  }
  finally {
    importRunning.value = false
  }
}

async function fetchBatches() {
  try {
    importBatches.value = await electronApi.historyImportBatchesGet()
  }
  catch (e: any) {
    toast.error(`${t('common.getFailed')}: ${e?.message || e}`)
  }
}

function deleteBatch(batch: SessionImportBatch) {
  useFaModal().confirm({
    title: t('common.confirm'),
    content: t('history.deleteBatchConfirm', { count: batch.remainingCount }),
    onConfirm: async () => {
      try {
        const count = await electronApi.historyImportBatchDelete({ batchId: batch.id })
        toast.success(t('history.batchDeleted', { count }))
        await fetchBatches()
      }
      catch (e: any) {
        toast.error(`${t('common.actionFailed')}: ${e?.message || e}`)
      }
    },
  })
}

async function fetchImportUsers() {
  try {
    const [inRecords, loggedIn] = await Promise.all([
      electronApi.steamGetUsersInRecord(),
      electronApi.steamGetLoginUser().catch(() => [] as SteamUser[]),
    ])
    const merged = new Map<string, SteamUser>()
    for (const user of [...loggedIn, ...inRecords]) {
      if (!merged.has(user.steamId)) {
        merged.set(user.steamId, user)
      }
    }
    importUsers.value = [...merged.values()]
  }
  catch {
    // 用户列表失败不阻塞导入
  }
}

onMounted(async () => {
  await Promise.all([fetchReconciliation(), fetchBatches(), fetchImportUsers()])
})
</script>

<template>
  <div>
    <FaPageMain class="mb-0">
      <AppPageHeader
        icon="i-mdi:history"
        :title="t('menu.steamHistory')"
        :description="t('history.subtitle')"
      >
        <template #actions>
          <FaButton variant="outline" :loading="loading" @click="fetchReconciliation">
            <FaIcon name="i-mdi:refresh" />
            {{ t('common.refreshData') }}
          </FaButton>
          <FaButton :loading="snapshotRunning" @click="runSnapshot">
            <FaIcon name="i-mdi:camera-outline" />
            {{ t('history.snapshotNow') }}
          </FaButton>
        </template>
      </AppPageHeader>

      <!-- 汇总卡片 -->
      <div class="mb-4 gap-3 grid grid-cols-2 lg:grid-cols-5">
        <div class="text-white p-4 rounded-lg bg-[oklch(var(--primary))]">
          <div class="text-sm opacity-80">
            {{ t('history.cardOfficialTotal') }}
          </div>
          <div class="text-2xl font-bold mt-1">
            {{ formatHours(summary.officialMinutes, true) }}
          </div>
        </div>
        <div class="text-white p-4 rounded-lg bg-[oklch(var(--primary)/0.85)]">
          <div class="text-sm opacity-80">
            {{ t('history.cardPreInstall') }}
          </div>
          <div class="text-2xl font-bold mt-1">
            {{ formatHours(summary.preTrackingSeconds) }}
          </div>
        </div>
        <div class="text-white p-4 rounded-lg bg-[oklch(var(--primary)/0.7)]">
          <div class="text-sm opacity-80">
            {{ t('history.cardTrackedSince') }}
          </div>
          <div class="text-2xl font-bold mt-1">
            {{ formatHours(summary.localSeconds) }}
          </div>
        </div>
        <div class="text-white p-4 rounded-lg bg-[oklch(var(--primary)/0.55)]">
          <div class="text-sm opacity-80">
            {{ t('history.cardUnaccounted') }}
          </div>
          <div class="text-2xl font-bold mt-1">
            {{ formatHours(summary.unaccountedSeconds) }}
          </div>
        </div>
        <div class="text-white p-4 rounded-lg bg-[oklch(var(--primary)/0.4)]">
          <div class="text-sm opacity-80">
            {{ t('history.cardLastSnapshot') }}
          </div>
          <div class="text-2xl font-bold mt-1">
            <FaTooltip :text="reconciling?.lastRunAt ? dayjs.unix(reconciling.lastRunAt).format('YYYY-MM-DD HH:mm:ss') : ''">
              {{ relativeTime(reconciling?.lastRunAt) }}
            </FaTooltip>
          </div>
          <div class="text-xs mt-1 flex flex-wrap gap-1">
            <FaTag :variant="reconciling?.localConfigAvailable ? 'secondary' : 'destructive'">
              {{ t('history.sourceLocalConfig') }}
            </FaTag>
            <FaTag :variant="reconciling?.cmAvailable ? 'secondary' : 'destructive'">
              {{ t('history.sourceCm') }}
            </FaTag>
          </div>
        </div>
      </div>

      <!-- 筛选 -->
      <div class="mb-4 flex flex-wrap gap-3 items-center">
        <FaSelect
          v-model="filterAccount"
          :options="accountOptions"
          :placeholder="t('history.filterAccount')"
          class="min-w-44"
        />
        <FaSelect
          v-model="filterStatus"
          :options="statusOptions"
          :placeholder="t('history.filterStatus')"
          class="min-w-36"
        />
        <FaInput
          v-model="keyword"
          :placeholder="t('history.filterKeyword')"
          clearable
          class="w-56"
        />
        <FaButton variant="outline" @click="resetBaseline(null, null)">
          <FaIcon name="i-mdi:restore" />
          {{ t('history.resetAllBaseline') }}
        </FaButton>
      </div>

      <!-- 对账表 -->
      <FaTable
        stripe
        :columns="columns"
        :data="pagedItems"
      >
        <template #cell-app="{ row }">
          <RouterLink :to="`/game/${row.original.appId}`" class="hover:underline">
            {{ appDisplayName(row.original) }}
          </RouterLink>
        </template>
        <template #cell-account="{ row }">
          {{ row.original.userPersonaName || row.original.steamId }}
        </template>
        <template #cell-source="{ row }">
          <FaTag variant="outline">
            {{ row.original.source === 'cm' ? t('history.sourceCm') : t('history.sourceLocalConfig') }}
          </FaTag>
        </template>
        <template #cell-baseline="{ row }">
          <FaTooltip :text="dayjs.unix(row.original.baselineCapturedAt).format('YYYY-MM-DD HH:mm:ss')">
            {{ formatHours(row.original.baselineMinutes, true) }}
          </FaTooltip>
        </template>
        <template #cell-official="{ row }">
          {{ formatHours(row.original.officialMinutes, true) }}
        </template>
        <template #cell-preInstall="{ row }">
          {{ formatSessionDuration(row.original.preTrackingSeconds) }}
        </template>
        <template #cell-localSince="{ row }">
          {{ formatSessionDuration(row.original.localSinceBaselineSeconds) }}
        </template>
        <template #cell-unaccounted="{ row }">
          <span :class="row.original.unaccountedSeconds > 0 ? 'text-red-500' : row.original.unaccountedSeconds < 0 ? 'text-blue-500' : ''">
            {{ row.original.unaccountedSeconds > 0 ? '+' : '' }}{{ formatSessionDuration(Math.abs(row.original.unaccountedSeconds)) }}
          </span>
        </template>
        <template #cell-status="{ row }">
          <FaTag :variant="statusVariant(row.original.status)">
            {{ statusLabel(row.original.status) }}
          </FaTag>
        </template>
        <template #cell-actions="{ row }">
          <div class="flex-center gap-2">
            <FaTooltip :text="t('history.resetBaseline')">
              <FaButton
                variant="outline"
                size="icon-sm"
                @click="resetBaseline(row.original.steamId, [row.original.appId])"
              >
                <FaIcon name="i-mdi:restore" />
              </FaButton>
            </FaTooltip>
            <FaTooltip :text="t('history.intervalDetails')">
              <FaButton
                variant="outline"
                size="icon-sm"
                :disabled="row.original.intervals.length === 0"
                @click="showIntervals(row.original)"
              >
                <FaIcon name="i-mdi:format-list-bulleted" />
              </FaButton>
            </FaTooltip>
          </div>
        </template>
        <template #empty>
          <div class="text-gray-400 py-12 flex flex-col gap-2 items-center">
            <span class="i-mdi:history text-gray-300 h-20 w-20 inline-block" />
            <span>{{ t('history.emptyTitle') }}</span>
            <span class="text-sm">{{ t('history.emptyHint') }}</span>
            <FaButton class="mt-2" :loading="snapshotRunning" @click="runSnapshot">
              {{ t('history.snapshotNow') }}
            </FaButton>
          </div>
        </template>
      </FaTable>

      <FaPagination
        v-model:page="page"
        v-model:size="pageSize"
        :total="filteredItems.length"
        class="mt-2"
      />
    </FaPageMain>

    <!-- CSV 导入 -->
    <FaPageMain class="mb-0">
      <FaCard :title="t('history.importTitle')">
        <template #header>
          <div class="flex w-full items-center justify-between">
            <span>{{ t('history.importTitle') }}</span>
            <FaButton variant="outline" @click="downloadTemplate">
              <FaIcon name="i-mdi:download-outline" />
              {{ t('history.downloadTemplate') }}
            </FaButton>
          </div>
        </template>
        <div class="space-y-4">
          <p class="text-sm text-muted-foreground">
            {{ t('history.importColumnsHelp') }}
          </p>
          <div class="flex flex-wrap gap-3 items-center">
            <FaButton variant="outline" @click="pickFile">
              <FaIcon name="i-mdi:file-outline" />
              {{ t('history.pickFile') }}
            </FaButton>
            <span class="text-sm">
              {{ importFile ? `${importFile.fileName} (${formatFileSize(importFile.sizeBytes)})` : t('history.noFile') }}
            </span>
            <FaSelect
              v-model="importDefaultSteamId"
              :options="importUserOptions"
              :placeholder="t('history.defaultAccount')"
              class="min-w-44"
            />
            <label class="text-sm flex gap-2 items-center">
              <FaSwitch v-model="importSkipDuplicates" />
              {{ t('history.skipDuplicates') }}
            </label>
            <FaButton
              variant="outline"
              :disabled="!importFile"
              :loading="importRunning"
              @click="runImport(true)"
            >
              {{ t('history.preview') }}
            </FaButton>
            <FaButton
              :disabled="!importPreview || importPreview.valid === 0"
              :loading="importRunning"
              @click="runImport(false)"
            >
              {{ t('history.confirmImport') }}
            </FaButton>
          </div>

          <template v-if="importPreview">
            <p class="text-sm">
              {{ t('history.previewResult', {
                total: importPreview.total,
                valid: importPreview.valid,
                duplicates: importPreview.duplicates,
                errors: importPreview.errorCount,
              }) }}
            </p>
            <FaTable
              v-if="importPreview.errors.length > 0"
              stripe
              :columns="errorColumns"
              :data="importPreview.errors"
            >
              <template #cell-code="{ row }">
                {{ rowErrorLabel(row.original.code) }}
              </template>
            </FaTable>
            <FaTable
              v-if="importPreview.preview.length > 0"
              stripe
              :columns="previewColumns"
              :data="importPreview.preview"
            >
              <template #cell-appName="{ row }">
                {{ row.original.appName || `App ${row.original.appId}` }}
              </template>
              <template #cell-account="{ row }">
                {{ importUsers.find(u => u.steamId === row.original.steamId)?.personaName || row.original.steamId }}
              </template>
              <template #cell-startTime="{ row }">
                {{ dayjs.unix(row.original.startTime).format('YYYY-MM-DD HH:mm:ss') }}
              </template>
              <template #cell-endTime="{ row }">
                {{ dayjs.unix(row.original.endTime).format('YYYY-MM-DD HH:mm:ss') }}
              </template>
              <template #cell-duration="{ row }">
                {{ formatSessionDuration(row.original.duration) }}
              </template>
              <template #cell-flags="{ row }">
                <FaTag v-if="row.original.duplicate" variant="secondary">
                  {{ t('history.duplicateTag') }}
                </FaTag>
              </template>
            </FaTable>
          </template>
        </div>
      </FaCard>
    </FaPageMain>

    <!-- 导入批次 -->
    <FaPageMain class="mb-0">
      <FaCard :title="t('history.batchesTitle')">
        <div v-if="importBatches.length === 0" class="text-gray-400 py-8 text-center">
          —
        </div>
        <div v-else class="space-y-2">
          <div
            v-for="batch in importBatches"
            :key="batch.id"
            class="p-3 border rounded-lg flex flex-wrap gap-3 items-center"
          >
            <FaIcon name="i-mdi:file-delimited-outline" class="text-xl" />
            <div class="flex-1 min-w-0">
              <div class="text-sm font-medium truncate">
                {{ batch.fileName }}
              </div>
              <div class="text-xs text-muted-foreground">
                {{ dayjs.unix(batch.importedAt).format('YYYY-MM-DD HH:mm:ss') }}
                · {{ t('history.batchRows', { remaining: batch.remainingCount, total: batch.rowCount }) }}
              </div>
            </div>
            <FaButton variant="outline" @click="deleteBatch(batch)">
              <FaIcon name="i-mdi:delete-outline" />
              {{ t('history.deleteBatch') }}
            </FaButton>
          </div>
        </div>
      </FaCard>
    </FaPageMain>

    <!-- 差异明细 -->
    <FaModal
      v-model="intervalsVisible"
      :title="t('history.intervalDetails')"
      :show-cancel-button="false"
      :show-confirm-button="false"
    >
      <div v-if="intervalsItem" class="space-y-2">
        <div
          v-for="(interval, index) in intervalsItem.intervals"
          :key="index"
          class="text-sm p-3 border rounded-lg"
        >
          <div class="font-medium">
            {{ dayjs.unix(interval.from).format('YYYY-MM-DD HH:mm') }}
            → {{ dayjs.unix(interval.to).format('YYYY-MM-DD HH:mm') }}
          </div>
          <div class="text-muted-foreground mt-1 flex flex-wrap gap-4">
            <span>{{ t('history.intervalOfficial') }}: {{ formatSessionDuration(interval.officialDeltaSeconds) }}</span>
            <span>{{ t('history.intervalLocal') }}: {{ formatSessionDuration(interval.localSeconds) }}</span>
            <span :class="interval.unaccountedSeconds > 0 ? 'text-red-500' : 'text-blue-500'">
              {{ t('history.intervalUnaccounted') }}: {{ interval.unaccountedSeconds > 0 ? '+' : '-' }}{{ formatSessionDuration(Math.abs(interval.unaccountedSeconds)) }}
            </span>
          </div>
        </div>
      </div>
    </FaModal>
  </div>
</template>
