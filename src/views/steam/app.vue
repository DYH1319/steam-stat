<script setup lang="ts">
import type { Dayjs } from 'dayjs'
import type { AnyColumn } from 'element-plus/es/components/table-v2/src/common'
import { useI18n } from 'vue-i18n'
import { toast } from 'vue-sonner'
import { formatBytes } from '@/utils'
import dayjs from '@/utils/dayjs.ts'

const { t } = useI18n()
const electronApi = (window as Window).electron

// electron api
const runningApps = ref<SteamApp[]>([])
const appsInfo = ref<SteamApp[]>([])

const loading = ref<{ running: boolean, apps: boolean }>({ running: false, apps: false })
const lastRefreshTime = ref<{ running?: Dayjs, appInfo?: Dayjs }>({})

const tableContainerRef = ref<HTMLElement | null>(null)
const tableHeight = ref(600)

function updateTableHeight() {
  if (!tableContainerRef.value) {
    return
  }
  const rect = tableContainerRef.value.getBoundingClientRect()
  // .app-content 的底部距离窗口顶部的位置
  const appContent = document.querySelector('.app-content')
  const bottomPadding = 20 + 32 + 20 + 24 + 6 // Footer + Footer margin + FaPageMain padding + Card padding + other
  const availableHeight = appContent
    ? appContent.clientHeight + appContent.getBoundingClientRect().top - rect.top - bottomPadding
    : 600
  tableHeight.value = Math.max(600, availableHeight)
}

const sortState = ref<{ field: string, order: 'asc' | 'desc' }>({ field: 'appOnDisk', order: 'desc' })
const filter = ref<{ installed?: boolean }>({
  installed: undefined,
})

// 列宽比例配置（总和为 1）
const columnWidthRatios = {
  appId: 0.08,
  name: 0.32,
  installDir: 0.30,
  appOnDisk: 0.12,
  installed: 0.10,
  actions: 0.08,
}

// el-table-v2 列配置（根据容器宽度按比例计算列宽）
function getTableColumns(containerWidth: number) {
  return [
    {
      key: 'appId',
      title: t('app.appId'),
      dataKey: 'appId',
      width: Math.floor(containerWidth * columnWidthRatios.appId),
      align: 'left',
      sortable: true,
    },
    {
      key: 'name',
      title: t('app.name'),
      dataKey: 'name',
      width: Math.floor(containerWidth * columnWidthRatios.name),
      align: 'center',
      sortable: true,
    },
    {
      key: 'installDir',
      title: t('app.installDir'),
      dataKey: 'installDir',
      width: Math.floor(containerWidth * columnWidthRatios.installDir),
      align: 'center',
      sortable: true,
    },
    {
      key: 'appOnDisk',
      title: t('app.sizeOnDisk'),
      dataKey: 'appOnDisk',
      width: Math.floor(containerWidth * columnWidthRatios.appOnDisk),
      align: 'right',
      sortable: true,
    },
    {
      key: 'installed',
      title: t('app.status'),
      dataKey: 'installed',
      width: Math.floor(containerWidth * columnWidthRatios.installed),
      align: 'center',
      sortable: false,
    },
    {
      key: 'actions',
      title: t('common.actions'),
      dataKey: 'actions',
      width: Math.floor(containerWidth * columnWidthRatios.actions),
      align: 'center',
      sortable: false,
    },
  ] as AnyColumn[]
}

// 统计信息（优化计算性能）
const stats = computed(() => {
  let installed = 0
  let totalSize = 0

  for (const app of appsInfo.value) {
    if (app.installed) {
      installed++
    }
    totalSize += Number(app.appOnDiskReal || app.appOnDisk || 0)
  }

  return {
    running: runningApps.value.length,
    installed,
    total: appsInfo.value.length,
    totalSize,
  }
})

onMounted(() => {
  fetchRunningApps(false)
  fetchAppsInfo(false)
  window.addEventListener('resize', updateTableHeight)
})

onBeforeUnmount(() => {
  window.removeEventListener('resize', updateTableHeight)
})

// 获取运行中应用
async function fetchRunningApps(isRefresh: boolean) {
  loading.value.running = true
  try {
    if (isRefresh) {
      await new Promise(resolve => setTimeout(resolve, 500))
    }
    const res = await electronApi.steamGetRunningApps()
    runningApps.value = res.apps
    lastRefreshTime.value.running = dayjs.unix(res.lastUpdateTime)
    if (isRefresh) {
      toast.success(t('app.getRunningSuccess'))
    }
  }
  catch (e: any) {
    toast.error(`${t('common.getFailed')}: ${e?.message || e}`)
  }
  finally {
    loading.value.running = false
  }
}

// 获取本地应用信息
async function fetchAppsInfo(isRefresh: boolean) {
  loading.value.apps = true
  try {
    const param = {
      sortField: sortState.value.field,
      sortOrder: sortState.value.order,
      filterInstalled: filter.value.installed,
    }
    if (isRefresh) {
      await new Promise(resolve => setTimeout(resolve, 500))
      appsInfo.value = await electronApi.steamRefreshAppsInfo(param)
    }
    else {
      await new Promise(resolve => setTimeout(resolve, 200))
      appsInfo.value = await electronApi.steamGetAppsInfo(param)
    }
    const status = await electronApi.steamGetStatus()
    lastRefreshTime.value.appInfo = status?.steamAppRefreshTime ? dayjs.unix(status.steamAppRefreshTime) : undefined
    if (isRefresh) {
      toast.success(t('app.getSuccess'))
    }
    // 数据加载后重新计算表格高度
    nextTick(() => updateTableHeight())
  }
  catch (e: any) {
    toast.error(`${t('common.getFailed')}: ${e?.message || e}`)
  }
  finally {
    loading.value.apps = false
  }
}

// 打开安装文件夹
function openInstallFolder(installDirPath: string) {
  try {
    electronApi.shellOpenPath(installDirPath)
  }
  catch (e: any) {
    toast.error(`${t('common.openFolderFailed')}: ${e?.message || e}`)
  }
}

// 排序处理函数
function handleSort(key: string) {
  if (sortState.value.field === key) {
    sortState.value.order = sortState.value.order === 'asc' ? 'desc' : 'asc'
  }
  else {
    sortState.value.field = key
    sortState.value.order = 'asc'
  }
  fetchAppsInfo(false)
}

// 过滤器处理函数
function handleFilterChange(command: string | object) {
  if (typeof command === 'object') {
    filter.value.installed = undefined
  }
  else {
    filter.value.installed = command === 'true'
  }
  fetchAppsInfo(false)
}
</script>

<template>
  <div>
    <FaPageMain class="mb-0">
      <div class="space-y-6">
        <!-- 统计卡片 -->
        <Transition name="slide-fade" appear>
          <div class="grid grid-cols-2 gap-4 lg:grid-cols-4">
            <div class="rounded-lg from-green-500 to-emerald-600 bg-gradient-to-br p-6 text-white shadow-lg">
              <div class="mb-2 flex items-center justify-between">
                <span class="i-mdi:gamepad-variant inline-block h-8 w-8" />
                <span class="text-3xl font-bold">{{ stats.running }}</span>
              </div>
              <div class="text-sm opacity-90">
                {{ t('common.running') }}
              </div>
            </div>

            <div class="rounded-lg from-blue-500 to-cyan-600 bg-gradient-to-br p-6 text-white shadow-lg">
              <div class="mb-2 flex items-center justify-between">
                <span class="i-mdi:checkbox-marked-circle inline-block h-8 w-8" />
                <span class="text-3xl font-bold">{{ stats.installed }}</span>
              </div>
              <div class="text-sm opacity-90">
                {{ t('app.installed') }}
              </div>
            </div>

            <div class="rounded-lg from-purple-500 to-pink-600 bg-gradient-to-br p-6 text-white shadow-lg">
              <div class="mb-2 flex items-center justify-between">
                <span class="i-mdi:apps inline-block h-8 w-8" />
                <span class="text-3xl font-bold">{{ stats.total }}</span>
              </div>
              <div class="text-sm opacity-90">
                {{ t('app.totalApps') }}
              </div>
            </div>

            <div class="rounded-lg from-orange-500 to-red-600 bg-gradient-to-br p-6 text-white shadow-lg">
              <div class="mb-2 flex items-center justify-between">
                <span class="i-mdi:harddisk inline-block h-8 w-8" />
                <span class="text-2xl font-bold">{{ formatBytes(stats.totalSize) }}</span>
              </div>
              <div class="text-sm opacity-90">
                {{ t('app.totalSize') }}
              </div>
            </div>
          </div>
        </Transition>

        <!-- 运行中的应用 -->
        <Transition name="slide-fade" appear>
          <div class="card-shadow rounded-lg bg-[var(--g-container-bg)] p-6">
            <div class="mb-4 flex items-center justify-between">
              <div class="flex items-center gap-3">
                <h3 class="flex items-center gap-2 text-xl font-bold">
                  <span class="i-mdi:gamepad-variant inline-block h-6 w-6" />
                  {{ t('app.runningApps') }}
                </h3>
                <el-tag v-if="runningApps.length > 0" size="large" class="ml-2" type="success" effect="dark">
                  <span class="i-mdi:gamepad-variant mr-1 inline-block h-4 w-4" />
                  {{ t('app.runningAppsLength', { count: runningApps.length }) }}
                </el-tag>
              </div>
              <div class="flex items-center gap-4">
                <span v-if="lastRefreshTime.running" class="text-xs text-gray-500">
                  {{ t('common.lastRefresh') }}
                  <FaTooltip :text="t('useRecord.lastUpdateTip')">
                    <FaIcon name="i-ri:question-line" />
                  </FaTooltip>
                  : {{ lastRefreshTime.running.format('YYYY-MM-DD HH:mm:ss') }}
                </span>
                <el-button
                  type="primary"
                  :loading="loading.running"
                  @click="fetchRunningApps(true)"
                >
                  <span class="i-mdi:refresh mr-1 inline-block h-4 w-4" />
                  {{ t('common.refresh') }}
                </el-button>
              </div>
            </div>

            <div v-loading="loading.running">
              <TransitionGroup v-if="runningApps.length > 0" name="list" tag="div" class="grid grid-cols-1 gap-3 lg:grid-cols-3 md:grid-cols-2">
                <div
                  v-for="app in runningApps"
                  :key="app.appId"
                  class="group border rounded-lg from-green-50 to-emerald-50 bg-gradient-to-br p-4 shadow-sm transition-all dark:from-green-900/20 dark:to-emerald-900/20 hover:shadow-md"
                >
                  <div class="flex items-center gap-3">
                    <div class="bg-success/10 h-12 w-12 flex items-center justify-center rounded-lg">
                      <span class="i-mdi:gamepad-variant text-success inline-block h-6 w-6" />
                    </div>
                    <div class="flex-1">
                      <div class="font-semibold">
                        {{ app.name }}
                      </div>
                      <div class="text-xs text-gray-500">
                        App ID: {{ app.appId }}
                      </div>
                    </div>
                    <div class="bg-success h-2 w-2 animate-pulse rounded-full" />
                  </div>
                </div>
              </TransitionGroup>

              <div v-else>
                <el-empty class="p-0 text-sm" :description="t('app.noRunningApps')">
                  <template #image>
                    <span class="i-mdi:gamepad-variant-outline inline-block h-12 w-12 text-gray-300" />
                  </template>
                </el-empty>
              </div>
            </div>
          </div>
        </Transition>

        <!-- 本地 Steam 应用 -->
        <Transition name="slide-fade" appear>
          <div class="card-shadow rounded-lg bg-[var(--g-container-bg)] p-6">
            <div class="mb-4 flex items-center justify-between">
              <div class="flex items-center gap-4">
                <h3 class="flex items-center gap-2 text-xl font-bold">
                  <span class="i-mdi:folder-download inline-block h-6 w-6 text-primary" />
                  {{ t('app.localApp') }}
                </h3>
                <el-tag v-if="appsInfo.length > 0" size="large" class="ml-2" type="primary" effect="dark">
                  <span class="i-mdi:folder-download mr-1 inline-block h-4 w-4" />
                  {{ t('app.appsInfoLength', { count: appsInfo.length }) }}
                </el-tag>
              </div>
              <div class="flex items-center gap-4">
                <span v-if="lastRefreshTime.appInfo" class="text-xs text-gray-500">
                  {{ t('common.lastRefresh') }}: {{ lastRefreshTime.appInfo.format('YYYY-MM-DD HH:mm:ss') }}
                </span>
                <el-button
                  type="primary"
                  :loading="loading.apps"
                  @click="fetchAppsInfo(true)"
                >
                  <span class="i-mdi:refresh mr-1 inline-block h-4 w-4" />
                  {{ t('common.refresh') }}
                </el-button>
              </div>
            </div>

            <div v-loading="loading.apps">
              <div v-if="appsInfo.length > 0">
                <div ref="tableContainerRef" :style="{ height: `${tableHeight}px` }">
                  <el-auto-resizer>
                    <template #default="{ height, width }">
                      <!-- 虚拟滚动表格 -->
                      <el-table-v2
                        :columns="getTableColumns(width)"
                        :data="appsInfo"
                        :width="width"
                        :height="height"
                        :row-height="50"
                        fixed
                        class="w-full"
                      >
                        <template #header-cell="{ column }">
                          <!-- 状态列：带过滤器 -->
                          <div v-if="column.dataKey === 'installed'" class="flex items-center justify-center gap-2">
                            <span>{{ column.title }}</span>
                            <el-dropdown trigger="click" @command="handleFilterChange">
                              <span class="cursor-pointer">
                                <span
                                  class="inline-block h-4 w-4"
                                  :class="filter.installed === undefined ? 'i-mdi:filter-outline' : 'i-mdi:filter text-primary'"
                                />
                              </span>
                              <template #dropdown>
                                <el-dropdown-menu>
                                  <el-dropdown-item :command="undefined" :class="{ 'is-active': filter.installed === undefined }">
                                    <span class="i-mdi:format-list-bulleted mr-2 inline-block h-4 w-4" />
                                    {{ t('app.all') }} ({{ appsInfo.length }})
                                  </el-dropdown-item>
                                  <el-dropdown-item command="true" :class="{ 'is-active': filter.installed === true }">
                                    <span class="i-mdi:check-circle text-success mr-2 inline-block h-4 w-4" />
                                    {{ t('app.installed') }} ({{ stats.installed }})
                                  </el-dropdown-item>
                                  <el-dropdown-item command="false" :class="{ 'is-active': filter.installed === false }">
                                    <span class="i-mdi:close-circle text-danger mr-2 inline-block h-4 w-4" />
                                    {{ t('app.notInstalled') }} ({{ appsInfo.length - stats.installed }})
                                  </el-dropdown-item>
                                </el-dropdown-menu>
                              </template>
                            </el-dropdown>
                          </div>

                          <!-- 可排序列 -->
                          <div
                            v-else
                            class="flex cursor-pointer select-none items-center justify-between"
                            :class="column.sortable ? 'hover:text-primary' : ''"
                            @click="column.sortable && handleSort(column.dataKey)"
                          >
                            <span>{{ column.title }}</span>
                            <span v-if="column.sortable && sortState.field === column.dataKey" class="ml-1">
                              <span v-if="sortState.order === 'asc'" class="i-mdi:arrow-up inline-block h-4 w-4" />
                              <span v-else class="i-mdi:arrow-down inline-block h-4 w-4" />
                            </span>
                          </div>
                        </template>

                        <template #cell="{ rowData, column }">
                          <!-- App ID -->
                          <div v-if="column.dataKey === 'appId'" class="text-sm font-mono">
                            {{ rowData.appId }}
                          </div>

                          <!-- 应用名称 -->
                          <div v-else-if="column.dataKey === 'name'" class="flex items-center gap-2">
                            <el-tag v-if="rowData.isRunning" type="success" size="small" effect="dark">
                              <span class="i-mdi:play inline-block h-3 w-3" />
                              {{ t('common.running') }}
                            </el-tag>
                            <span class="truncate">{{ rowData.name || '-' }}</span>
                          </div>

                          <!-- 安装目录名 -->
                          <div v-else-if="column.dataKey === 'installDir'" class="truncate text-sm">
                            {{ rowData.installDir || '-' }}
                          </div>

                          <!-- 占用空间 -->
                          <div v-else-if="column.dataKey === 'appOnDisk'" class="flex flex-col items-end">
                            <span v-if="rowData.appOnDisk" class="text-xs font-mono">
                              {{ formatBytes(rowData.appOnDisk) }}
                            </span>
                            <span v-if="rowData.appOnDiskReal" class="text-xs text-gray-500 font-mono">
                              {{ t('app.actualSize') }}: {{ formatBytes(rowData.appOnDiskReal) }}
                            </span>
                          </div>

                          <!-- 状态 -->
                          <div v-else-if="column.dataKey === 'installed'" class="flex justify-center">
                            <el-tag :type="rowData.installed ? 'success' : 'danger'" size="small">
                              {{ rowData.installed ? t('app.installed') : t('app.notInstalled') }}
                            </el-tag>
                          </div>

                          <!-- 更新时间 -->
                          <div v-else-if="column.dataKey === 'refreshTime'" class="text-xs">
                            {{ rowData.refreshTime ? new Date(rowData.refreshTime * 1000).toLocaleString() : '-' }}
                          </div>

                          <!-- 操作 -->
                          <div v-else-if="column.dataKey === 'actions'" class="flex justify-center">
                            <el-button
                              v-if="rowData.installDirPath"
                              type="primary"
                              size="small"
                              text
                              @click="openInstallFolder(rowData.installDirPath)"
                            >
                              <span class="i-mdi:folder-open inline-block h-4 w-4" />
                            </el-button>
                            <span v-else class="text-xs text-gray-400">-</span>
                          </div>
                        </template>
                      </el-table-v2>
                    </template>
                  </el-auto-resizer>
                </div>
              </div>

              <div v-else-if="!loading.apps" class="py-8">
                <el-empty :description="t('app.noApps')">
                  <template #image>
                    <span class="i-mdi:folder-off inline-block h-20 w-20 text-gray-300" />
                  </template>
                </el-empty>
              </div>
            </div>
          </div>
        </Transition>
      </div>
    </FaPageMain>
  </div>
</template>

<style scoped>
.card-shadow {
  box-shadow: 0 10px 15px -3px rgb(0 0 0 / 10%), 0 4px 6px -4px rgb(0 0 0 / 10%);
}

:root.dark .card-shadow {
  box-shadow: 0 10px 15px -3px rgb(255 255 255 / 5%), 0 4px 6px -4px rgb(255 255 255 / 5%), 0 0 0 1px rgb(255 255 255 / 8%);
}

.slide-fade-enter-active {
  transition: all 0.4s ease;
}

.slide-fade-enter-from {
  opacity: 0;
  transform: translateY(-20px);
}

.list-enter-active {
  transition: all 0.3s ease;
}

.list-enter-from {
  opacity: 0;
  transform: scale(0.95);
}

.list-move {
  transition: transform 0.3s ease;
}

/* 激活状态的下拉菜单项 */
:deep(.el-dropdown-menu__item.is-active) {
  font-weight: 600;
  color: var(--el-color-primary);
  background-color: var(--el-color-primary-light-9);
}

:deep(.el-dropdown-menu__item.is-active:hover) {
  background-color: var(--el-color-primary-light-8);
}
</style>
