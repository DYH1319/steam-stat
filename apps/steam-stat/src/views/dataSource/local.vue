<script setup lang="ts">
import type { DeepPartial } from '@/utils/types'
import { Button, Empty, InputNumber, Spin, Switch, Table, Tag, Tooltip } from 'ant-design-vue'
import { useI18n } from 'vue-i18n'
import { toast } from 'vue-sonner'
import { useDataSourceStore } from '@/store/modules/dataSource'
import { getAppSettings, updateAppSettings } from '@/utils/appSettings'
import { copyToClipboard } from '@/utils/clipboard'
import dayjs from '@/utils/dayjs.ts'

const { t } = useI18n()
const dataSourceStore = useDataSourceStore()

const appSettings = ref<AppSettings>({ updateAppRunningStatusJob: {} } as AppSettings)
const settingsLoading = ref(false)

onMounted(async () => {
  settingsLoading.value = true
  try {
    appSettings.value = await getAppSettings()
  }
  catch (e: any) {
    toast.error(`${t('common.getFailed')}: ${e?.message || e}`)
  }
  finally {
    settingsLoading.value = false
  }
})

// 应用检测设置（由设置页迁移而来）：变更后同步刷新本地数据源中的任务状态
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

function formatLastRefreshed(section: 'global' | 'local' | 'libraryFolders'): string {
  const timestamp = dataSourceStore.lastRefreshedAt[section]
  return timestamp ? dayjs(timestamp).format('YYYY-MM-DD HH:mm:ss') : ''
}

async function onRefreshGlobalStatus() {
  try {
    await dataSourceStore.refreshGlobalStatus()
    toast.success(t('status.getSuccess'))
  }
  catch (e: any) {
    toast.error(`${t('common.getFailed')}: ${e?.message || e}`)
  }
}

async function onRefreshLibraryFolders() {
  try {
    await dataSourceStore.refreshLibraryFolders()
    toast.success(t('status.getLibrarySuccess'))
  }
  catch (e: any) {
    toast.error(`${t('common.getFailed')}: ${e?.message || e}`)
  }
}

async function onRefreshLocalSources() {
  try {
    await dataSourceStore.refreshLocalSources()
    toast.success(t('dataSource.refreshSuccess'))
  }
  catch (e: any) {
    toast.error(`${t('common.getFailed')}: ${e?.message || e}`)
  }
}

async function onRefreshAll() {
  const results = await dataSourceStore.refreshAll()
  if (results.every(result => result.status === 'fulfilled')) {
    toast.success(t('dataSource.refreshAllSuccess'))
  }
  else {
    const failed = results.find((result): result is PromiseRejectedResult => result.status === 'rejected')
    toast.error(`${t('common.getFailed')}: ${(failed?.reason as Error)?.message || failed?.reason}`)
  }
}

const statusColorMap: Record<SteamLocalDataSource['status'], string> = {
  ok: 'green',
  missing: 'orange',
  parseFailed: 'red',
}

const sourceColumns = computed(() => [
  { title: t('dataSource.local.columnName'), dataIndex: 'name', key: 'name' },
  { title: t('dataSource.local.columnPath'), dataIndex: 'path', key: 'path' },
  { title: t('dataSource.local.columnStatus'), dataIndex: 'status', key: 'status' },
])
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
          <Button type="primary" class="flex gap-1 items-center" @click="onRefreshAll">
            <template #icon>
              <span class="i-mdi:refresh h-4 w-4" />
            </template>
            {{ t('dataSource.refreshAll') }}
          </Button>
        </template>
      </AppPageHeader>

      <div class="space-y-6">
        <!-- Steam 状态卡片 -->
        <Transition name="slide-fade" appear>
          <Spin :spinning="dataSourceStore.loading.global">
            <div class="p-6 rounded-lg bg-[var(--g-container-bg)] shadow-lg">
              <div class="mb-4 flex items-center justify-between">
                <h3 class="text-xl font-bold flex gap-2 items-center">
                  <span class="i-mdi:steam h-6 w-6 inline-block" />
                  {{ t('status.statusCard') }}
                </h3>
                <div class="flex gap-4 items-center">
                  <span v-if="formatLastRefreshed('global')" class="text-xs text-gray-500">
                    {{ t('common.lastRefresh') }}: {{ formatLastRefreshed('global') }}
                  </span>
                  <Button
                    type="primary"
                    :loading="dataSourceStore.loading.global"
                    class="flex gap-1 items-center"
                    @click="onRefreshGlobalStatus"
                  >
                    <template #icon>
                      <span class="i-mdi:refresh h-4 w-4" />
                    </template>
                    {{ t('common.refresh') }}
                  </Button>
                </div>
              </div>

              <div v-if="dataSourceStore.globalStatus" class="space-y-4">
                <!-- 状态标签 -->
                <div class="flex flex-wrap gap-2 items-center">
                  <Tag
                    :color="Number(dataSourceStore.globalStatus.steamPid) > 0 ? 'blue' : 'default'"
                    class="px-4 py-2 flex gap-2 items-center"
                  >
                    <span class="i-mdi:steam h-4 w-4" />
                    {{ Number(dataSourceStore.globalStatus.steamPid) > 0 ? t('status.steamRunning') : t('status.steamNotRunning') }}
                    <span v-if="Number(dataSourceStore.globalStatus.steamPid) > 0" class="text-xs opacity-80">PID: {{ dataSourceStore.globalStatus.steamPid }}</span>
                  </Tag>

                  <Tag
                    :color="dataSourceStore.globalStatus.activeUserSteamId ? 'orange' : 'default'"
                    class="px-4 py-2 flex gap-2 items-center"
                  >
                    <span class="i-mdi:account h-4 w-4" />
                    {{ dataSourceStore.globalStatus.activeUserSteamId ? t('status.userLoggedIn') : t('status.noUser') }}
                    <span v-if="dataSourceStore.globalStatus.activeUserSteamId" class="text-xs opacity-80">
                      Steam ID: {{ dataSourceStore.globalStatus.activeUserSteamId }}
                    </span>
                  </Tag>

                  <Tag
                    :color="Number(dataSourceStore.globalStatus.runningAppId) > 0 ? 'green' : 'default'"
                    class="px-4 py-2 flex gap-2 items-center"
                  >
                    <span class="i-mdi:gamepad h-4 w-4" />
                    {{ Number(dataSourceStore.globalStatus.runningAppId) > 0 ? t('status.appRunning') : t('status.noApp') }}
                    <span v-if="Number(dataSourceStore.globalStatus.runningAppId) > 0" class="text-xs opacity-80">
                      APP ID: {{ dataSourceStore.globalStatus.runningAppId }}
                    </span>
                  </Tag>

                  <Tag
                    v-if="dataSourceStore.globalStatus.refreshTime"
                    color="pink"
                    class="px-4 py-2 flex gap-2 items-center"
                  >
                    <span class="i-mdi:clock h-4 w-4" />
                    {{ t('common.dataUpdateTime') }}: {{ dayjs.unix(dataSourceStore.globalStatus.refreshTime).format('YYYY-MM-DD HH:mm:ss') }}
                  </Tag>
                </div>

                <!-- 路径信息 -->
                <div class="gap-4 grid grid-cols-1 lg:grid-cols-2">
                  <Transition name="slide-fade">
                    <div v-if="dataSourceStore.globalStatus.steamPath" class="group p-4 border rounded-lg transition-all from-blue-50 to-blue-100 bg-gradient-to-br hover:shadow-md dark:from-blue-900/20 dark:to-blue-800/20">
                      <div class="mb-2 flex gap-2 items-center">
                        <span class="i-mdi:folder text-blue-600 h-6 w-6 inline-block dark:text-blue-400" />
                        <span class="font-size-4 text-gray-800 font-semibold dark:text-gray-200">{{ t('status.steamPath') }}</span>
                      </div>
                      <div class="flex gap-2 items-center">
                        <code class="text-sm px-3 py-2 rounded-lg bg-white/50 flex-1 dark:bg-black/20">{{ dataSourceStore.globalStatus.steamPath }}</code>
                        <Button type="text" class="flex gap-2 h-9 items-center" @click="copyToClipboard(dataSourceStore.globalStatus.steamPath!)">
                          <span class="i-mdi:content-copy h-4 w-4" />
                        </Button>
                      </div>
                    </div>
                  </Transition>

                  <Transition name="slide-fade">
                    <div v-if="dataSourceStore.globalStatus.steamExePath" class="group p-4 border rounded-lg transition-all from-green-50 to-green-100 bg-gradient-to-br hover:shadow-md dark:from-green-900/20 dark:to-green-800/20">
                      <div class="mb-2 flex gap-2 items-center">
                        <span class="i-mdi:application text-green-600 h-6 w-6 inline-block dark:text-green-400" />
                        <span class="font-size-4 text-gray-800 font-semibold dark:text-gray-200">{{ t('status.steamExe') }}</span>
                      </div>
                      <div class="flex gap-2 items-center">
                        <code class="text-sm px-3 py-2 rounded-lg bg-white/50 flex-1 dark:bg-black/20">{{ dataSourceStore.globalStatus.steamExePath }}</code>
                        <Button type="text" class="flex gap-2 h-9 items-center" @click="copyToClipboard(dataSourceStore.globalStatus.steamExePath!)">
                          <span class="i-mdi:content-copy h-4 w-4" />
                        </Button>
                      </div>
                    </div>
                  </Transition>

                  <Transition name="slide-fade">
                    <div v-if="dataSourceStore.globalStatus.steamClientDllPath" class="group p-4 border rounded-lg transition-all from-purple-50 to-purple-100 bg-gradient-to-br hover:shadow-md dark:from-purple-900/20 dark:to-purple-800/20">
                      <div class="mb-2 flex gap-2 items-center">
                        <span class="i-mdi:file-code text-purple-600 h-6 w-6 inline-block dark:text-purple-400" />
                        <span class="font-size-4 text-gray-800 font-semibold dark:text-gray-200">{{ t('status.steamClientDll32') }}</span>
                      </div>
                      <div class="flex gap-2 items-center">
                        <code class="text-sm px-3 py-2 rounded-lg bg-white/50 flex-1 dark:bg-black/20">{{ dataSourceStore.globalStatus.steamClientDllPath }}</code>
                        <Button type="text" class="flex gap-2 h-9 items-center" @click="copyToClipboard(dataSourceStore.globalStatus.steamClientDllPath!)">
                          <span class="i-mdi:content-copy h-4 w-4" />
                        </Button>
                      </div>
                    </div>
                  </Transition>

                  <Transition name="slide-fade">
                    <div v-if="dataSourceStore.globalStatus.steamClientDll64Path" class="group p-4 border rounded-lg transition-all from-orange-50 to-orange-100 bg-gradient-to-br hover:shadow-md dark:from-orange-900/20 dark:to-orange-800/20">
                      <div class="mb-2 flex gap-2 items-center">
                        <span class="i-mdi:file-code text-orange-600 h-6 w-6 inline-block dark:text-orange-400" />
                        <span class="font-size-4 text-gray-800 font-semibold dark:text-gray-200">{{ t('status.steamClientDll64') }}</span>
                      </div>
                      <div class="flex gap-2 items-center">
                        <code class="text-sm px-3 py-2 rounded-lg bg-white/50 flex-1 dark:bg-black/20">{{ dataSourceStore.globalStatus.steamClientDll64Path }}</code>
                        <Button type="text" class="flex gap-2 h-9 items-center" @click="copyToClipboard(dataSourceStore.globalStatus.steamClientDll64Path!)">
                          <span class="i-mdi:content-copy h-4 w-4" />
                        </Button>
                      </div>
                    </div>
                  </Transition>
                </div>
              </div>

              <div v-else class="py-8">
                <Empty :description="t('status.notDetected')" />
              </div>
            </div>
          </Spin>
        </Transition>

        <!-- Steam 库目录卡片 -->
        <Transition name="slide-fade" appear>
          <Spin :spinning="dataSourceStore.loading.libraryFolders">
            <div class="p-6 rounded-lg bg-[var(--g-container-bg)] shadow-lg">
              <div class="mb-4 flex items-center justify-between">
                <h3 class="text-xl font-bold flex gap-2 items-center">
                  <span class="i-mdi:folder-multiple h-6 w-6 inline-block" />
                  {{ t('status.libraryFolders') }}
                </h3>
                <div class="flex gap-4 items-center">
                  <span v-if="formatLastRefreshed('libraryFolders')" class="text-xs text-gray-500">
                    {{ t('common.lastRefresh') }}: {{ formatLastRefreshed('libraryFolders') }}
                  </span>
                  <Button
                    type="primary"
                    :loading="dataSourceStore.loading.libraryFolders"
                    class="flex gap-1 items-center"
                    @click="onRefreshLibraryFolders"
                  >
                    <template #icon>
                      <span class="i-mdi:refresh h-4 w-4" />
                    </template>
                    {{ t('common.refresh') }}
                  </Button>
                </div>
              </div>

              <div v-if="dataSourceStore.libraryFolders.length > 0" class="space-y-4">
                <div class="flex flex-wrap gap-2 items-center">
                  <Tag
                    color="green"
                    class="px-4 py-2 flex gap-2 items-center"
                  >
                    <span class="i-mdi:folder-multiple h-4 w-4" />
                    {{ t('status.totalFolders', { count: dataSourceStore.libraryFolders.length }) }}
                  </Tag>
                </div>

                <TransitionGroup name="list" tag="div" class="space-y-4">
                  <div
                    v-for="(folder, idx) in dataSourceStore.libraryFolders"
                    :key="folder"
                    class="group p-4 border rounded-lg flex gap-3 transition-all items-center from-indigo-50 to-purple-50 bg-gradient-to-r hover:shadow-md dark:from-indigo-900/20 dark:to-purple-900/20"
                  >
                    <span class="i-mdi:folder text-indigo-600 h-6 w-6 inline-block dark:text-indigo-400" />
                    <span class="font-size-4 text-gray-700 font-semibold dark:text-gray-300">库 {{ idx + 1 }}</span>
                    <code class="text-sm px-3 py-2 rounded-lg bg-white/50 flex-1 dark:bg-black/20">{{ folder }}</code>
                    <Button type="text" class="flex gap-2 h-9 items-center" @click="copyToClipboard(folder)">
                      <span class="i-mdi:content-copy h-4 w-4" />
                    </Button>
                  </div>
                </TransitionGroup>
              </div>

              <div v-else class="py-8">
                <Empty :description="t('status.noLibraryFolders')" />
              </div>
            </div>
          </Spin>
        </Transition>

        <!-- 本地数据源状态卡片 -->
        <Transition name="slide-fade" appear>
          <Spin :spinning="dataSourceStore.loading.local">
            <div class="p-6 rounded-lg bg-[var(--g-container-bg)] shadow-lg">
              <div class="mb-4 flex items-center justify-between">
                <h3 class="text-xl font-bold flex gap-2 items-center">
                  <span class="i-mdi:database-cog-outline h-6 w-6 inline-block" />
                  {{ t('dataSource.local.sourcesCard') }}
                </h3>
                <div class="flex gap-4 items-center">
                  <span v-if="dataSourceStore.localSources" class="text-xs text-gray-500">
                    {{ t('common.lastRefresh') }}: {{ dayjs.unix(dataSourceStore.localSources.checkedAt).format('YYYY-MM-DD HH:mm:ss') }}
                  </span>
                  <Button
                    type="primary"
                    :loading="dataSourceStore.loading.local"
                    class="flex gap-1 items-center"
                    @click="onRefreshLocalSources"
                  >
                    <template #icon>
                      <span class="i-mdi:refresh h-4 w-4" />
                    </template>
                    {{ t('common.refresh') }}
                  </Button>
                </div>
              </div>

              <Table
                :columns="sourceColumns"
                :data-source="dataSourceStore.localSources?.sources ?? []"
                :pagination="false"
                row-key="id"
              >
                <template #bodyCell="{ column, record }">
                  <template v-if="column.key === 'name'">
                    {{ dataSourceStore.labelOf(record.id) }}
                  </template>
                  <template v-else-if="column.key === 'path'">
                    <div v-if="record.path" class="flex gap-1 min-w-0 items-center">
                      <code class="text-xs font-mono flex-1 truncate" :title="record.path">{{ record.path }}</code>
                      <Button type="text" class="flex gap-1 h-7 items-center" @click="copyToClipboard(record.path)">
                        <span class="i-mdi:content-copy h-3.5 w-3.5" />
                      </Button>
                    </div>
                    <span v-else>-</span>
                  </template>
                  <template v-else-if="column.key === 'status'">
                    <div class="flex gap-2 items-center">
                      <Tooltip :title="record.error ?? undefined">
                        <Tag :color="statusColorMap[record.status as SteamLocalDataSource['status']]">
                          {{ t(`dataSource.status.${record.status}`) }}
                        </Tag>
                      </Tooltip>
                      <div class="text-xs text-gray-500">
                        <span v-if="record.itemCount != null">{{ t('dataSource.local.itemCount', { count: record.itemCount }) }}</span>
                        <span v-if="record.failedCount" class="text-red-500 ms-1">{{ t('dataSource.local.failedCount', { count: record.failedCount }) }}</span>
                      </div>
                    </div>
                  </template>
                </template>
              </Table>
            </div>
          </Spin>
        </Transition>

        <!-- 应用检测设置卡片 -->
        <Transition name="slide-fade" appear>
          <div class="p-5 rounded-lg bg-[var(--g-container-bg)]">
            <div class="mb-4 flex gap-2 items-center">
              <span class="i-mdi:cog h-6 w-6 inline-block" />
              <h3 class="text-lg font-bold">
                {{ t('settings.appDetection') }}
              </h3>
            </div>

            <div class="divide-[var(--el-border-color-lighter)] divide-y">
              <div class="setting-row">
                <div class="setting-label">
                  <span class="i-mdi:radar text-primary h-5 w-5 inline-block" />
                  <div>
                    <div class="font-medium flex gap-2 items-center">
                      {{ t('settings.enableDetection') }}
                      <Tag v-if="appSettings.updateAppRunningStatusJob.enabled" color="success">
                        {{ t('settings.detectionRunning') }}
                      </Tag>
                      <Tag v-else color="default">
                        {{ t('settings.detectionStopped') }}
                      </Tag>
                    </div>
                    <div class="text-xs text-gray-500">
                      {{ t('settings.detectionDesc') }}
                    </div>
                  </div>
                </div>
                <Switch
                  v-model:checked="appSettings.updateAppRunningStatusJob.enabled" :loading="settingsLoading"
                  @change="updateDetectionSettings({ updateAppRunningStatusJob: { enabled: appSettings.updateAppRunningStatusJob.enabled } })"
                />
              </div>

              <div class="setting-row" :class="{ 'opacity-50': !appSettings.updateAppRunningStatusJob.enabled }">
                <div class="setting-label">
                  <span class="i-mdi:clock-outline text-primary h-5 w-5 inline-block" />
                  <div>
                    <div class="font-medium">
                      {{ t('settings.detectionInterval') }}
                    </div>
                    <div class="text-xs text-gray-500">
                      {{ t('settings.detectionIntervalDesc') }}
                    </div>
                  </div>
                </div>
                <div class="flex gap-3 items-center">
                  <InputNumber
                    v-model:value="appSettings.updateAppRunningStatusJob.intervalSeconds" :min="1" :max="60" :step="1" size="large"
                    :disabled="!appSettings.updateAppRunningStatusJob.enabled || settingsLoading"
                    class="w-32"
                    @blur="updateDetectionSettings({ updateAppRunningStatusJob: { intervalSeconds: appSettings.updateAppRunningStatusJob.intervalSeconds } })"
                    @step="updateDetectionSettings({ updateAppRunningStatusJob: { intervalSeconds: appSettings.updateAppRunningStatusJob.intervalSeconds } })"
                  />
                </div>
              </div>
            </div>
          </div>
        </Transition>
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
  padding: 14px 0;
}

.setting-label {
  display: flex;
  flex: 1;
  gap: 12px;
  align-items: center;
  min-width: 0;
}

.slide-fade-enter-active,
.slide-fade-leave-active {
  transition: all 0.3s ease;
}

.slide-fade-enter-from {
  opacity: 0;
  transform: translateY(-10px);
}

.slide-fade-leave-to {
  opacity: 0;
  transform: translateY(10px);
}

.list-enter-active,
.list-leave-active {
  transition: all 0.3s ease;
}

.list-enter-from {
  opacity: 0;
  transform: translateX(-20px);
}

.list-leave-to {
  opacity: 0;
  transform: translateX(20px);
}

.list-move {
  transition: transform 0.3s ease;
}
</style>
