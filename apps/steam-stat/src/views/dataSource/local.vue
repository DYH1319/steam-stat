<script setup lang="ts">
import type { DeepPartial } from '@/utils/types'
import { Button, Empty, InputNumber, Spin, Switch, Tooltip } from 'ant-design-vue'
import { useI18n } from 'vue-i18n'
import { toast } from 'vue-sonner'
import { useIpc } from '@/composables/useIpc'
import { useDataSourceStore } from '@/store/modules/dataSource'
import { getAppSettings, updateAppSettings } from '@/utils/appSettings'
import { copyToClipboard } from '@/utils/clipboard'
import dayjs from '@/utils/dayjs.ts'
import { normalizePath } from '@/utils/path'

const { t } = useI18n()
const ipc = useIpc()
const dataSourceStore = useDataSourceStore()

const appSettings = ref<AppSettings>({ updateAppRunningStatusJob: {} } as AppSettings)
const settingsLoading = ref(false)
const jobStatus = ref<UpdateAppRunningStatusJobStatus | null>(null)

onMounted(async () => {
  settingsLoading.value = true
  try {
    const [settings, status] = await Promise.all([
      getAppSettings(),
      ipc.jobGetUpdateAppRunningStatusJobStatus(),
    ])
    appSettings.value = settings
    jobStatus.value = status
  }
  catch (e: any) {
    toast.error(`${t('common.getFailed')}: ${e?.message || e}`)
  }
  finally {
    settingsLoading.value = false
  }
})

// 应用检测设置：变更后同步刷新本地数据源中的任务状态
async function updateDetectionSettings(partialSettings: DeepPartial<AppSettings>) {
  settingsLoading.value = true
  try {
    const result = await updateAppSettings(partialSettings as Partial<AppSettings>)
    if (result) {
      if (partialSettings.updateAppRunningStatusJob?.enabled !== undefined) {
        toast.success(partialSettings.updateAppRunningStatusJob.enabled ? t('settings.detectionEnabled') : t('settings.detectionDisabled'))
      }
      if (partialSettings.updateAppRunningStatusJob?.intervalSeconds !== undefined) {
        if (partialSettings.updateAppRunningStatusJob.intervalSeconds === null) {
          toast.warning(t('settings.intervalNotNull'))
        }
        else {
          toast.success(t('settings.intervalSet', { seconds: partialSettings.updateAppRunningStatusJob.intervalSeconds }))
        }
      }
      await dataSourceStore.refreshLocalSources().catch(() => undefined)
      jobStatus.value = await ipc.jobGetUpdateAppRunningStatusJobStatus().catch(() => jobStatus.value)
    }
    else {
      toast.error(t('settings.saveFailed'))
    }
  }
  catch (e: any) {
    toast.error(`${t('settings.saveFailed')}: ${e?.message || e}`)
  }
  finally {
    settingsLoading.value = false
  }
}

async function onRefreshAll() {
  const results = await dataSourceStore.refreshAll()
  jobStatus.value = await ipc.jobGetUpdateAppRunningStatusJobStatus().catch(() => jobStatus.value)
  if (results.every(result => result.status === 'fulfilled')) {
    toast.success(t('dataSource.refreshAllSuccess'))
  }
  else {
    const failed = results.find((result): result is PromiseRejectedResult => result.status === 'rejected')
    toast.error(`${t('common.getFailed')}: ${(failed?.reason as Error)?.message || failed?.reason}`)
  }
}

const KIND_ORDER: SteamLocalDataSource['kind'][] = ['registry', 'file', 'job']

const groupedSources = computed(() => {
  const sources = dataSourceStore.localSources?.sources ?? []
  return KIND_ORDER
    .map(kind => ({ kind, sources: sources.filter(source => source.kind === kind) }))
    .filter(group => group.sources.length > 0)
})

const statusSummary = computed(() => {
  const summary: Record<SteamLocalDataSource['status'], number> = { ok: 0, missing: 0, parseFailed: 0 }
  for (const source of dataSourceStore.localSources?.sources ?? []) {
    summary[source.status] += 1
  }
  return summary
})

const refreshing = computed(() =>
  dataSourceStore.loading.local || dataSourceStore.loading.global || dataSourceStore.loading.libraryFolders)

const jobRunning = computed(() => jobStatus.value?.isRunning ?? appSettings.value.updateAppRunningStatusJob.enabled === true)

// 轮询刷新本地数据源时同步任务运行状态标签
watch(() => dataSourceStore.lastRefreshedAt.local, () => {
  void ipc.jobGetUpdateAppRunningStatusJobStatus()
    .then((status) => { jobStatus.value = status })
    .catch(() => undefined)
})

const sourceIcons: Record<SteamLocalDataSource['id'], string> = {
  'registry.steam': 'i-mdi:steam',
  'registry.activeProcess': 'i-mdi:application-cog-outline',
  'registry.apps': 'i-mdi:apps',
  'file.loginUsersVdf': 'i-mdi:account-multiple',
  'file.libraryFoldersVdf': 'i-mdi:folder-multiple',
  'file.appManifests': 'i-mdi:file-document-multiple',
  'file.configVdf': 'i-mdi:file-cog',
  'file.localConfigVdf': 'i-mdi:file-account',
  'job.appRunningStatus': 'i-mdi:radar',
}

const statusStyles: Record<SteamLocalDataSource['status'], { dot: string, text: string }> = {
  ok: { dot: 'bg-emerald-500', text: 'text-emerald-600 dark:text-emerald-400' },
  missing: { dot: 'bg-amber-500', text: 'text-amber-600 dark:text-amber-400' },
  parseFailed: { dot: 'bg-red-500', text: 'text-red-600 dark:text-red-400' },
}

const expandableIds = new Set<SteamLocalDataSource['id']>([
  'registry.steam',
  'registry.activeProcess',
  'file.libraryFoldersVdf',
  'job.appRunningStatus',
])
const expandedIds = ref<string[]>([])

function toggleSource(source: SteamLocalDataSource) {
  if (!expandableIds.has(source.id)) {
    return
  }
  const index = expandedIds.value.indexOf(source.id)
  if (index >= 0) {
    expandedIds.value.splice(index, 1)
  }
  else {
    expandedIds.value.push(source.id)
  }
}

const installPaths = computed(() => {
  const status = dataSourceStore.globalStatus
  return [
    { label: t('status.steamPath'), path: status?.steamPath },
    { label: t('status.steamExe'), path: status?.steamExePath },
    { label: t('status.steamClientDll32'), path: status?.steamClientDllPath },
    { label: t('status.steamClientDll64'), path: status?.steamClientDll64Path },
  ]
})

const runtimeSnapshot = computed(() => {
  const status = dataSourceStore.globalStatus
  return [
    {
      label: t('dataSource.local.fieldProcess'),
      value: Number(status?.steamPid) > 0 ? `${t('status.steamRunning')} (PID: ${status!.steamPid})` : t('status.steamNotRunning'),
    },
    {
      label: t('dataSource.local.fieldUser'),
      value: status?.activeUserSteamId || t('status.noUser'),
    },
    {
      label: t('dataSource.local.fieldApp'),
      value: Number(status?.runningAppId) > 0 ? `${t('status.appRunning')} (${status!.runningAppId})` : t('status.noApp'),
    },
  ]
})
</script>

<template>
  <div>
    <FaPageMain class="mb-0">
      <AppPageHeader
        icon="i-mdi:harddisk"
        :title="t('menu.dataSourceLocal')"
        :description="t('dataSource.local.subtitle')"
      >
        <template #actions>
          <div class="flex gap-2 items-center">
            <span class="text-sm op-70">{{ t('dataSource.pollInterval') }}</span>
            <InputNumber
              :value="dataSourceStore.pollIntervalSeconds"
              :min="5"
              :max="600"
              :step="1"
              class="w-24"
              @change="(value) => dataSourceStore.setPollInterval(Number(value) || 5)"
            />
          </div>
          <Button type="primary" :loading="refreshing" class="flex gap-1 items-center" @click="onRefreshAll">
            <template #icon>
              <span class="i-mdi:refresh h-4 w-4" />
            </template>
            {{ t('dataSource.refreshAll') }}
          </Button>
        </template>
      </AppPageHeader>

      <div class="space-y-4">
        <!-- 健康度概览 -->
        <div class="px-4 py-3 rounded-lg bg-[var(--g-container-bg)] flex flex-wrap gap-x-6 gap-y-2 items-center">
          <span v-for="status in (['ok', 'missing', 'parseFailed'] as const)" :key="status" class="flex gap-1.5 items-center">
            <span class="rounded-full h-2 w-2" :class="statusStyles[status].dot" />
            <span :class="statusStyles[status].text">{{ statusSummary[status] }} {{ t(`dataSource.status.${status}`) }}</span>
          </span>
          <span v-if="dataSourceStore.localSources" class="text-sm ms-auto op-60">
            {{ t('dataSource.local.checkedAt', { time: dayjs.unix(dataSourceStore.localSources.checkedAt).format('YYYY-MM-DD HH:mm:ss') }) }}
          </span>
        </div>

        <!-- 数据源清单 -->
        <div class="rounded-lg bg-[var(--g-container-bg)]">
          <Spin :spinning="dataSourceStore.loading.local && !dataSourceStore.localSources">
            <template v-if="groupedSources.length > 0">
              <div
                v-for="group in groupedSources"
                :key="group.kind"
                class="border-t border-[var(--el-border-color-lighter)] first:border-t-0"
              >
                <div class="text-sm font-medium px-4 pb-1 pt-4 op-60">
                  {{ t(`dataSource.local.group.${group.kind}`) }}
                </div>
                <template v-for="source in group.sources" :key="source.id">
                  <div
                    class="px-4 py-3 flex gap-3 items-center"
                    :class="expandableIds.has(source.id) ? 'cursor-pointer transition-colors hover:bg-black/3 dark:hover:bg-white/3' : ''"
                    @click="toggleSource(source)"
                  >
                    <span
                      v-if="expandableIds.has(source.id)"
                      class="i-mdi:chevron-right op-50 shrink-0 h-4 w-4 transition-transform"
                      :class="{ 'rotate-90': expandedIds.includes(source.id) }"
                    />
                    <span v-else class="shrink-0 h-4 w-4" />
                    <span :class="sourceIcons[source.id]" class="text-primary shrink-0 h-5 w-5" />
                    <div class="flex-1 min-w-0">
                      <div class="font-medium">
                        {{ dataSourceStore.labelOf(source.id) }}
                      </div>
                      <div v-if="source.path" class="text-sm font-mono op-60 truncate" :title="normalizePath(source.path)">
                        {{ normalizePath(source.path) }}
                      </div>
                    </div>
                    <Button
                      v-if="source.path"
                      type="text"
                      class="flex shrink-0 h-7 items-center"
                      @click.stop="copyToClipboard(normalizePath(source.path))"
                    >
                      <span class="i-mdi:content-copy h-3.5 w-3.5" />
                    </Button>
                    <span
                      v-if="source.id === 'job.appRunningStatus'"
                      class="text-sm text-primary px-2 py-0.5 border border-primary/40 rounded-lg bg-primary/10 shrink-0"
                    >
                      {{ jobRunning ? t('dataSource.local.jobRunning') : t('dataSource.local.jobStopped') }}
                    </span>
                    <Tooltip :title="source.error ?? undefined">
                      <span class="flex shrink-0 gap-1.5 items-center">
                        <span class="rounded-full h-2 w-2" :class="statusStyles[source.status].dot" />
                        <span :class="statusStyles[source.status].text">{{ t(`dataSource.status.${source.status}`) }}</span>
                      </span>
                    </Tooltip>
                    <span class="text-sm text-end op-60 shrink-0 w-24">
                      <template v-if="source.itemCount != null">
                        {{ t('dataSource.local.itemCount', { count: source.itemCount }) }}
                      </template>
                      <template v-if="source.failedCount">
                        <span class="text-red-500 ms-1">{{ t('dataSource.local.failedCount', { count: source.failedCount }) }}</span>
                      </template>
                    </span>
                  </div>

                  <!-- 展开详情 -->
                  <div
                    v-if="expandedIds.includes(source.id)"
                    class="px-4 pb-4 ps-11 pt-3 border-t border-[var(--el-border-color-lighter)]"
                  >
                    <!-- Steam 安装路径 -->
                    <template v-if="source.id === 'registry.steam'">
                      <div class="text-sm font-medium mb-2 op-60">
                        {{ t('dataSource.local.detailInstall') }}
                      </div>
                      <div class="space-y-1">
                        <div v-for="item in installPaths" :key="item.label" class="flex gap-3 items-center">
                          <span class="op-70 shrink-0 w-44">{{ item.label }}</span>
                          <code class="text-sm font-mono flex-1 min-w-0 truncate" :title="item.path ? normalizePath(item.path) : ''">
                            {{ item.path ? normalizePath(item.path) : '-' }}
                          </code>
                          <Button
                            v-if="item.path"
                            type="text"
                            class="flex shrink-0 h-7 items-center"
                            @click="copyToClipboard(normalizePath(item.path))"
                          >
                            <span class="i-mdi:content-copy h-3.5 w-3.5" />
                          </Button>
                        </div>
                      </div>
                    </template>

                    <!-- 实时运行快照 -->
                    <template v-else-if="source.id === 'registry.activeProcess'">
                      <div class="text-sm font-medium mb-2 op-60">
                        {{ t('dataSource.local.detailRuntime') }}
                      </div>
                      <div v-if="dataSourceStore.globalStatus" class="gap-3 grid sm:grid-cols-3">
                        <div v-for="field in runtimeSnapshot" :key="field.label">
                          <div class="text-sm op-60">
                            {{ field.label }}
                          </div>
                          <div class="font-medium">
                            {{ field.value }}
                          </div>
                        </div>
                      </div>
                      <div v-else class="op-60">
                        {{ t('status.notDetected') }}
                      </div>
                    </template>

                    <!-- 库目录列表 -->
                    <template v-else-if="source.id === 'file.libraryFoldersVdf'">
                      <div class="text-sm font-medium mb-2 op-60">
                        {{ t('status.libraryFolders') }}
                      </div>
                      <div v-if="dataSourceStore.libraryFolders.length > 0" class="space-y-1">
                        <div v-for="folder in dataSourceStore.libraryFolders" :key="folder" class="flex gap-3 items-center">
                          <span class="i-mdi:folder op-60 shrink-0 h-4 w-4" />
                          <code class="text-sm font-mono flex-1 min-w-0 truncate" :title="normalizePath(folder)">
                            {{ normalizePath(folder) }}
                          </code>
                          <Button
                            type="text"
                            class="flex shrink-0 h-7 items-center"
                            @click="copyToClipboard(normalizePath(folder))"
                          >
                            <span class="i-mdi:content-copy h-3.5 w-3.5" />
                          </Button>
                        </div>
                      </div>
                      <div v-else class="op-60">
                        {{ t('status.noLibraryFolders') }}
                      </div>
                    </template>

                    <!-- 应用检测设置 -->
                    <template v-else-if="source.id === 'job.appRunningStatus'">
                      <div class="text-sm font-medium mb-2 op-60">
                        {{ t('settings.appDetection') }}
                      </div>
                      <div class="setting-row">
                        <div class="setting-label">
                          <div>
                            <div class="font-medium">
                              {{ t('settings.enableDetection') }}
                            </div>
                            <div class="text-sm op-60">
                              {{ t('settings.detectionDesc') }}
                            </div>
                          </div>
                        </div>
                        <Switch
                          v-model:checked="appSettings.updateAppRunningStatusJob.enabled"
                          :loading="settingsLoading"
                          @change="updateDetectionSettings({ updateAppRunningStatusJob: { enabled: appSettings.updateAppRunningStatusJob.enabled } })"
                        />
                      </div>
                      <div class="setting-row" :class="{ 'opacity-50': !appSettings.updateAppRunningStatusJob.enabled }">
                        <div class="setting-label">
                          <div>
                            <div class="font-medium">
                              {{ t('settings.detectionInterval') }}
                            </div>
                            <div class="text-sm op-60">
                              {{ t('settings.detectionIntervalDesc') }}
                            </div>
                          </div>
                        </div>
                        <InputNumber
                          v-model:value="appSettings.updateAppRunningStatusJob.intervalSeconds"
                          :min="1"
                          :max="60"
                          :step="1"
                          :disabled="!appSettings.updateAppRunningStatusJob.enabled || settingsLoading"
                          class="w-32"
                          @blur="updateDetectionSettings({ updateAppRunningStatusJob: { intervalSeconds: appSettings.updateAppRunningStatusJob.intervalSeconds } })"
                          @step="updateDetectionSettings({ updateAppRunningStatusJob: { intervalSeconds: appSettings.updateAppRunningStatusJob.intervalSeconds } })"
                        />
                      </div>
                    </template>
                  </div>
                </template>
              </div>
            </template>
            <Empty v-else :description="t('dataSource.local.noSources')" class="py-8" />
          </Spin>
        </div>
      </div>
    </FaPageMain>
  </div>
</template>

<style scoped>
.setting-row {
  display: flex;
  gap: 16px;
  align-items: center;
  justify-content: space-between;
  padding: 10px 0;
}

.setting-label {
  display: flex;
  flex: 1;
  gap: 12px;
  align-items: center;
  min-width: 0;
}
</style>
