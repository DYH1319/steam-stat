<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { toast } from 'vue-sonner'

const { t } = useI18n()
const electronApi = (window as any).electron

const runningApps = ref<any[]>([])
const appsInfo = ref<any[]>([])
const loadingRunning = ref(false)
const loadingApps = ref(false)
const lastRefreshTime = ref<{ running: Date | null, appInfo: Date | null }>({
  running: null,
  appInfo: null,
})
const filterInstalled = ref<'all' | 'true' | 'false'>('all')
const sortState = ref<{ key: string, order: 'asc' | 'desc' }>({ key: 'appOnDisk', order: 'desc' })

// 格式化字节大小
function formatBytes(bytes: bigint | number | null | undefined): string {
  if (!bytes) {
    return '0 B'
  }
  const value = typeof bytes === 'bigint' ? Number(bytes) : bytes
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  const k = 1024
  const i = Math.floor(Math.log(value) / Math.log(k))
  return `${(value / k ** i).toFixed(2)} ${units[i]}`
}

// 获取运行中应用
async function fetchRunningApps(showToast = false) {
  loadingRunning.value = true
  try {
    const res = await electronApi.steamGetRunningApps()
    runningApps.value = res.apps
    lastRefreshTime.value.running = new Date(res.lastUpdateTime)
    if (showToast) {
      toast.success(t('app.getRunningSuccess'), {
        duration: 1000,
      })
    }
  }
  catch (e: any) {
    toast.error(`${t('common.getFailed')}: ${e?.message || e}`)
  }
  finally {
    loadingRunning.value = false
  }
}

// 获取本地应用信息
async function fetchAppsInfo(showToast = false) {
  loadingApps.value = true
  try {
    appsInfo.value = await electronApi.steamGetAppsInfo()
    if (showToast) {
      toast.success(t('app.getSuccess'), {
        duration: 1000,
      })
    }
  }
  catch (e: any) {
    toast.error(`${t('common.getFailed')}: ${e?.message || e}`)
  }
  finally {
    loadingApps.value = false
  }
}

// 刷新本地应用信息
async function refreshAppsInfo(showToast = true) {
  loadingApps.value = true
  try {
    appsInfo.value = await electronApi.steamRefreshAppsInfo()
    lastRefreshTime.value.appInfo = new Date()
    if (showToast) {
      toast.success(t('app.refreshSuccess'), {
        duration: 1000,
      })
    }
  }
  catch (e: any) {
    toast.error(`${t('common.refreshFailed')}: ${e?.message || e}`)
  }
  finally {
    loadingApps.value = false
  }
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

// 过滤和排序后的应用数据
const filteredAndSortedApps = computed(() => {
  let result = [...appsInfo.value]

  // 过滤
  if (filterInstalled.value !== 'all') {
    const isInstalled = filterInstalled.value === 'true'
    result = result.filter(app => app.installed === isInstalled)
  }

  // 排序
  const { key, order } = sortState.value
  result.sort((a, b) => {
    let aVal = a[key]
    let bVal = b[key]

    // 特殊处理数字类型
    if (key === 'appOnDisk' || key === 'appId') {
      aVal = Number(aVal || 0)
      bVal = Number(bVal || 0)
    }

    // 特殊处理日期
    if (key === 'refreshTime') {
      aVal = aVal ? new Date(aVal).getTime() : 0
      bVal = bVal ? new Date(bVal).getTime() : 0
    }

    if (aVal < bVal) {
      return order === 'asc' ? -1 : 1
    }
    if (aVal > bVal) {
      return order === 'asc' ? 1 : -1
    }
    return 0
  })

  return result
})

// 排序处理函数
function handleSort(key: string) {
  if (sortState.value.key === key) {
    sortState.value.order = sortState.value.order === 'asc' ? 'desc' : 'asc'
  }
  else {
    sortState.value.key = key
    sortState.value.order = 'desc'
  }
}

// 过滤器处理函数
function handleFilterChange(command: 'all' | 'true' | 'false') {
  filterInstalled.value = command
}

// 打开安装文件夹
async function openInstallFolder(installDirPath: string) {
  try {
    const result = await electronApi.shellOpenPath(installDirPath)
    if (!result.success) {
      toast.error(`${t('common.openFolderFailed')}: ${result.error || t('common.error')}`)
    }
  }
  catch (e: any) {
    toast.error(`${t('common.openFolderFailed')}: ${e?.message || e}`)
  }
}

// el-table-v2 列配置
const tableColumns = computed(() => [
  {
    key: 'appId',
    title: t('app.appId'),
    dataKey: 'appId',
    width: 90,
    sortable: true,
  },
  {
    key: 'name',
    title: t('app.name'),
    dataKey: 'name',
    width: 375,
    sortable: true,
  },
  {
    key: 'installDir',
    title: t('app.installDir'),
    dataKey: 'installDir',
    width: 375,
    sortable: true,
  },
  {
    key: 'appOnDisk',
    title: t('app.sizeOnDisk'),
    dataKey: 'appOnDisk',
    width: 150,
    sortable: true,
    align: 'right' as const,
  },
  {
    key: 'installed',
    title: t('app.status'),
    dataKey: 'installed',
    width: 100,
    align: 'center' as const,
  },
  {
    key: 'refreshTime',
    title: t('common.dataUpdateTime'),
    dataKey: 'refreshTime',
    width: 135,
    sortable: true,
  },
  {
    key: 'actions',
    title: t('common.actions'),
    dataKey: 'actions',
    width: 90,
    align: 'center' as const,
  },
] as any)

// 页面加载时自动获取数据
onMounted(async () => {
  // 使用 Promise.all 并发请求，提升加载速度
  await Promise.all([
    fetchRunningApps(),
    fetchAppsInfo(),
  ])
})
</script>

<template>
  <div>
    <FaPageHeader :title="t('app.title')" />
    <FaPageMain>
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
                {{ t('app.running') }}
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
          <div class="rounded-lg bg-[var(--g-container-bg)] p-6 shadow-lg">
            <div class="mb-4 flex items-center justify-between">
              <div class="flex items-center gap-3">
                <h3 class="flex items-center gap-2 text-xl font-bold">
                  <span class="i-mdi:gamepad-variant text-success inline-block h-6 w-6" />
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
                  : {{ lastRefreshTime.running.toLocaleTimeString() }}
                </span>
                <el-button
                  type="success"
                  :loading="loadingRunning"
                  @click="fetchRunningApps(true)"
                >
                  <span class="i-mdi:refresh mr-1 inline-block h-4 w-4" />
                  {{ t('common.refresh') }}
                </el-button>
              </div>
            </div>

            <div v-loading="loadingRunning">
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
                <el-empty :description="t('app.noRunningApps')">
                  <template #image>
                    <span class="i-mdi:gamepad-variant-outline inline-block h-20 w-20 text-gray-300" />
                  </template>
                </el-empty>
              </div>
            </div>
          </div>
        </Transition>

        <!-- 本地 Steam 应用 -->
        <Transition name="slide-fade" appear>
          <div class="rounded-lg bg-[var(--g-container-bg)] p-6 shadow-lg">
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
                  {{ t('common.lastRefresh') }}: {{ lastRefreshTime.appInfo.toLocaleTimeString() }}
                </span>
                <el-button
                  type="primary"
                  :loading="loadingApps"
                  @click="refreshAppsInfo(true)"
                >
                  <span class="i-mdi:refresh mr-1 inline-block h-4 w-4" />
                  {{ t('common.refresh') }}
                </el-button>
              </div>
            </div>

            <div v-loading="loadingApps">
              <div v-if="appsInfo.length > 0">
                <el-auto-resizer style="height: 600px;">
                  <template #default="{ height, width }">
                    <!-- 虚拟滚动表格 -->
                    <el-table-v2
                      :columns="tableColumns"
                      :data="filteredAndSortedApps"
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
                                :class="filterInstalled === 'all' ? 'i-mdi:filter-outline' : 'i-mdi:filter text-primary'"
                              />
                            </span>
                            <template #dropdown>
                              <el-dropdown-menu>
                                <el-dropdown-item command="all" :class="{ 'is-active': filterInstalled === 'all' }">
                                  <span class="i-mdi:format-list-bulleted mr-2 inline-block h-4 w-4" />
                                  {{ t('app.all') }} ({{ appsInfo.length }})
                                </el-dropdown-item>
                                <el-dropdown-item command="true" :class="{ 'is-active': filterInstalled === 'true' }">
                                  <span class="i-mdi:check-circle text-success mr-2 inline-block h-4 w-4" />
                                  {{ t('app.installed') }} ({{ stats.installed }})
                                </el-dropdown-item>
                                <el-dropdown-item command="false" :class="{ 'is-active': filterInstalled === 'false' }">
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
                          <span v-if="column.sortable && sortState.key === column.dataKey" class="ml-1">
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
                            {{ t('app.running') }}
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
                            实际: {{ formatBytes(rowData.appOnDiskReal) }}
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
                          {{ rowData.refreshTime ? new Date(rowData.refreshTime).toLocaleString() : '-' }}
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

              <div v-else-if="!loadingApps" class="py-8">
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
