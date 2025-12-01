<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'
import { toast } from 'vue-sonner'

const electronApi = (window as any).electron

const isJobRunning = ref(false)
const intervalSeconds = ref(5)
const autoStart = ref(false)
const loading = ref(false)
const loadingAutoStart = ref(false)

// 更新相关状态
const currentVersion = ref('')
const autoUpdate = ref(true)
const loadingAutoUpdate = ref(false)
const checkingUpdate = ref(false)
const updateAvailable = ref(false)
const latestVersion = ref('')
const downloading = ref(false)
const downloadProgress = ref(0)
const downloadSpeed = ref(0)
const updateDownloaded = ref(false)
const updateError = ref('')

// 获取任务状态
async function fetchJobStatus() {
  try {
    const status = await electronApi.jobGetUpdateAppRunningStatusJobStatus()
    isJobRunning.value = status.isRunning
    intervalSeconds.value = Math.floor(status.intervalTime / 1000) // 毫秒转秒
  }
  catch (error: any) {
    toast.error(`获取任务状态失败: ${error?.message || error}`)
  }
}

// 切换任务开关
async function toggleJob(value: string | number | boolean) {
  const boolValue = Boolean(value)
  loading.value = true
  try {
    if (boolValue) {
      await electronApi.jobStartUpdateAppRunningStatusJob()
      toast.success('已启动定期检测运行的应用')
    }
    else {
      await electronApi.jobStopUpdateAppRunningStatusJob()
      toast.success('已停止定期检测运行的应用')
    }
    await fetchJobStatus()
    await saveJobSettings()
  }
  catch (error: any) {
    toast.error(`操作失败: ${error?.message || error}`)
    // 失败时恢复开关状态
    isJobRunning.value = !boolValue
  }
  finally {
    loading.value = false
  }
}

// 更新检测间隔
async function updateInterval() {
  // 验证输入
  if (!intervalSeconds.value || intervalSeconds.value < 1) {
    toast.error('检测间隔必须至少为 1 秒')
    intervalSeconds.value = 1
    return
  }

  loading.value = true
  try {
    await electronApi.jobSetUpdateAppRunningStatusJobInterval(intervalSeconds.value)
    toast.success(`已设置检测间隔为 ${intervalSeconds.value} 秒`)
    await fetchJobStatus()
    await saveJobSettings()
  }
  catch (error: any) {
    toast.error(`设置失败: ${error?.message || error}`)
  }
  finally {
    loading.value = false
  }
}

// 获取开机自启状态
async function fetchAutoStartStatus() {
  try {
    autoStart.value = await electronApi.settingsGetAutoStart()
  }
  catch (error: any) {
    toast.error(`获取开机自启状态失败: ${error?.message || error}`)
  }
}

// 切换开机自启
async function toggleAutoStart(value: string | number | boolean) {
  const boolValue = Boolean(value)
  loadingAutoStart.value = true
  try {
    const result = await electronApi.settingsUpdate({ autoStart: boolValue })
    if (result.success) {
      toast.success(boolValue ? '已启用开机自启' : '已关闭开机自启', {
        duration: 1000,
      })
      await fetchAutoStartStatus()
    }
    else {
      toast.error('设置开机自启失败')
      autoStart.value = !boolValue
    }
  }
  catch (error: any) {
    toast.error(`操作失败: ${error?.message || error}`)
    autoStart.value = !boolValue
  }
  finally {
    loadingAutoStart.value = false
  }
}

// 保存设置到持久化存储
async function saveJobSettings() {
  try {
    const result = await electronApi.settingsUpdate({
      updateAppRunningStatusJob: {
        enabled: isJobRunning.value,
        intervalSeconds: intervalSeconds.value,
      },
    })
    if (result.success) {
      toast.success('设置已保存', { duration: 700 })
    }
  }
  catch (error: any) {
    console.error('保存设置失败:', error)
  }
}

// 获取当前版本
async function fetchCurrentVersion() {
  try {
    currentVersion.value = await electronApi.updateGetCurrentVersion()
  }
  catch (error: any) {
    console.error('获取版本信息失败:', error)
  }
}

// 获取更新状态
async function fetchUpdateStatus() {
  try {
    const status = await electronApi.updateGetStatus()
    autoUpdate.value = status.autoUpdateEnabled
    checkingUpdate.value = status.isChecking
    downloading.value = status.isDownloading
    currentVersion.value = status.currentVersion
  }
  catch (error: any) {
    console.error('获取更新状态失败:', error)
  }
}

// 切换自动更新
async function toggleAutoUpdate(value: string | number | boolean) {
  const boolValue = Boolean(value)
  loadingAutoUpdate.value = true
  try {
    const result = await electronApi.updateSetAutoUpdate(boolValue)
    if (result.success) {
      toast.success(boolValue ? '已启用自动更新' : '已关闭自动更新', {
        duration: 1000,
      })
      await fetchUpdateStatus()
    }
    else {
      toast.error('设置自动更新失败')
      autoUpdate.value = !boolValue
    }
  }
  catch (error: any) {
    toast.error(`操作失败: ${error?.message || error}`)
    autoUpdate.value = !boolValue
  }
  finally {
    loadingAutoUpdate.value = false
  }
}

// 手动检查更新
async function checkForUpdates() {
  if (checkingUpdate.value) {
    toast.warning('正在检查更新中，请稍后再试')
    return
  }

  checkingUpdate.value = true
  updateError.value = ''
  try {
    await electronApi.updateCheckForUpdates()
    toast.info('正在检查更新...')
  }
  catch (error: any) {
    toast.error(`检查更新失败: ${error?.message || error}`)
    checkingUpdate.value = false
  }
}

// 下载更新
async function downloadUpdate() {
  try {
    await electronApi.updateDownloadUpdate()
    toast.info('开始下载更新...')
  }
  catch (error: any) {
    toast.error(`下载失败: ${error?.message || error}`)
  }
}

// 退出并安装
async function quitAndInstall() {
  try {
    await electronApi.updateQuitAndInstall()
  }
  catch (error: any) {
    toast.error(`安装失败: ${error?.message || error}`)
  }
}

// 监听更新事件
function handleUpdateEvent(data: any) {
  const { event, data: eventData } = data

  switch (event) {
    case 'checking-for-update':
      checkingUpdate.value = true
      updateError.value = ''
      break

    case 'update-available':
      checkingUpdate.value = false
      updateAvailable.value = true
      latestVersion.value = eventData.version
      toast.success(`发现新版本 ${eventData.version}！`, {
        duration: 3000,
      })
      break

    case 'update-not-available':
      checkingUpdate.value = false
      updateAvailable.value = false
      toast.info('当前已是最新版本', {
        duration: 2000,
      })
      break

    case 'download-progress':
      downloading.value = true
      downloadProgress.value = Math.floor(eventData.percent)
      downloadSpeed.value = eventData.bytesPerSecond
      break

    case 'update-downloaded':
      downloading.value = false
      downloadProgress.value = 100
      updateDownloaded.value = true
      toast.success(`版本 ${eventData.version} 下载完成！`, {
        duration: 3000,
      })
      break

    case 'update-error':
      checkingUpdate.value = false
      downloading.value = false
      updateError.value = eventData.message
      toast.error(`更新错误: ${eventData.message}`)
      break
  }
}

// 页面加载时获取状态
onMounted(() => {
  fetchJobStatus()
  fetchAutoStartStatus()
  fetchCurrentVersion()
  fetchUpdateStatus()

  // 监听更新事件
  electronApi.onUpdateEvent(handleUpdateEvent)
})

// 组件销毁时移除监听器
onBeforeUnmount(() => {
  electronApi.removeUpdateEventListener()
})
</script>

<template>
  <div>
    <FaPageHeader title="设置" />
    <FaPageMain>
      <div class="space-y-6">
        <!-- 系统设置卡片 -->
        <Transition name="slide-fade" appear>
          <div class="rounded-lg bg-[var(--g-container-bg)] p-6 shadow-lg">
            <div class="mb-6 flex items-center gap-3">
              <span class="i-mdi:application-cog inline-block h-8 w-8 text-primary" />
              <div>
                <h3 class="text-2xl font-bold">
                  系统设置
                </h3>
                <p class="text-sm text-gray-500">
                  配置应用系统相关设置
                </p>
              </div>
            </div>

            <!-- 开机自启动 -->
            <Transition name="fade" appear>
              <div
                class="group border rounded-lg from-orange-50 to-red-50 bg-gradient-to-r p-6 transition-all dark:from-orange-900/20 dark:to-red-900/20 hover:shadow-md"
              >
                <div class="flex items-center justify-between">
                  <div class="flex items-center gap-4">
                    <div
                      class="h-14 w-14 flex items-center justify-center rounded-full from-orange-500 to-red-500 bg-gradient-to-br shadow-lg"
                    >
                      <span class="i-mdi:power inline-block h-7 w-7 text-white" />
                    </div>
                    <div class="flex-1">
                      <div class="flex items-center gap-4">
                        <h4 class="text-lg font-bold">
                          开机自启动
                        </h4>
                        <el-tag v-if="autoStart" type="success" effect="dark">
                          <span class="i-mdi:check-circle mr-1 inline-block h-3 w-3" />
                          已启用
                        </el-tag>
                        <el-tag v-else type="danger" effect="dark">
                          <span class="i-mdi:close-circle mr-1 inline-block h-3 w-3" />
                          未启用
                        </el-tag>
                      </div>
                      <p class="mt-1 text-sm text-gray-600 dark:text-gray-400">
                        开机时自动启动 Steam Stat 应用
                      </p>
                    </div>
                  </div>
                  <el-switch
                    v-model="autoStart" :loading="loadingAutoStart" size="large" active-color="#13ce66"
                    inactive-color="#dcdfe6" @change="toggleAutoStart"
                  />
                </div>
              </div>
            </Transition>
          </div>
        </Transition>

        <!-- 定时任务设置卡片 -->
        <Transition name="slide-fade" appear>
          <div class="rounded-lg bg-[var(--g-container-bg)] p-6 shadow-lg">
            <div class="mb-6 flex items-center gap-3">
              <span class="i-mdi:cog inline-block h-8 w-8 text-primary" />
              <div>
                <h3 class="text-2xl font-bold">
                  应用检测设置
                </h3>
                <p class="text-sm text-gray-500">
                  配置定期检测运行中的 Steam 应用功能
                </p>
              </div>
            </div>

            <div class="space-y-6">
              <!-- 启用检测开关 -->
              <Transition name="fade" appear>
                <div
                  class="group flex items-center justify-between border rounded-lg from-blue-50 to-indigo-50 bg-gradient-to-r p-6 transition-all dark:from-blue-900/20 dark:to-indigo-900/20 hover:shadow-md"
                >
                  <div class="flex items-center gap-4">
                    <div
                      class="h-14 w-14 flex items-center justify-center rounded-full from-primary to-purple-500 bg-gradient-to-br shadow-lg"
                    >
                      <span class="i-mdi:radar inline-block h-7 w-7 text-white" />
                    </div>
                    <div class="flex-1">
                      <div class="flex items-center gap-4">
                        <h4 class="text-lg font-bold">
                          启用定期检测
                        </h4>
                        <el-tag v-if="isJobRunning" type="success" effect="dark">
                          <span class="i-mdi:check-circle mr-1 inline-block h-3 w-3" />
                          运行中
                        </el-tag>
                        <el-tag v-else type="danger" effect="dark">
                          <span class="i-mdi:pause-circle mr-1 inline-block h-3 w-3" />
                          已停止
                        </el-tag>
                      </div>
                      <p class="mt-1 text-sm text-gray-600 dark:text-gray-400">
                        自动检测正在运行的 Steam 应用，支持获取运行应用列表和获取数据进行统计
                      </p>
                    </div>
                  </div>
                  <el-switch
                    v-model="isJobRunning" :loading="loading" size="large" active-color="#13ce66"
                    inactive-color="#dcdfe6" @change="toggleJob"
                  />
                </div>
              </Transition>

              <!-- 检测间隔设置 -->
              <Transition name="fade" appear>
                <div
                  class="group border rounded-lg from-green-50 to-emerald-50 bg-gradient-to-r p-6 transition-all dark:from-green-900/20 dark:to-emerald-900/20 hover:shadow-md"
                  :class="{ 'opacity-50': !isJobRunning }"
                >
                  <div class="mb-4 flex justify-between">
                    <div class="flex items-center justify-center gap-4">
                      <div
                        class="h-14 w-14 flex items-center justify-center rounded-full from-green-500 to-emerald-500 bg-gradient-to-br shadow-md"
                      >
                        <span class="i-mdi:clock-outline inline-block h-6 w-6 text-white" />
                      </div>
                      <div>
                        <h4 class="text-lg font-bold">
                          检测时间间隔
                        </h4>
                        <p class="text-sm text-gray-600 dark:text-gray-400">
                          设置每次检测的时间间隔（最低 1 秒，最高 3600 秒）
                        </p>
                      </div>
                    </div>
                    <div class="flex items-center justify-center gap-4">
                      <el-input-number
                        v-model="intervalSeconds" :min="1" :max="3600" :step="1"
                        :disabled="!isJobRunning || loading" size="large" class="flex-1"
                      />
                      <el-button
                        type="primary" :loading="loading" :disabled="!isJobRunning" size="large"
                        @click="updateInterval"
                      >
                        <span class="i-mdi:content-save mr-1 inline-block h-5 w-5" />
                        保存间隔
                      </el-button>
                    </div>
                  </div>

                  <div class="mt-4 rounded-lg bg-white/50 p-3 dark:bg-black/20">
                    <div class="flex items-start gap-2">
                      <span class="i-mdi:information mt-0.5 inline-block h-5 w-5 text-blue-500" />
                      <div class="text-xs text-gray-600 dark:text-gray-400">
                        <p class="mb-1 font-semibold">
                          说明：
                        </p>
                        <ul class="list-disc pl-4 space-y-1">
                          <li>检测间隔越短，数据更新越及时，但会占用更多系统资源</li>
                          <li>建议设置为 5-10 秒以获得最佳平衡</li>
                        </ul>
                      </div>
                    </div>
                  </div>
                </div>
              </Transition>

              <!-- 功能说明卡片 -->
              <Transition name="fade" appear>
                <div
                  class="border rounded-lg from-purple-50 to-pink-50 bg-gradient-to-r p-6 dark:from-purple-900/20 dark:to-pink-900/20"
                >
                  <div class="mb-3 flex items-center gap-2">
                    <span class="i-mdi:information-outline inline-block h-6 w-6 text-purple-600 dark:text-purple-400" />
                    <h4 class="text-lg font-bold">
                      功能说明
                    </h4>
                  </div>
                  <div class="text-sm text-gray-700 space-y-2 dark:text-gray-300">
                    <p class="flex items-start gap-2">
                      <span class="i-mdi:check-circle text-success mt-0.5 inline-block h-4 w-4" />
                      <span>启用定期检测后，系统会自动检测当前运行的 Steam 应用</span>
                    </p>
                    <p class="flex items-start gap-2">
                      <span class="i-mdi:check-circle text-success mt-0.5 inline-block h-4 w-4" />
                      <span>可在"Steam 应用信息"页面查看实时的运行中应用列表</span>
                    </p>
                    <p class="flex items-start gap-2">
                      <span class="i-mdi:check-circle text-success mt-0.5 inline-block h-4 w-4" />
                      <span>系统会自动记录每个应用的使用时长，可在"使用统计"页面查看详细数据</span>
                    </p>
                    <p class="flex items-start gap-2">
                      <span class="i-mdi:alert-circle text-warning mt-0.5 inline-block h-4 w-4" />
                      <span>关闭此功能后，将无法获取运行中应用列表和获取新的统计数据</span>
                    </p>
                  </div>
                </div>
              </Transition>
            </div>
          </div>
        </Transition>

        <!-- 应用更新设置卡片 -->
        <Transition name="slide-fade" appear>
          <div class="rounded-lg bg-[var(--g-container-bg)] p-6 shadow-lg">
            <div class="mb-6 flex items-center gap-3">
              <span class="i-mdi:cloud-download inline-block h-8 w-8 text-primary" />
              <div>
                <h3 class="text-2xl font-bold">
                  应用更新
                </h3>
                <p class="text-sm text-gray-500">
                  管理应用程序的版本更新
                </p>
              </div>
            </div>

            <div class="space-y-6">
              <!-- 当前版本信息 -->
              <Transition name="fade" appear>
                <div
                  class="group border rounded-lg from-cyan-50 to-blue-50 bg-gradient-to-r p-6 transition-all dark:from-cyan-900/20 dark:to-blue-900/20 hover:shadow-md"
                >
                  <div class="flex items-center gap-4">
                    <div
                      class="h-14 w-14 flex items-center justify-center rounded-full from-cyan-500 to-blue-500 bg-gradient-to-br shadow-lg"
                    >
                      <span class="i-mdi:application inline-block h-7 w-7 text-white" />
                    </div>
                    <div class="flex-1">
                      <div class="mt-1 flex items-center gap-4">
                        <div>
                          <h4 class="text-lg font-bold">
                            当前版本
                          </h4>
                          <p class="text-sm text-gray-600 dark:text-gray-400">
                            当前应用的版本信息
                          </p>
                        </div>
                        <el-tag type="primary" size="large" effect="dark">
                          <span class="i-mdi:tag mr-1 inline-block h-4 w-4" />
                          {{ currentVersion || '加载中...' }}
                        </el-tag>
                        <el-tag v-if="updateAvailable" type="warning" size="large" effect="dark">
                          <span class="i-mdi:alert-circle mr-1 inline-block h-3 w-3" />
                          有可用更新
                        </el-tag>
                        <el-tag v-else-if="!checkingUpdate" type="success" size="large" effect="dark">
                          <span class="i-mdi:check-circle mr-1 inline-block h-3 w-3" />
                          已是最新版本
                        </el-tag>
                      </div>
                    </div>
                  </div>
                </div>
              </Transition>

              <!-- 自动更新开关 -->
              <Transition name="fade" appear>
                <div
                  class="group border rounded-lg from-violet-50 to-purple-50 bg-gradient-to-r p-6 transition-all dark:from-violet-900/20 dark:to-purple-900/20 hover:shadow-md"
                >
                  <div class="flex items-center justify-between">
                    <div class="flex items-center gap-4">
                      <div
                        class="h-14 w-14 flex items-center justify-center rounded-full from-violet-500 to-purple-500 bg-gradient-to-br shadow-lg"
                      >
                        <span class="i-mdi:update inline-block h-7 w-7 text-white" />
                      </div>
                      <div class="flex-1">
                        <div class="flex items-center gap-4">
                          <h4 class="text-lg font-bold">
                            自动更新
                          </h4>
                          <el-tag v-if="autoUpdate" type="success" effect="dark">
                            <span class="i-mdi:check-circle mr-1 inline-block h-3 w-3" />
                            已启用
                          </el-tag>
                          <el-tag v-else type="danger" effect="dark">
                            <span class="i-mdi:close-circle mr-1 inline-block h-3 w-3" />
                            未启用
                          </el-tag>
                        </div>
                        <p class="mt-1 text-sm text-gray-600 dark:text-gray-400">
                          启用后将自动检查并下载更新，关闭后需手动检查
                        </p>
                      </div>
                    </div>
                    <el-switch
                      v-model="autoUpdate" :loading="loadingAutoUpdate" size="large" active-color="#13ce66"
                      inactive-color="#dcdfe6" @change="toggleAutoUpdate"
                    />
                  </div>
                </div>
              </Transition>

              <!-- 手动检查更新 -->
              <Transition name="fade" appear>
                <div
                  class="group border rounded-lg from-amber-50 to-yellow-50 bg-gradient-to-r p-6 transition-all dark:from-amber-900/20 dark:to-yellow-900/20 hover:shadow-md"
                >
                  <div class="flex items-center justify-between">
                    <div class="flex items-center gap-4">
                      <div
                        class="h-14 w-14 flex items-center justify-center rounded-full from-amber-500 to-yellow-500 bg-gradient-to-br shadow-md"
                      >
                        <span class="i-mdi:magnify inline-block h-6 w-6 text-white" />
                      </div>
                      <div>
                        <h4 class="text-lg font-bold">
                          检查更新
                        </h4>
                        <p class="text-sm text-gray-600 dark:text-gray-400">
                          手动检查是否有可用的新版本
                        </p>
                      </div>
                    </div>

                    <div class="flex items-center gap-4">
                      <el-button
                        type="primary" :loading="checkingUpdate" :disabled="downloading" size="large"
                        @click="checkForUpdates"
                      >
                        <span class="i-mdi:refresh mr-1 inline-block h-5 w-5" />
                        {{ checkingUpdate ? '检查中...' : '检查更新' }}
                      </el-button>

                      <el-button
                        v-if="updateAvailable && !autoUpdate" type="success" :loading="downloading"
                        size="large" @click="downloadUpdate"
                      >
                        <span class="i-mdi:download mr-1 inline-block h-5 w-5" />
                        下载更新
                      </el-button>

                      <el-button v-if="updateDownloaded" type="warning" size="large" @click="quitAndInstall">
                        <span class="i-mdi:restart mr-1 inline-block h-5 w-5" />
                        重启并安装
                      </el-button>
                    </div>
                  </div>

                  <div v-if="(updateAvailable && latestVersion) || downloading || updateDownloaded || updateError" class="mt-4 space-y-4">
                    <!-- 更新状态提示 -->
                    <div v-if="updateAvailable && latestVersion" class="rounded-lg bg-blue-50 p-3 dark:bg-blue-900/20">
                      <div class="flex items-start gap-2">
                        <span class="i-mdi:information mt-0.5 inline-block h-5 w-5 text-blue-500" />
                        <div class="text-sm text-gray-700 dark:text-gray-300">
                          <p class="font-semibold">
                            发现新版本：{{ latestVersion }}
                          </p>
                          <p class="mt-1 text-xs text-gray-600 dark:text-gray-400">
                            {{ autoUpdate ? '正在自动下载更新...' : '请点击“下载更新”按钮开始下载' }}
                          </p>
                        </div>
                      </div>
                    </div>

                    <!-- 下载进度 -->
                    <div v-if="downloading" class="space-y-2">
                      <div class="flex items-center justify-between text-sm">
                        <span class="text-gray-700 font-semibold dark:text-gray-300">下载进度</span>
                        <span class="text-primary font-bold">{{ downloadProgress }}%</span>
                      </div>
                      <el-progress
                        :percentage="downloadProgress" :stroke-width="12" :show-text="false"
                        status="success"
                      />
                      <div class="flex items-center justify-between text-xs text-gray-600 dark:text-gray-400">
                        <span>下载速度：{{ (downloadSpeed / 1024 / 1024).toFixed(2) }} MB/s</span>
                        <span>请稍候...</span>
                      </div>
                    </div>

                    <!-- 更新完成提示 -->
                    <div v-if="updateDownloaded" class="rounded-lg bg-green-50 p-3 dark:bg-green-900/20">
                      <div class="flex items-start gap-2">
                        <span class="i-mdi:check-circle mt-0.5 inline-block h-5 w-5 text-green-500" />
                        <div class="text-sm text-gray-700 dark:text-gray-300">
                          <p class="font-semibold">
                            更新下载完成！
                          </p>
                          <p class="mt-1 text-xs text-gray-600 dark:text-gray-400">
                            点击“重启并安装”按钮完成更新，或等待应用退出时自动安装
                          </p>
                        </div>
                      </div>
                    </div>

                    <!-- 错误提示 -->
                    <div v-if="updateError" class="rounded-lg bg-red-50 p-3 dark:bg-red-900/20">
                      <div class="flex items-start gap-2">
                        <span class="i-mdi:alert-circle mt-0.5 inline-block h-5 w-5 text-red-500" />
                        <div class="text-sm text-gray-700 dark:text-gray-300">
                          <p class="font-semibold">
                            更新错误
                          </p>
                          <p class="mt-1 text-xs text-gray-600 dark:text-gray-400">
                            {{ updateError }}
                          </p>
                        </div>
                      </div>
                    </div>
                  </div>
                </div>
              </Transition>

              <!-- 更新说明 -->
              <Transition name="fade" appear>
                <div
                  class="border rounded-lg from-teal-50 to-cyan-50 bg-gradient-to-r p-6 dark:from-teal-900/20 dark:to-cyan-900/20"
                >
                  <div class="mb-3 flex items-center gap-2">
                    <span class="i-mdi:information-outline inline-block h-6 w-6 text-teal-600 dark:text-teal-400" />
                    <h4 class="text-lg font-bold">
                      更新说明
                    </h4>
                  </div>
                  <div class="text-sm text-gray-700 space-y-2 dark:text-gray-300">
                    <p class="flex items-start gap-2">
                      <span class="i-mdi:check-circle text-success mt-0.5 inline-block h-4 w-4" />
                      <span>启用自动更新后，应用将在启动时和每 4 小时自动检查并下载更新</span>
                    </p>
                    <p class="flex items-start gap-2">
                      <span class="i-mdi:check-circle text-success mt-0.5 inline-block h-4 w-4" />
                      <span>更新下载完成后，将在下次退出应用时自动安装</span>
                    </p>
                    <p class="flex items-start gap-2">
                      <span class="i-mdi:check-circle text-success mt-0.5 inline-block h-4 w-4" />
                      <span>也可以点击“重启并安装”按钮立即完成更新</span>
                    </p>
                    <p class="flex items-start gap-2">
                      <span class="i-mdi:information mt-0.5 inline-block h-4 w-4 text-primary" />
                      <span>更新源为 GitHub Releases，如遇网络问题请手动下载</span>
                    </p>
                  </div>
                </div>
              </Transition>
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

.fade-enter-active {
  transition: all 0.3s ease;
}

.fade-enter-from {
  opacity: 0;
  transform: translateY(-10px);
}
</style>
