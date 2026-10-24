<script setup lang="ts">
import type { TableColumn, TableSortingState } from '@fantastic-admin/components'
import { DatePicker, RangePicker } from 'ant-design-vue'
import { computed, onActivated, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import { toast } from 'vue-sonner'
import dayjs from '@/utils/dayjs'
import { formatSessionDuration } from '@/utils/sessionDuration'

defineOptions({
  name: 'SteamSessions',
})

const { t, locale } = useI18n()
const route = useRoute()
const router = useRouter()

const electronApi = (window as Window).electron

// Electron.NET 宿主对同一 channel 的并发 invoke 会互删回复监听（见 utils/appSettings.ts），
// 因此 sessionListGet 串行排队，其它 channel 不受影响。
let listQueue: Promise<unknown> = Promise.resolve()
function sessionListGetQueued(param: SessionListQueryRequest): Promise<SessionList> {
  const task = listQueue.then(() => electronApi.sessionListGet(param))
  listQueue = task.catch(() => undefined)
  return task
}

const loading = ref(false)
const items = ref<SessionRecord[]>([])
const total = ref(0)
const page = ref(1)
const pageSize = ref(20)
const sorting = ref<TableSortingState>([{ id: 'startTime', desc: true }])

const filters = ref<{
  steamIds?: string[]
  appId?: number
  dateRange?: [string, string]
  status?: 'all' | 'completed' | 'ongoing' | 'discarded'
  tag?: string
}>({ status: 'all' })
const keyword = ref('')

// 从统计页跳转携带的 query（appId / start / end）应用一次后清除，避免 keepAlive 反复应用
function applyRouteQuery() {
  const { appId, start, end } = route.query as Record<string, string | undefined>
  if (!appId && !start && !end) {
    return false
  }
  filters.value = {
    ...filters.value,
    appId: appId ? Number(appId) : undefined,
    dateRange: start ? [start, end ?? start] : undefined,
  }
  router.replace({ query: {} })
  return true
}

const usersInRecords = ref<SteamUser[]>([])
const sessionApps = ref<SessionApp[]>([])
const allTags = ref<string[]>([])

const selection = ref<SessionRecord[]>([])
const tableRef = ref()

function clearSelection() {
  selection.value = []
  tableRef.value?.table?.resetRowSelection?.()
}

const editVisible = ref(false)
const editing = ref<SessionRecord | null>(null)
const editStart = ref<dayjs.Dayjs>()
const editEnd = ref<dayjs.Dayjs>()
const editNote = ref('')
const editTagsInput = ref('')

const editParsedTags = computed(() =>
  editTagsInput.value
    .split(',')
    .map(tag => tag.trim())
    .filter(tag => tag.length > 0)
    .filter((tag, index, arr) => arr.findIndex(other => other.toLowerCase() === tag.toLowerCase()) === index),
)

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

const userOptions = computed(() =>
  usersInRecords.value.map(user => ({
    value: user.steamId,
    label: `${user.personaName ?? user.accountName} (${user.accountName})`,
  })),
)

const appOptions = computed(() =>
  sessionApps.value.map(app => ({
    value: app.appId,
    label: `${appDisplayName(app)} (${app.sessionCount})`,
  })),
)

const statusOptions = computed(() => [
  { value: 'all', label: t('sessions.statusAll') },
  { value: 'completed', label: t('sessions.statusCompleted') },
  { value: 'ongoing', label: t('sessions.statusOngoing') },
  { value: 'discarded', label: t('sessions.statusDiscarded') },
])

const tagOptions = computed(() => allTags.value.map(tag => ({ value: tag, label: tag })))

const columns = computed<TableColumn<SessionRecord>[]>(() => [
  { type: 'selection', fixed: 'left', width: 48 },
  { id: 'app', header: t('sessions.columnApp'), minWidth: 160 },
  { id: 'user', header: t('sessions.columnUser'), width: 140 },
  { accessorKey: 'startTime', header: t('sessions.columnStart'), width: 160, enableSorting: true },
  { id: 'end', header: t('sessions.columnEnd'), width: 160 },
  { accessorKey: 'duration', header: t('sessions.columnDuration'), width: 110, enableSorting: true },
  { accessorKey: 'idleDuration', header: t('sessions.columnIdle'), width: 110, enableSorting: true },
  { id: 'effective', header: t('sessions.columnEffective'), width: 110 },
  { id: 'foreground', header: t('sessions.columnForeground'), width: 110 },
  { id: 'device', header: t('sessions.columnDevice'), width: 120 },
  { id: 'status', header: t('sessions.columnStatus'), width: 120 },
  { id: 'note', header: t('sessions.columnNote'), minWidth: 160 },
  { id: 'actions', header: t('sessions.columnActions'), width: 110, align: 'center', fixed: 'right' },
])

function statusVariant(status: SessionRecord['status']) {
  return status === 'ongoing' ? 'default' : status === 'discarded' ? 'destructive' : 'secondary'
}

function statusLabel(status: SessionRecord['status']) {
  return status === 'ongoing'
    ? t('sessions.statusOngoing')
    : status === 'discarded' ? t('sessions.statusDiscarded') : t('sessions.statusCompleted')
}

function endReasonLabel(reason?: string | null) {
  switch (reason) {
    case 'exited': return t('sessions.endReasonExited')
    case 'suspended': return t('sessions.endReasonSuspended')
    case 'recovered': return t('sessions.endReasonRecovered')
    case 'ended': return t('sessions.endReasonEnded')
    case 'discarded': return t('sessions.endReasonDiscarded')
    default: return ''
  }
}

async function fetchSessions() {
  loading.value = true
  try {
    const sortField = sorting.value[0]?.id
    const param: SessionListQueryRequest = {
      page: page.value,
      pageSize: pageSize.value,
      steamIds: filters.value.steamIds?.length ? filters.value.steamIds : null,
      appIds: filters.value.appId != null ? [filters.value.appId] : null,
      startDate: filters.value.dateRange?.[0] ? dayjs(filters.value.dateRange[0], 'YYYY-MM-DD').unix() : null,
      endDate: filters.value.dateRange?.[1] ? dayjs(filters.value.dateRange[1], 'YYYY-MM-DD').unix() + 86400 - 1 : null,
      keyword: keyword.value.trim() || null,
      tag: filters.value.tag || null,
      status: filters.value.status || 'all',
      sortField: sortField === 'duration' || sortField === 'idleDuration' ? sortField : 'startTime',
      sortOrder: sorting.value[0]?.desc === false ? 'asc' : 'desc',
    }
    const res = await sessionListGetQueued(param)
    items.value = res.items
    total.value = res.total
  }
  catch (e: any) {
    toast.error(`${t('sessions.getFailed')}: ${e?.message || e}`)
  }
  finally {
    loading.value = false
  }
}

async function fetchFilterSources() {
  try {
    const [users, apps, tags] = await Promise.all([
      electronApi.steamGetUsersInRecord(),
      electronApi.sessionAppsGet(),
      electronApi.sessionTagsGet(),
    ])
    usersInRecords.value = users
    sessionApps.value = apps
    allTags.value = tags
  }
  catch (e: any) {
    toast.error(`${t('common.getFailed')}: ${e?.message || e}`)
  }
}

function applyFilters() {
  page.value = 1
  fetchSessions()
}

let keywordTimer: ReturnType<typeof setTimeout> | undefined
let suppressFilterWatch = false
watch(keyword, () => {
  if (suppressFilterWatch) {
    return
  }
  clearTimeout(keywordTimer)
  keywordTimer = setTimeout(applyFilters, 300)
})

watch(filters, () => {
  if (!suppressFilterWatch) {
    applyFilters()
  }
}, { deep: true })

function resetFilters() {
  clearTimeout(keywordTimer)
  suppressFilterWatch = true
  keyword.value = ''
  filters.value = { status: 'all' }
  page.value = 1
  suppressFilterWatch = false
  applyFilters()
}

function onSortingChange(value: TableSortingState) {
  sorting.value = value.length > 0 ? [value[0]] : [{ id: 'startTime', desc: true }]
  page.value = 1
  fetchSessions()
}

function errorToast(error?: string | null) {
  const key = {
    notFound: 'sessions.errorNotFound',
    ongoing: 'sessions.errorOngoing',
    invalidRange: 'sessions.errorInvalidRange',
    tooFew: 'sessions.errorTooFew',
    discarded: 'sessions.errorDiscarded',
    mismatch: 'sessions.errorMismatch',
  }[error ?? ''] ?? 'common.actionFailed'
  toast.error(t(key))
}

function onEdit(row: SessionRecord) {
  editing.value = row
  editStart.value = dayjs.unix(row.startTime)
  editEnd.value = row.endTime ? dayjs.unix(row.endTime) : undefined
  editNote.value = row.note ?? ''
  editTagsInput.value = row.tags.join(', ')
  editVisible.value = true
}

async function submitEdit() {
  const row = editing.value
  if (!row || !editStart.value || !editEnd.value) {
    return
  }
  const result = await electronApi.sessionUpdate({
    id: row.id,
    startTime: editStart.value.unix(),
    endTime: editEnd.value.unix(),
    note: editNote.value,
    tags: editParsedTags.value,
  })
  if (result.success) {
    toast.success(t('sessions.updated'))
    editVisible.value = false
    fetchSessions()
  }
  else {
    errorToast(result.error)
  }
}

function stepBackIfBeyondLastPage() {
  const lastPage = Math.max(1, Math.ceil(total.value / pageSize.value))
  if (page.value > lastPage) {
    page.value = lastPage
  }
}

function onDelete(row: SessionRecord) {
  useFaModal().confirm({
    title: t('common.confirm'),
    content: t('sessions.deleteOneConfirm'),
    onConfirm: async () => {
      try {
        const count = await electronApi.sessionDelete({ ids: [row.id] })
        toast.success(t('sessions.deleted', { count }))
        total.value = Math.max(0, total.value - count)
        stepBackIfBeyondLastPage()
        fetchSessions()
      }
      catch (e: any) {
        toast.error(`${t('common.actionFailed')}: ${e?.message || e}`)
      }
    },
  })
}

function onBatchDelete() {
  const rows = selection.value
  if (rows.length === 0) {
    return
  }
  useFaModal().confirm({
    title: t('common.confirm'),
    content: t('sessions.deleteConfirm', { count: rows.length }),
    onConfirm: async () => {
      try {
        const count = await electronApi.sessionDelete({ ids: rows.map(r => r.id) })
        toast.success(t('sessions.deleted', { count }))
        clearSelection()
        total.value = Math.max(0, total.value - count)
        stepBackIfBeyondLastPage()
        fetchSessions()
      }
      catch (e: any) {
        toast.error(`${t('common.actionFailed')}: ${e?.message || e}`)
      }
    },
  })
}

function onBatchMerge() {
  const rows = selection.value
  if (rows.length < 2) {
    return
  }
  useFaModal().confirm({
    title: t('common.confirm'),
    content: t('sessions.mergeConfirm', { count: rows.length }),
    onConfirm: async () => {
      try {
        const result = await electronApi.sessionMerge({ ids: rows.map(r => r.id) })
        if (result.success) {
          toast.success(t('sessions.merged'))
          clearSelection()
          total.value = Math.max(0, total.value - rows.length + 1)
          stepBackIfBeyondLastPage()
          fetchSessions()
          fetchFilterSources()
        }
        else {
          errorToast(result.error)
        }
      }
      catch (e: any) {
        toast.error(`${t('common.actionFailed')}: ${e?.message || e}`)
      }
    },
  })
}

onMounted(async () => {
  applyRouteQuery()
  await fetchFilterSources()
  await fetchSessions()
})

onActivated(() => {
  if (applyRouteQuery()) {
    // query 已消费并在 filters 上生效，watch 会触发重新加载
  }
})
</script>

<template>
  <div>
    <FaPageMain class="mb-0">
      <AppPageHeader
        icon="i-mdi:timeline-clock-outline"
        :title="t('menu.steamSessions')"
        :description="t('sessions.subtitle')"
      >
        <template #actions>
          <FaButton variant="outline" :loading="loading" @click="fetchSessions">
            <FaIcon name="i-mdi:refresh" />
            {{ t('common.refreshData') }}
          </FaButton>
        </template>
      </AppPageHeader>

      <div class="mb-4 flex flex-wrap gap-3 items-center">
        <FaSelect
          v-model="filters.steamIds"
          multiple
          :options="userOptions"
          :placeholder="t('sessions.filterUser')"
          class="min-w-44"
        />
        <FaSelect
          v-model="filters.appId"
          :options="appOptions"
          :placeholder="t('sessions.filterApp')"
          class="min-w-44"
        />
        <RangePicker
          v-model:value="filters.dateRange"
          :placeholder="[t('useRecord.startDate'), t('useRecord.endDate')]"
          format="YYYY-MM-DD"
          value-format="YYYY-MM-DD"
        />
        <FaSelect
          v-model="filters.status"
          :options="statusOptions"
          :placeholder="t('sessions.filterStatus')"
          class="min-w-32"
        />
        <FaSelect
          v-model="filters.tag"
          :options="tagOptions"
          :placeholder="t('sessions.filterTag')"
          class="min-w-32"
        />
        <FaInput
          v-model="keyword"
          :placeholder="t('sessions.filterKeyword')"
          clearable
          class="w-56"
        />
        <FaButton variant="outline" @click="resetFilters">
          <FaIcon name="i-mdi:restart" />
          {{ t('sessions.resetFilter') }}
        </FaButton>
      </div>

      <FaTable
        ref="tableRef"
        row-key="id"
        selectable
        multiple
        stripe
        sortable
        :columns="columns"
        :data="items"
        :sorting="sorting"
        :enable-multi-sort="false"
        @selection-change="selection = $event"
        @sorting-change="onSortingChange"
      >
        <template #toolbar>
          <div class="flex flex-1 gap-2 items-center">
            <FaButton variant="outline" :disabled="selection.length < 2" @click="onBatchMerge">
              <FaIcon name="i-mdi:call-merge" />
              {{ t('sessions.mergeSelected') }}
            </FaButton>
            <FaButton variant="outline" :disabled="selection.length === 0" @click="onBatchDelete">
              <FaIcon name="i-mdi:delete-outline" />
              {{ t('sessions.deleteSelected') }}
            </FaButton>
          </div>
        </template>

        <template #cell-app="{ row }">
          <RouterLink :to="`/game/${row.original.appId}`" class="hover:underline">
            {{ appDisplayName(row.original) }}
          </RouterLink>
        </template>
        <template #cell-user="{ row }">
          {{ row.original.userPersonaName || row.original.steamId }}
        </template>
        <template #cell-startTime="{ row }">
          {{ dayjs.unix(row.original.startTime).format('YYYY-MM-DD HH:mm:ss') }}
        </template>
        <template #cell-end="{ row }">
          {{ row.original.endTime ? dayjs.unix(row.original.endTime).format('YYYY-MM-DD HH:mm:ss') : t('sessions.unknownValue') }}
        </template>
        <template #cell-duration="{ row }">
          {{ formatSessionDuration(row.original.duration) }}
        </template>
        <template #cell-idleDuration="{ row }">
          {{ formatSessionDuration(row.original.idleDuration) }}
        </template>
        <template #cell-effective="{ row }">
          {{ row.original.duration != null ? formatSessionDuration(Math.max(0, row.original.duration - row.original.idleDuration)) : t('sessions.unknownValue') }}
        </template>
        <template #cell-foreground="{ row }">
          {{ row.original.foregroundDuration != null ? formatSessionDuration(row.original.foregroundDuration) : t('sessions.unknownValue') }}
        </template>
        <template #cell-device="{ row }">
          {{ row.original.source === 'imported' ? t('sessions.deviceImported') : (row.original.deviceName || t('sessions.unknownValue')) }}
        </template>
        <template #cell-status="{ row }">
          <FaTooltip :text="endReasonLabel(row.original.endReason)" :disabled="!row.original.endReason">
            <FaTag :variant="statusVariant(row.original.status)">
              {{ statusLabel(row.original.status) }}
            </FaTag>
          </FaTooltip>
        </template>
        <template #cell-note="{ row }">
          <div class="flex flex-wrap gap-1 items-center">
            <span v-if="row.original.note" class="max-w-40 truncate">{{ row.original.note }}</span>
            <FaTag v-for="tag in row.original.tags" :key="tag" variant="outline">
              {{ tag }}
            </FaTag>
            <FaTag v-if="row.original.source === 'merged'" variant="secondary">
              {{ t('sessions.sourceMerged') }}
            </FaTag>
            <FaTag v-if="row.original.source === 'imported'" variant="secondary">
              {{ t('sessions.sourceImported') }}
            </FaTag>
          </div>
        </template>
        <template #cell-actions="{ row }">
          <div class="flex-center gap-2">
            <FaButton
              variant="outline"
              size="icon-sm"
              :disabled="row.original.status === 'ongoing'"
              @click="onEdit(row.original)"
            >
              <FaIcon name="i-ri:edit-line" />
            </FaButton>
            <FaButton
              variant="outline"
              size="icon-sm"
              :disabled="row.original.status === 'ongoing'"
              @click="onDelete(row.original)"
            >
              <FaIcon name="i-mdi:delete-outline" />
            </FaButton>
          </div>
        </template>
        <template #empty>
          <div class="text-gray-400 py-12 flex flex-col gap-2 items-center">
            <span class="i-mdi:timeline-clock-outline text-gray-300 h-20 w-20 inline-block" />
            <span>{{ t('sessions.empty') }}</span>
          </div>
        </template>
      </FaTable>

      <FaPagination
        v-model:page="page"
        v-model:size="pageSize"
        :total="total"
        class="mt-2"
        @page-change="fetchSessions"
        @size-change="applyFilters"
      />
    </FaPageMain>

    <FaModal
      v-model="editVisible"
      :title="t('sessions.editTitle')"
      :show-cancel-button="true"
      @confirm="submitEdit"
    >
      <div class="space-y-4">
        <div class="flex gap-3 items-center">
          <span class="text-sm shrink-0 w-20">{{ t('sessions.editStart') }}</span>
          <DatePicker v-model:value="editStart" show-time class="flex-1" />
        </div>
        <div class="flex gap-3 items-center">
          <span class="text-sm shrink-0 w-20">{{ t('sessions.editEnd') }}</span>
          <DatePicker v-model:value="editEnd" show-time class="flex-1" />
        </div>
        <div class="flex gap-3 items-start">
          <span class="text-sm mt-1 shrink-0 w-20">{{ t('sessions.editNote') }}</span>
          <FaTextarea v-model="editNote" class="flex-1" rows="3" maxlength="2000" />
        </div>
        <div class="flex gap-3 items-start">
          <span class="text-sm mt-1 shrink-0 w-20">{{ t('sessions.editTags') }}</span>
          <div class="flex-1 space-y-2">
            <FaInput v-model="editTagsInput" :placeholder="t('sessions.editTagsPlaceholder')" class="w-full" />
            <div class="flex flex-wrap gap-1">
              <FaTag v-for="tag in editParsedTags" :key="tag" variant="outline">
                {{ tag }}
              </FaTag>
            </div>
          </div>
        </div>
      </div>
    </FaModal>
  </div>
</template>
