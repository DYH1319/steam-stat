<script setup lang="ts">
import type { DeepPartial } from '@/utils/types'
import { onBeforeUnmount, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { toast } from 'vue-sonner'

const electronApi = (window as Window).electron
const { t, locale } = useI18n()

// electron api
// @ts-expect-error ignore
const appSettings = ref<AppSettings>({ updateAppRunningStatusJob: {} })
// @ts-expect-error ignore
const updaterStatus = ref<UpdaterStatus>({})

const loading = ref(false)

// 更新相关状态
const updateAvailable = ref(false)
const latestVersion = ref('')
const downloadProgress = ref(0)
const downloadSpeed = ref(0)
const updateDownloaded = ref(false)
const updateError = ref('')
const hasCheckedForUpdate = ref(false)

onMounted(async () => {
  loading.value = true

  await fetchSettings()
  await fetchUpdateStatus()

  loading.value = false

  // 监听更新器事件
  electronApi.updaterEventOnListener(handleUpdateEvent)
})

onBeforeUnmount(() => {
  electronApi.updaterEventRemoveListener()
})

// 获取设置
async function fetchSettings() {
  try {
    appSettings.value = await electronApi.settingGet()
  }
  catch (error: any) {
    toast.error(`${t('common.getFailed')}: ${error?.message || error}`)
  }
}

// 获取更新状态
async function fetchUpdateStatus() {
  try {
    updaterStatus.value = await electronApi.updaterGetStatus()
  }
  catch (error: any) {
    toast.error(`${t('common.failed')}: ${error?.message || error}`)
  }
}

// 更新设置
async function updateSettings(partialSettings: DeepPartial<AppSettings>) {
  loading.value = true
  try {
    const result = await electronApi.settingUpdate(partialSettings as Partial<AppSettings>)
    // 更新设置后的处理
    if (result) {
      // 更新语言
      if (partialSettings.language !== undefined) {
        locale.value = partialSettings.language
        toast.success(t('settings.languageSwitched'))
      }
      // 切换开机自启
      if (partialSettings.autoStart !== undefined) {
        toast.success(partialSettings.autoStart ? t('settings.autoStartSuccess') : t('settings.autoStartDisabled2'))
      }
      // 切换静默启动
      if (partialSettings.silentStart !== undefined) {
        toast.success(partialSettings.silentStart ? t('settings.silentStartSuccess') : t('settings.silentStartDisabled2'))
      }
      // 切换关闭应用行为
      if (partialSettings.closeAction !== undefined) {
        toast.success(t('settings.closeActionSet'))
      }
      // 切换定时检测正在运行应用的任务
      if (partialSettings.updateAppRunningStatusJob !== undefined) {
        if (partialSettings.updateAppRunningStatusJob.enabled !== undefined) {
          toast.success(partialSettings.updateAppRunningStatusJob.enabled ? t('settings.detectionEnabled') : t('settings.detectionDisabled'))
        }
        if (partialSettings.updateAppRunningStatusJob.intervalSeconds !== undefined) {
          toast.success(t('settings.intervalSet', { seconds: partialSettings.updateAppRunningStatusJob.intervalSeconds }))
        }
      }
      // 切换自动更新
      if (partialSettings.autoUpdate !== undefined) {
        toast.success(partialSettings.autoUpdate ? t('settings.autoUpdateSuccess') : t('settings.autoUpdateDisabled2'))
      }
    }
    else {
      toast.error(t('settings.saveFailed'))
    }
  }
  catch (error: any) {
    toast.error(`${t('settings.saveFailed')}: ${error?.message || error}`)
  }
  finally {
    loading.value = false
  }
}

// 手动检查更新
function checkForUpdates() {
  try {
    electronApi.updaterCheck()
  }
  catch (error: any) {
    toast.error(`${t('settings.updateCheckFailed')}: ${error?.message || error}`)
  }
}

// 下载更新
function downloadUpdate() {
  try {
    toast.info(t('settings.downloading'))
    electronApi.updaterDownload()
  }
  catch (error: any) {
    toast.error(`${t('settings.downloadFailed')}: ${error?.message || error}`)
  }
}

// 退出并安装
function quitAndInstall() {
  try {
    electronApi.updaterQuitAndInstall()
  }
  catch (error: any) {
    toast.error(`${t('settings.installFailed')}: ${error?.message || error}`)
  }
}

// 监听更新事件
function handleUpdateEvent(data: { updaterEvent: string, data?: any }) {
  const { updaterEvent, data: eventData } = data

  switch (updaterEvent) {
    case 'checking-for-update':
      toast.info(t('settings.checkingForUpdates'))
      updaterStatus.value.isChecking = true
      updateError.value = ''
      break

    case 'update-available':
      updaterStatus.value.isChecking = false
      updateAvailable.value = true
      latestVersion.value = eventData.version
      toast.success(t('settings.foundNewVersion', { version: eventData.version }), {
        duration: 3000,
      })
      break

    case 'update-not-available':
      updaterStatus.value.isChecking = false
      updateAvailable.value = false
      hasCheckedForUpdate.value = true
      toast.info(t('settings.alreadyLatest'), {
        duration: 2000,
      })
      break

    case 'download-progress':
      updaterStatus.value.isDownloading = true
      downloadProgress.value = Math.floor(eventData.percent)
      downloadSpeed.value = eventData.bytesPerSecond
      break

    case 'update-downloaded':
      updaterStatus.value.isDownloading = false
      downloadProgress.value = 100
      updateDownloaded.value = true
      toast.success(t('settings.downloadComplete', { version: eventData.version }), {
        duration: 3000,
      })
      break

    case 'update-error':
      updaterStatus.value.isChecking = false
      updaterStatus.value.isDownloading = false
      updateError.value = eventData.message
      toast.error(`${t('settings.updateError')}: ${eventData.message}`)
      break
  }
}
</script>

<template>
  <div>
    <FaPageMain class="mb-0">
      <div class="space-y-6">
        <!-- 系统设置卡片 -->
        <Transition name="slide-fade" appear>
          <div class="rounded-lg bg-[var(--g-container-bg)] p-6 shadow-lg">
            <div class="mb-6 flex items-center gap-3">
              <span class="i-mdi:application-cog inline-block h-8 w-8 text-primary" />
              <div>
                <h3 class="text-2xl font-bold">
                  {{ t('settings.general') }}
                </h3>
                <p class="text-sm text-gray-500">
                  {{ t('settings.subtitle') }}
                </p>
              </div>
            </div>

            <div class="space-y-6">
              <!-- 语言设置 -->
              <Transition name="fade" appear>
                <div
                  class="group border rounded-lg from-indigo-50 to-purple-50 bg-gradient-to-r p-6 transition-all dark:from-indigo-900/20 dark:to-purple-900/20 hover:shadow-md"
                >
                  <div class="flex items-center justify-between">
                    <div class="flex items-center gap-4">
                      <div
                        class="h-14 w-14 flex items-center justify-center rounded-full from-indigo-500 to-purple-500 bg-gradient-to-br shadow-lg"
                      >
                        <span class="i-mdi:translate inline-block h-7 w-7 text-white" />
                      </div>
                      <div class="flex-1">
                        <h4 class="text-lg font-bold">
                          {{ t('settings.language') }}
                        </h4>
                        <p class="mt-1 text-sm text-gray-600 dark:text-gray-400">
                          {{ t('settings.languageDesc') }}
                        </p>
                      </div>
                    </div>
                    <el-select
                      v-model="appSettings.language"
                      :loading="loading"
                      size="large"
                      style="width: 150px;"
                      @change="updateSettings({ language: appSettings.language })"
                    >
                      <el-option label="简体中文" value="zh-CN" />
                      <el-option label="English" value="en-US" />
                    </el-select>
                  </div>
                </div>
              </Transition>

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
                            {{ t('settings.autoStart') }}
                          </h4>
                          <el-tag v-if="appSettings.autoStart" type="success" effect="dark">
                            <span class="i-mdi:check-circle mr-1 inline-block h-3 w-3" />
                            {{ t('settings.autoStartEnabled') }}
                          </el-tag>
                          <el-tag v-else type="danger" effect="dark">
                            <span class="i-mdi:close-circle mr-1 inline-block h-3 w-3" />
                            {{ t('settings.autoStartDisabled') }}
                          </el-tag>
                        </div>
                        <p class="mt-1 text-sm text-gray-600 dark:text-gray-400">
                          {{ t('settings.autoStartDesc') }}
                        </p>
                      </div>
                    </div>
                    <el-switch
                      v-model="appSettings.autoStart" :loading="loading" size="large" active-color="#13ce66"
                      inactive-color="#dcdfe6" @change="updateSettings({ autoStart: appSettings.autoStart })"
                    />
                  </div>
                </div>
              </Transition>

              <!-- 静默启动 -->
              <Transition name="fade" appear>
                <div
                  class="group border rounded-lg from-teal-50 to-cyan-50 bg-gradient-to-r p-6 transition-all dark:from-teal-900/20 dark:to-cyan-900/20 hover:shadow-md"
                  :class="{ 'opacity-50': !appSettings.autoStart }"
                >
                  <div class="flex items-center justify-between">
                    <div class="flex items-center gap-4">
                      <div
                        class="h-14 w-14 flex items-center justify-center rounded-full from-teal-500 to-cyan-500 bg-gradient-to-br shadow-lg"
                      >
                        <span class="i-mdi:eye-off inline-block h-7 w-7 text-white" />
                      </div>
                      <div class="flex-1">
                        <div class="flex items-center gap-4">
                          <h4 class="text-lg font-bold">
                            {{ t('settings.silentStart') }}
                          </h4>
                          <el-tag v-if="appSettings.silentStart" type="success" effect="dark">
                            <span class="i-mdi:check-circle mr-1 inline-block h-3 w-3" />
                            {{ t('settings.silentStartEnabled') }}
                          </el-tag>
                          <el-tag v-else type="danger" effect="dark">
                            <span class="i-mdi:close-circle mr-1 inline-block h-3 w-3" />
                            {{ t('settings.silentStartDisabled') }}
                          </el-tag>
                        </div>
                        <p class="mt-1 text-sm text-gray-600 dark:text-gray-400">
                          {{ t('settings.silentStartDesc') }}
                        </p>
                      </div>
                    </div>
                    <el-switch
                      v-model="appSettings.silentStart"
                      :loading="loading"
                      :disabled="!appSettings.autoStart"
                      size="large"
                      active-color="#13ce66"
                      inactive-color="#dcdfe6"
                      @change="updateSettings({ silentStart: appSettings.silentStart })"
                    />
                  </div>
                </div>
              </Transition>

              <!-- 关闭应用行为 -->
              <Transition name="fade" appear>
                <div
                  class="group border rounded-lg from-pink-50 to-rose-50 bg-gradient-to-r p-6 transition-all dark:from-pink-900/20 dark:to-rose-900/20 hover:shadow-md"
                >
                  <div class="flex items-center justify-between">
                    <div class="flex items-center gap-4">
                      <div
                        class="h-14 w-14 flex items-center justify-center rounded-full from-pink-500 to-rose-500 bg-gradient-to-br shadow-lg"
                      >
                        <span class="i-mdi:close-box inline-block h-7 w-7 text-white" />
                      </div>
                      <div class="flex-1">
                        <h4 class="text-lg font-bold">
                          {{ t('settings.closeAction') }}
                        </h4>
                        <p class="mt-1 text-sm text-gray-600 dark:text-gray-400">
                          {{ t('settings.closeActionDesc') }}
                        </p>
                      </div>
                    </div>
                    <el-select
                      v-model="appSettings.closeAction"
                      :loading="loading"
                      size="large"
                      style="width: 180px;"
                      @change="updateSettings({ closeAction: appSettings.closeAction })"
                    >
                      <el-option :label="t('settings.exitDirectly')" value="exit">
                        <span class="flex items-center gap-2">
                          <span class="i-mdi:exit-to-app inline-block h-4 w-4" />
                          {{ t('settings.exitDirectly') }}
                        </span>
                      </el-option>
                      <el-option :label="t('settings.minimizeToTray')" value="minimize">
                        <span class="flex items-center gap-2">
                          <span class="i-mdi:tray-arrow-down inline-block h-4 w-4" />
                          {{ t('settings.minimizeToTray') }}
                        </span>
                      </el-option>
                      <el-option :label="t('settings.askEveryTime')" value="ask">
                        <span class="flex items-center gap-2">
                          <span class="i-mdi:help-circle inline-block h-4 w-4" />
                          {{ t('settings.askEveryTime') }}
                        </span>
                      </el-option>
                    </el-select>
                  </div>
                </div>
              </Transition>
            </div>
          </div>
        </Transition>

        <!-- 定时任务设置卡片 -->
        <Transition name="slide-fade" appear>
          <div class="rounded-lg bg-[var(--g-container-bg)] p-6 shadow-lg">
            <div class="mb-6 flex items-center gap-3">
              <span class="i-mdi:cog inline-block h-8 w-8 text-primary" />
              <div>
                <h3 class="text-2xl font-bold">
                  {{ t('settings.appDetection') }}
                </h3>
                <p class="text-sm text-gray-500">
                  {{ t('settings.appDetectionDesc') }}
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
                          {{ t('settings.enableDetection') }}
                        </h4>
                        <el-tag v-if="appSettings.updateAppRunningStatusJob.enabled" type="success" effect="dark">
                          <span class="i-mdi:check-circle mr-1 inline-block h-3 w-3" />
                          {{ t('settings.detectionRunning') }}
                        </el-tag>
                        <el-tag v-else type="danger" effect="dark">
                          <span class="i-mdi:pause-circle mr-1 inline-block h-3 w-3" />
                          {{ t('settings.detectionStopped') }}
                        </el-tag>
                      </div>
                      <p class="mt-1 text-sm text-gray-600 dark:text-gray-400">
                        {{ t('settings.detectionDesc') }}
                      </p>
                    </div>
                  </div>
                  <el-switch
                    v-model="appSettings.updateAppRunningStatusJob.enabled" :loading="loading" size="large" active-color="#13ce66"
                    inactive-color="#dcdfe6" @change="updateSettings({ updateAppRunningStatusJob: { enabled: appSettings.updateAppRunningStatusJob.enabled } })"
                  />
                </div>
              </Transition>

              <!-- 检测间隔设置 -->
              <Transition name="fade" appear>
                <div
                  class="group border rounded-lg from-green-50 to-emerald-50 bg-gradient-to-r p-6 transition-all dark:from-green-900/20 dark:to-emerald-900/20 hover:shadow-md"
                  :class="{ 'opacity-50': !appSettings.updateAppRunningStatusJob.enabled }"
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
                          {{ t('settings.detectionInterval') }}
                        </h4>
                        <p class="text-sm text-gray-600 dark:text-gray-400">
                          {{ t('settings.detectionIntervalDesc') }}
                        </p>
                      </div>
                    </div>
                    <div class="flex items-center justify-center gap-4">
                      <el-input-number
                        v-model="appSettings.updateAppRunningStatusJob.intervalSeconds" :min="1" :max="3600" :step="1"
                        :disabled="!appSettings.updateAppRunningStatusJob.enabled || loading" size="large" class="flex-1"
                      />
                      <el-button
                        type="primary" :loading="loading" :disabled="!appSettings.updateAppRunningStatusJob.enabled" size="large"
                        @click="updateSettings({ updateAppRunningStatusJob: { intervalSeconds: appSettings.updateAppRunningStatusJob.intervalSeconds } })"
                      >
                        <span class="i-mdi:content-save mr-1 inline-block h-5 w-5" />
                        {{ t('settings.saveInterval') }}
                      </el-button>
                    </div>
                  </div>

                  <div class="mt-4 rounded-lg bg-white/50 p-3 dark:bg-black/20">
                    <div class="flex items-start gap-2">
                      <span class="i-mdi:information mt-0.5 inline-block h-5 w-5 text-blue-500" />
                      <div class="text-xs text-gray-600 dark:text-gray-400">
                        <p class="mb-1 font-semibold">
                          {{ t('settings.detectionNote') }}
                        </p>
                        <ul class="list-disc pl-4 space-y-1">
                          <li>{{ t('settings.detectionNote1') }}</li>
                          <li>{{ t('settings.detectionNote2') }}</li>
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
                      {{ t('settings.featureDescription') }}
                    </h4>
                  </div>
                  <div class="text-sm text-gray-700 space-y-2 dark:text-gray-300">
                    <p class="flex items-start gap-2">
                      <span class="i-mdi:check-circle text-success mt-0.5 inline-block h-4 w-4" />
                      <span>{{ t('settings.feature1') }}</span>
                    </p>
                    <p class="flex items-start gap-2">
                      <span class="i-mdi:check-circle text-success mt-0.5 inline-block h-4 w-4" />
                      <span>{{ t('settings.feature2') }}</span>
                    </p>
                    <p class="flex items-start gap-2">
                      <span class="i-mdi:check-circle text-success mt-0.5 inline-block h-4 w-4" />
                      <span>{{ t('settings.feature3') }}</span>
                    </p>
                    <p class="flex items-start gap-2">
                      <span class="i-mdi:alert-circle text-warning mt-0.5 inline-block h-4 w-4" />
                      <span>{{ t('settings.feature4') }}</span>
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
                  {{ t('settings.appUpdate') }}
                </h3>
                <p class="text-sm text-gray-500">
                  {{ t('settings.appUpdateDesc') }}
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
                            {{ t('settings.currentVersion') }}
                          </h4>
                          <p class="text-sm text-gray-600 dark:text-gray-400">
                            {{ t('settings.currentVersionDesc') }}
                          </p>
                        </div>
                        <el-tag type="primary" size="large" effect="dark">
                          <span class="i-mdi:tag mr-1 inline-block h-4 w-4" />
                          {{ updaterStatus.currentVersion ? `v${updaterStatus.currentVersion}` : t('settings.loadingVersion') }}
                        </el-tag>
                        <el-tag v-if="updateAvailable" type="warning" size="large" effect="dark">
                          <span class="i-mdi:alert-circle mr-1 inline-block h-3 w-3" />
                          {{ t('settings.updateAvailable') }}
                        </el-tag>
                        <el-tag v-else-if="hasCheckedForUpdate" type="success" size="large" effect="dark">
                          <span class="i-mdi:check-circle mr-1 inline-block h-3 w-3" />
                          {{ t('settings.alreadyLatest') }}
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
                            {{ t('settings.autoUpdate') }}
                          </h4>
                          <el-tag v-if="appSettings.autoUpdate" type="success" effect="dark">
                            <span class="i-mdi:check-circle mr-1 inline-block h-3 w-3" />
                            {{ t('settings.autoUpdateEnabled') }}
                          </el-tag>
                          <el-tag v-else type="danger" effect="dark">
                            <span class="i-mdi:close-circle mr-1 inline-block h-3 w-3" />
                            {{ t('settings.autoUpdateDisabled') }}
                          </el-tag>
                        </div>
                        <p class="mt-1 text-sm text-gray-600 dark:text-gray-400">
                          {{ t('settings.autoUpdateDesc') }}
                        </p>
                      </div>
                    </div>
                    <el-switch
                      v-model="appSettings.autoUpdate" :loading="loading" size="large" active-color="#13ce66"
                      inactive-color="#dcdfe6" @change="updateSettings({ autoUpdate: appSettings.autoUpdate })"
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
                          {{ t('settings.checkForUpdates') }}
                        </h4>
                        <p class="text-sm text-gray-600 dark:text-gray-400">
                          {{ t('settings.manualCheckDesc') }}
                        </p>
                      </div>
                    </div>

                    <div class="flex items-center gap-4">
                      <el-button
                        type="primary" :loading="updaterStatus.isChecking" :disabled="updaterStatus.isDownloading" size="large"
                        @click="checkForUpdates"
                      >
                        <span class="i-mdi:refresh mr-1 inline-block h-5 w-5" />
                        {{ updaterStatus.isChecking ? t('settings.checkingUpdate') : t('settings.checkUpdate') }}
                      </el-button>

                      <el-button
                        v-if="updateAvailable && !appSettings.autoUpdate" type="success" :loading="updaterStatus.isDownloading"
                        size="large" @click="downloadUpdate"
                      >
                        <span class="i-mdi:download mr-1 inline-block h-5 w-5" />
                        {{ t('settings.downloadUpdate') }}
                      </el-button>

                      <el-button v-if="updateDownloaded" type="warning" size="large" @click="quitAndInstall">
                        <span class="i-mdi:restart mr-1 inline-block h-5 w-5" />
                        {{ t('settings.installUpdate') }}
                      </el-button>
                    </div>
                  </div>

                  <div v-if="(updateAvailable && latestVersion) || updaterStatus.isDownloading || updateDownloaded || updateError" class="mt-4 space-y-4">
                    <!-- 更新状态提示 -->
                    <div v-if="updateAvailable && latestVersion" class="rounded-lg bg-blue-50 p-3 dark:bg-blue-900/20">
                      <div class="flex items-start gap-2">
                        <span class="i-mdi:information mt-0.5 inline-block h-5 w-5 text-blue-500" />
                        <div class="text-sm text-gray-700 dark:text-gray-300">
                          <p class="font-semibold">
                            {{ t('settings.newVersionAvailable', { version: latestVersion }) }}
                          </p>
                          <p class="mt-1 text-xs text-gray-600 dark:text-gray-400">
                            {{ appSettings.autoUpdate ? t('settings.downloadHint') : t('settings.downloadHintManual') }}
                          </p>
                        </div>
                      </div>
                    </div>

                    <!-- 下载进度 -->
                    <div v-if="updaterStatus.isDownloading" class="space-y-2">
                      <div class="flex items-center justify-between text-sm">
                        <span class="text-gray-700 font-semibold dark:text-gray-300">{{ t('settings.downloadProgress') }}</span>
                        <span class="text-primary font-bold">{{ downloadProgress }}%</span>
                      </div>
                      <el-progress
                        :percentage="downloadProgress" :stroke-width="12" :show-text="false"
                        status="success"
                      />
                      <div class="flex items-center justify-between text-xs text-gray-600 dark:text-gray-400">
                        <span>{{ t('settings.downloadSpeed') }}{{ (downloadSpeed / 1024 / 1024).toFixed(2) }} MB/s</span>
                        <span>{{ t('settings.waiting') }}</span>
                      </div>
                    </div>

                    <!-- 更新完成提示 -->
                    <div v-if="updateDownloaded" class="rounded-lg bg-green-50 p-3 dark:bg-green-900/20">
                      <div class="flex items-start gap-2">
                        <span class="i-mdi:check-circle mt-0.5 inline-block h-5 w-5 text-green-500" />
                        <div class="text-sm text-gray-700 dark:text-gray-300">
                          <p class="font-semibold">
                            {{ t('settings.downloadComplete', { version: latestVersion }) }}
                          </p>
                          <p class="mt-1 text-xs text-gray-600 dark:text-gray-400">
                            {{ t('settings.downloadCompleteDesc') }}
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
                            {{ t('settings.updateError') }}
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
                      {{ t('settings.updateNoteTitle') }}
                    </h4>
                  </div>
                  <div class="text-sm text-gray-700 space-y-2 dark:text-gray-300">
                    <p class="flex items-start gap-2">
                      <span class="i-mdi:check-circle text-success mt-0.5 inline-block h-4 w-4" />
                      <span>{{ t('settings.updateNote1') }}</span>
                    </p>
                    <p class="flex items-start gap-2">
                      <span class="i-mdi:check-circle text-success mt-0.5 inline-block h-4 w-4" />
                      <span>{{ t('settings.updateNote2') }}</span>
                    </p>
                    <p class="flex items-start gap-2">
                      <span class="i-mdi:information mt-0.5 inline-block h-4 w-4 text-primary" />
                      <span>{{ t('settings.updateNote3') }}</span>
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
