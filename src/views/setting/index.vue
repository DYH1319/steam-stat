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
    await new Promise(resolve => setTimeout(resolve, 500))
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
      // 切换启动主页
      if (partialSettings.homePage !== undefined) {
        toast.success(t('settings.homePageSet'))
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
      <div>
        <!-- 通用设置卡片 -->
        <Transition name="slide-fade" appear>
          <div class="rounded-lg bg-[var(--g-container-bg)] p-5">
            <div class="mb-4 flex items-center gap-2">
              <span class="i-mdi:application-cog inline-block h-6 w-6 text-primary" />
              <h3 class="text-lg font-bold">
                {{ t('settings.general') }}
              </h3>
            </div>

            <div class="divide-y divide-[var(--el-border-color-lighter)]">
              <!-- 语言设置 -->
              <div class="setting-row">
                <div class="setting-label">
                  <span class="i-mdi:translate inline-block h-5 w-5 text-primary" />
                  <div>
                    <div class="font-medium">
                      {{ t('settings.language') }}
                    </div>
                    <div class="text-xs text-gray-500">
                      {{ t('settings.languageDesc') }}
                    </div>
                  </div>
                </div>
                <el-select
                  v-model="appSettings.language" :loading="loading" style="width: 150px;"
                  @change="updateSettings({ language: appSettings.language })"
                >
                  <el-option label="简体中文" value="zh-CN" />
                  <el-option label="English" value="en-US" />
                </el-select>
              </div>

              <!-- 启动主页 -->
              <div class="setting-row">
                <div class="setting-label">
                  <span class="i-mdi:home inline-block h-5 w-5 text-primary" />
                  <div>
                    <div class="font-medium">
                      {{ t('settings.homePage') }}
                    </div>
                    <div class="text-xs text-gray-500">
                      {{ t('settings.homePageDesc') }}
                    </div>
                  </div>
                </div>
                <el-select
                  v-model="appSettings.homePage" :loading="loading" style="width: 180px;"
                  @change="updateSettings({ homePage: appSettings.homePage })"
                >
                  <el-option :label="t('menu.steamStatus')" value="/status">
                    <span class="flex items-center gap-2">
                      <span class="i-tabler:brand-steam inline-block h-4 w-4" />
                      {{ t('menu.steamStatus') }}
                    </span>
                  </el-option>
                  <el-option :label="t('menu.steamUser')" value="/user">
                    <span class="flex items-center gap-2">
                      <span class="i-mdi:user-group inline-block h-4 w-4" />
                      {{ t('menu.steamUser') }}
                    </span>
                  </el-option>
                  <el-option :label="t('menu.steamApp')" value="/app">
                    <span class="flex items-center gap-2">
                      <span class="i-iconamoon:apps inline-block h-4 w-4" />
                      {{ t('menu.steamApp') }}
                    </span>
                  </el-option>
                  <el-option :label="t('menu.steamUsage')" value="/useRecord">
                    <span class="flex items-center gap-2">
                      <span class="i-uil:statistics inline-block h-4 w-4" />
                      {{ t('menu.steamUsage') }}
                    </span>
                  </el-option>
                </el-select>
              </div>

              <!-- 开机自启动 -->
              <div class="setting-row">
                <div class="setting-label">
                  <span class="i-mdi:power inline-block h-5 w-5 text-primary" />
                  <div>
                    <div class="flex items-center gap-2 font-medium">
                      {{ t('settings.autoStart') }}
                      <el-tag v-if="appSettings.autoStart" type="success" size="small">
                        {{ t('settings.autoStartEnabled') }}
                      </el-tag>
                      <el-tag v-else type="info" size="small">
                        {{ t('settings.autoStartDisabled') }}
                      </el-tag>
                    </div>
                    <div class="text-xs text-gray-500">
                      {{ t('settings.autoStartDesc') }}
                    </div>
                  </div>
                </div>
                <el-switch
                  v-model="appSettings.autoStart" :loading="loading"
                  @change="updateSettings({ autoStart: appSettings.autoStart })"
                />
              </div>

              <!-- 静默启动 -->
              <div class="setting-row" :class="{ 'opacity-50': !appSettings.autoStart }">
                <div class="setting-label">
                  <span class="i-mdi:eye-off inline-block h-5 w-5 text-primary" />
                  <div>
                    <div class="flex items-center gap-2 font-medium">
                      {{ t('settings.silentStart') }}
                      <el-tag v-if="appSettings.silentStart" type="success" size="small">
                        {{ t('settings.silentStartEnabled') }}
                      </el-tag>
                      <el-tag v-else type="info" size="small">
                        {{ t('settings.silentStartDisabled') }}
                      </el-tag>
                    </div>
                    <div class="text-xs text-gray-500">
                      {{ t('settings.silentStartDesc') }}
                    </div>
                  </div>
                </div>
                <el-switch
                  v-model="appSettings.silentStart" :loading="loading" :disabled="!appSettings.autoStart"
                  @change="updateSettings({ silentStart: appSettings.silentStart })"
                />
              </div>

              <!-- 关闭应用行为 -->
              <div class="setting-row">
                <div class="setting-label">
                  <span class="i-mdi:close-box inline-block h-5 w-5 text-primary" />
                  <div>
                    <div class="font-medium">
                      {{ t('settings.closeAction') }}
                    </div>
                    <div class="text-xs text-gray-500">
                      {{ t('settings.closeActionDesc') }}
                    </div>
                  </div>
                </div>
                <el-select
                  v-model="appSettings.closeAction" :loading="loading" style="width: 180px;"
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
          </div>
        </Transition>

        <!-- 应用检测设置卡片 -->
        <Transition name="slide-fade" appear>
          <div class="rounded-lg bg-[var(--g-container-bg)] p-5">
            <div class="mb-4 flex items-center gap-2">
              <span class="i-mdi:cog inline-block h-6 w-6 text-primary" />
              <h3 class="text-lg font-bold">
                {{ t('settings.appDetection') }}
              </h3>
            </div>

            <div class="divide-y divide-[var(--el-border-color-lighter)]">
              <!-- 启用检测开关 -->
              <div class="setting-row">
                <div class="setting-label">
                  <span class="i-mdi:radar inline-block h-5 w-5 text-primary" />
                  <div>
                    <div class="flex items-center gap-2 font-medium">
                      {{ t('settings.enableDetection') }}
                      <el-tag v-if="appSettings.updateAppRunningStatusJob.enabled" type="success" size="small">
                        {{ t('settings.detectionRunning') }}
                      </el-tag>
                      <el-tag v-else type="danger" size="small">
                        {{ t('settings.detectionStopped') }}
                      </el-tag>
                    </div>
                    <div class="text-xs text-gray-500">
                      {{ t('settings.detectionDesc') }}
                    </div>
                  </div>
                </div>
                <el-switch
                  v-model="appSettings.updateAppRunningStatusJob.enabled" :loading="loading"
                  @change="updateSettings({ updateAppRunningStatusJob: { enabled: appSettings.updateAppRunningStatusJob.enabled } })"
                />
              </div>

              <!-- 检测间隔设置 -->
              <div class="setting-row" :class="{ 'opacity-50': !appSettings.updateAppRunningStatusJob.enabled }">
                <div class="setting-label">
                  <span class="i-mdi:clock-outline inline-block h-5 w-5 text-primary" />
                  <div>
                    <div class="font-medium">
                      {{ t('settings.detectionInterval') }}
                    </div>
                    <div class="text-xs text-gray-500">
                      {{ t('settings.detectionIntervalDesc') }}
                    </div>
                  </div>
                </div>
                <div class="flex items-center gap-3">
                  <el-input-number
                    v-model="appSettings.updateAppRunningStatusJob.intervalSeconds" :min="1" :max="3600" :step="1"
                    :disabled="!appSettings.updateAppRunningStatusJob.enabled || loading"
                  />
                  <el-button
                    type="primary" :loading="loading" :disabled="!appSettings.updateAppRunningStatusJob.enabled"
                    @click="updateSettings({ updateAppRunningStatusJob: { intervalSeconds: appSettings.updateAppRunningStatusJob.intervalSeconds } })"
                  >
                    <span class="i-mdi:content-save mr-1 inline-block h-4 w-4" />
                    {{ t('settings.saveInterval') }}
                  </el-button>
                </div>
              </div>
            </div>
          </div>
        </Transition>

        <!-- 应用更新设置卡片 -->
        <Transition name="slide-fade" appear>
          <div class="rounded-lg bg-[var(--g-container-bg)] p-5">
            <div class="mb-4 flex items-center gap-2">
              <span class="i-mdi:cloud-download inline-block h-6 w-6 text-primary" />
              <h3 class="text-lg font-bold">
                {{ t('settings.appUpdate') }}
              </h3>
            </div>

            <div class="divide-y divide-[var(--el-border-color-lighter)]">
              <!-- 当前版本信息 -->
              <div class="setting-row">
                <div class="setting-label">
                  <span class="i-mdi:application inline-block h-5 w-5 text-primary" />
                  <div>
                    <div class="font-medium">
                      {{ t('settings.currentVersion') }}
                    </div>
                    <div class="text-xs text-gray-500">
                      {{ t('settings.currentVersionDesc') }}
                    </div>
                  </div>
                </div>
                <div class="flex items-center gap-2">
                  <el-tag type="primary" effect="dark">
                    <span class="i-mdi:tag mr-1 inline-block h-3 w-3" />
                    {{ updaterStatus.currentVersion ? `v${updaterStatus.currentVersion}` : t('settings.loadingVersion') }}
                  </el-tag>
                  <el-tag v-if="updateAvailable" type="warning" effect="dark">
                    {{ t('settings.updateAvailable') }}
                  </el-tag>
                  <el-tag v-else-if="hasCheckedForUpdate" type="success" effect="dark">
                    {{ t('settings.alreadyLatest') }}
                  </el-tag>
                </div>
              </div>

              <!-- 自动更新开关 -->
              <div class="setting-row">
                <div class="setting-label">
                  <span class="i-mdi:update inline-block h-5 w-5 text-primary" />
                  <div>
                    <div class="flex items-center gap-2 font-medium">
                      {{ t('settings.autoUpdate') }}
                      <el-tag v-if="appSettings.autoUpdate" type="success" size="small">
                        {{ t('settings.autoUpdateEnabled') }}
                      </el-tag>
                      <el-tag v-else type="info" size="small">
                        {{ t('settings.autoUpdateDisabled') }}
                      </el-tag>
                    </div>
                    <div class="text-xs text-gray-500">
                      {{ t('settings.autoUpdateDesc') }}
                    </div>
                  </div>
                </div>
                <el-switch
                  v-model="appSettings.autoUpdate" :loading="loading"
                  @change="updateSettings({ autoUpdate: appSettings.autoUpdate })"
                />
              </div>

              <!-- 手动检查更新 -->
              <div class="setting-row">
                <div class="setting-label">
                  <span class="i-mdi:magnify inline-block h-5 w-5 text-primary" />
                  <div>
                    <div class="font-medium">
                      {{ t('settings.checkForUpdates') }}
                    </div>
                    <div class="text-xs text-gray-500">
                      {{ t('settings.manualCheckDesc') }}
                    </div>
                  </div>
                </div>
                <div class="flex items-center gap-2">
                  <el-button
                    type="primary" :loading="updaterStatus.isChecking" :disabled="updaterStatus.isDownloading"
                    @click="checkForUpdates"
                  >
                    <span class="i-mdi:refresh mr-1 inline-block h-4 w-4" />
                    {{ updaterStatus.isChecking ? t('settings.checkingUpdate') : t('settings.checkUpdate') }}
                  </el-button>
                  <el-button
                    v-if="updateAvailable && !appSettings.autoUpdate" type="success" :loading="updaterStatus.isDownloading"
                    @click="downloadUpdate"
                  >
                    <span class="i-mdi:download mr-1 inline-block h-4 w-4" />
                    {{ t('settings.downloadUpdate') }}
                  </el-button>
                  <el-button v-if="updateDownloaded" type="warning" @click="quitAndInstall">
                    <span class="i-mdi:restart mr-1 inline-block h-4 w-4" />
                    {{ t('settings.installUpdate') }}
                  </el-button>
                </div>
              </div>
            </div>

            <!-- 更新状态区域 -->
            <div v-if="(updateAvailable && latestVersion) || updaterStatus.isDownloading || updateDownloaded || updateError" class="mt-4 space-y-3">
              <div v-if="updateAvailable && latestVersion" class="rounded-md bg-[var(--el-color-primary-light-9)] p-3">
                <div class="flex items-start gap-2">
                  <span class="i-mdi:information mt-0.5 inline-block h-4 w-4 text-primary" />
                  <div class="text-sm">
                    <p class="font-medium">
                      {{ t('settings.newVersionAvailable', { version: latestVersion }) }}
                    </p>
                    <p class="mt-1 text-xs text-gray-500">
                      {{ appSettings.autoUpdate ? t('settings.downloadHint') : t('settings.downloadHintManual') }}
                    </p>
                  </div>
                </div>
              </div>

              <div v-if="updaterStatus.isDownloading" class="space-y-2">
                <div class="flex items-center justify-between text-sm">
                  <span class="font-medium">{{ t('settings.downloadProgress') }}</span>
                  <span class="text-primary font-bold">{{ downloadProgress }}%</span>
                </div>
                <el-progress :percentage="downloadProgress" :stroke-width="8" :show-text="false" status="success" />
                <div class="flex items-center justify-between text-xs text-gray-500">
                  <span>{{ t('settings.downloadSpeed') }}{{ (downloadSpeed / 1024 / 1024).toFixed(2) }} MB/s</span>
                  <span>{{ t('settings.waiting') }}</span>
                </div>
              </div>

              <div v-if="updateDownloaded" class="rounded-md bg-[var(--el-color-success-light-9)] p-3">
                <div class="flex items-start gap-2">
                  <span class="i-mdi:check-circle mt-0.5 inline-block h-4 w-4 text-[var(--el-color-success)]" />
                  <div class="text-sm">
                    <p class="font-medium">
                      {{ t('settings.downloadComplete', { version: latestVersion }) }}
                    </p>
                    <p class="mt-1 text-xs text-gray-500">
                      {{ t('settings.downloadCompleteDesc') }}
                    </p>
                  </div>
                </div>
              </div>

              <div v-if="updateError" class="rounded-md bg-[var(--el-color-danger-light-9)] p-3">
                <div class="flex items-start gap-2">
                  <span class="i-mdi:alert-circle mt-0.5 inline-block h-4 w-4 text-[var(--el-color-danger)]" />
                  <div class="text-sm">
                    <p class="font-medium">
                      {{ t('settings.updateError') }}
                    </p>
                    <p class="mt-1 text-xs text-gray-500">
                      {{ updateError }}
                    </p>
                  </div>
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

.slide-fade-enter-active {
  transition: all 0.4s ease;
}

.slide-fade-enter-from {
  opacity: 0;
  transform: translateY(-20px);
}
</style>
