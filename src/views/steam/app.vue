<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { toast } from 'vue-sonner'

const electronApi = (window as any).electron

const runningApps = ref<any[]>([])
const appsInfo = ref<any[]>([])
const loadingRunning = ref(false)
const loadingApps = ref(false)
const lastRefreshTime = ref<{ running: Date | null, appInfo: Date | null }>({
  running: null,
  appInfo: null,
})

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
async function fetchRunningApps() {
  loadingRunning.value = true
  try {
    const res = await electronApi.steamGetRunningApps()
    runningApps.value = res.apps
    lastRefreshTime.value.running = new Date(res.lastUpdateTime)
    toast.success('获取运行中应用成功', {
      duration: 700,
    })
  }
  catch (e: any) {
    toast.error(`获取失败: ${e?.message || e}`)
  }
  finally {
    loadingRunning.value = false
  }
}

// 获取本地应用信息
async function fetchAppsInfo() {
  loadingApps.value = true
  try {
    appsInfo.value = await electronApi.steamGetAppsInfo()
    lastRefreshTime.value.appInfo = new Date()
    toast.success('获取本地应用信息成功', {
      duration: 700,
    })
  }
  catch (e: any) {
    toast.error(`获取失败: ${e?.message || e}`)
  }
  finally {
    loadingApps.value = false
  }
}

// 刷新本地应用信息
async function refreshAppsInfo() {
  loadingApps.value = true
  try {
    appsInfo.value = await electronApi.steamRefreshAppsInfo()
    lastRefreshTime.value.appInfo = new Date()
    toast.success('刷新本地应用信息成功', {
      duration: 700,
    })
  }
  catch (e: any) {
    toast.error(`刷新失败: ${e?.message || e}`)
  }
  finally {
    loadingApps.value = false
  }
}

// 统计信息
const stats = computed(() => ({
  running: runningApps.value.length,
  installed: appsInfo.value.filter(app => app.installed).length,
  total: appsInfo.value.length,
  totalSize: appsInfo.value.reduce((sum, app) => sum + Number(app.appOnDiskReal || app.appOnDisk || 0), 0),
}))

// 页面加载时自动获取数据
onMounted(() => {
  fetchRunningApps()
  fetchAppsInfo()
})
</script>

<template>
  <div>
    <FaPageHeader title="Steam 应用信息" />
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
                运行中
              </div>
            </div>

            <div class="rounded-lg from-blue-500 to-cyan-600 bg-gradient-to-br p-6 text-white shadow-lg">
              <div class="mb-2 flex items-center justify-between">
                <span class="i-mdi:checkbox-marked-circle inline-block h-8 w-8" />
                <span class="text-3xl font-bold">{{ stats.installed }}</span>
              </div>
              <div class="text-sm opacity-90">
                已安装
              </div>
            </div>

            <div class="rounded-lg from-purple-500 to-pink-600 bg-gradient-to-br p-6 text-white shadow-lg">
              <div class="mb-2 flex items-center justify-between">
                <span class="i-mdi:apps inline-block h-8 w-8" />
                <span class="text-3xl font-bold">{{ stats.total }}</span>
              </div>
              <div class="text-sm opacity-90">
                总应用数
              </div>
            </div>

            <div class="rounded-lg from-orange-500 to-red-600 bg-gradient-to-br p-6 text-white shadow-lg">
              <div class="mb-2 flex items-center justify-between">
                <span class="i-mdi:harddisk inline-block h-8 w-8" />
                <span class="text-2xl font-bold">{{ formatBytes(stats.totalSize) }}</span>
              </div>
              <div class="text-sm opacity-90">
                总占用空间
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
                  运行中的应用
                </h3>
                <el-tag v-if="runningApps.length > 0" size="large" class="ml-2" type="success" effect="dark">
                  <span class="i-mdi:gamepad-variant mr-1 inline-block h-4 w-4" />
                  {{ runningApps.length }} 个应用运行中
                </el-tag>
              </div>
              <div class="flex items-center gap-4">
                <span v-if="lastRefreshTime.running" class="text-xs text-gray-500">
                  上次刷新时间
                  <FaTooltip text="来自自动化获取运行应用脚本的上次获取信息的时间">
                    <FaIcon name="i-ri:question-line" />
                  </FaTooltip>
                  : {{ lastRefreshTime.running.toLocaleTimeString() }}
                </span>
                <el-button
                  type="success"
                  :loading="loadingRunning"
                  @click="fetchRunningApps"
                >
                  <span class="i-mdi:refresh mr-1 inline-block h-4 w-4" />
                  刷新
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
                <el-empty description="当前无运行应用">
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
                  本地 Steam 应用
                </h3>
                <el-tag v-if="appsInfo.length > 0" size="large" class="ml-2" type="primary" effect="dark">
                  <span class="i-mdi:folder-download mr-1 inline-block h-4 w-4" />
                  共 {{ appsInfo.length }} 个应用
                </el-tag>
              </div>
              <div class="flex items-center gap-4">
                <span v-if="lastRefreshTime.appInfo" class="text-xs text-gray-500">
                  上次刷新时间: {{ lastRefreshTime.appInfo.toLocaleTimeString() }}
                </span>
                <el-button
                  type="primary"
                  :loading="loadingApps"
                  @click="refreshAppsInfo"
                >
                  <span class="i-mdi:refresh mr-1 inline-block h-4 w-4" />
                  刷新
                </el-button>
              </div>
            </div>

            <div v-loading="loadingApps">
              <el-table v-if="appsInfo.length > 0" :data="appsInfo" stripe max-height="600" class="w-full" :default-sort="{ prop: 'appOnDisk', order: 'descending' }">
                <el-table-column prop="appId" label="App ID" width="100" sortable fixed />
                <el-table-column prop="name" label="应用名称" min-width="200" sortable show-overflow-tooltip>
                  <template #default="{ row }">
                    <div class="flex items-center gap-2">
                      <el-tag v-if="row.isRunning" type="success" size="small" effect="dark">
                        <span class="i-mdi:play inline-block h-3 w-3" />
                        运行中
                      </el-tag>
                      <span>{{ row.name || '-' }}</span>
                    </div>
                  </template>
                </el-table-column>
                <el-table-column prop="installDir" label="安装目录名" min-width="150" sortable show-overflow-tooltip />
                <el-table-column prop="appOnDisk" label="占用空间" width="130" sortable :sort-method="(a, b) => Number(a.appOnDisk - b.appOnDisk)" align="right">
                  <template #default="{ row }">
                    <div class="flex flex-col items-end">
                      <span v-if="row.appOnDisk" class="text-xs font-mono">
                        {{ formatBytes(row.appOnDisk) }}
                      </span>
                      <span v-if="row.appOnDiskReal" class="text-xs text-gray-500 font-mono">
                        实际: {{ formatBytes(row.appOnDiskReal) }}
                      </span>
                    </div>
                  </template>
                </el-table-column>
                <el-table-column
                  label="状态"
                  width="90"
                  align="center"
                  filter-placement="bottom"
                  :filter-multiple="false"
                  :filters="[{ text: '已安装', value: 'true' }, { text: '未安装', value: 'false' }]"
                  :filter-method="(value, row) => row.installed === (value === 'true')"
                >
                  <template #default="{ row }">
                    <el-tag :type="row.installed ? 'success' : 'danger'" size="small">
                      {{ row.installed ? '已安装' : '未安装' }}
                    </el-tag>
                  </template>
                </el-table-column>
                <el-table-column label="更新时间" width="160" sortable>
                  <template #default="{ row }">
                    <span class="text-xs">{{ row.refreshTime ? new Date(row.refreshTime).toLocaleString() : '-' }}</span>
                  </template>
                </el-table-column>
              </el-table>

              <div v-else-if="!loadingApps" class="py-8">
                <el-empty description="暂无已安装应用">
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
</style>
