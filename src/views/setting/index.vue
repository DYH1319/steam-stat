<script setup lang="ts">
import type { DeepPartial } from '@/utils/types'
import { Button, InputNumber, Progress, Select, SelectOption, Switch, Tag } from 'ant-design-vue'
import { useI18n } from 'vue-i18n'
import { toast } from 'vue-sonner'

const electronApi = (window as Window).electron
const { t, locale } = useI18n()
const settingsStore = useSettingsStore()
const updaterStore = useUpdaterStore()

// electron api
const appSettings = ref<AppSettings>({ updateAppRunningStatusJob: {} } as AppSettings)

const loading = ref(false)

onMounted(async () => {
  loading.value = true

  await fetchSettings()

  loading.value = false
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

// 更新设置
async function updateSettings(partialSettings: DeepPartial<AppSettings>) {
  loading.value = true
  try {
    await new Promise(resolve => setTimeout(resolve, 100))
    const result = await electronApi.settingUpdate(partialSettings as Partial<AppSettings>)
    // 更新设置后的处理
    if (result) {
      // 更新语言
      if (partialSettings.language !== undefined) {
        locale.value = partialSettings.language
        settingsStore.setLocale(partialSettings.language)
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
      // 切换主题
      if (partialSettings.colorScheme !== undefined) {
        settingsStore.setColorScheme(partialSettings.colorScheme === 'system' ? '' : partialSettings.colorScheme)
        toast.success(t('settings.colorSchemeSet'))
      }
      // 切换定时检测正在运行应用的任务
      if (partialSettings.updateAppRunningStatusJob !== undefined) {
        if (partialSettings.updateAppRunningStatusJob.enabled !== undefined) {
          toast.success(partialSettings.updateAppRunningStatusJob.enabled ? t('settings.detectionEnabled') : t('settings.detectionDisabled'))
        }
        if (partialSettings.updateAppRunningStatusJob.intervalSeconds !== undefined) {
          if (partialSettings.updateAppRunningStatusJob.intervalSeconds === null) {
            toast.warning(t('settings.intervalNotNull'))
          }
          else {
            toast.success(t('settings.intervalSet', { seconds: partialSettings.updateAppRunningStatusJob.intervalSeconds }))
          }
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
                <Select
                  v-model:value="appSettings.language" :loading="loading" style="width: 150px;"
                  @change="updateSettings({ language: appSettings.language })"
                >
                  <SelectOption value="zh-CN">
                    简体中文
                  </SelectOption>
                  <SelectOption value="en-US">
                    English
                  </SelectOption>
                </Select>
              </div>

              <!-- 主题设置 -->
              <div class="setting-row">
                <div class="setting-label">
                  <span class="i-mdi:theme-light-dark inline-block h-5 w-5 text-primary" />
                  <div>
                    <div class="font-medium">
                      {{ t('settings.colorScheme') }}
                    </div>
                    <div class="text-xs text-gray-500">
                      {{ t('settings.colorSchemeDesc') }}
                    </div>
                  </div>
                </div>
                <Select
                  v-model:value="appSettings.colorScheme" :loading="loading" style="width: 180px;"
                  @change="updateSettings({ colorScheme: appSettings.colorScheme })"
                >
                  <SelectOption value="light">
                    <span class="flex items-center gap-2">
                      <span class="i-ri:sun-line inline-block h-4 w-4" />
                      {{ t('titleBar.lightMode') }}
                    </span>
                  </SelectOption>
                  <SelectOption value="dark">
                    <span class="flex items-center gap-2">
                      <span class="i-ri:moon-line inline-block h-4 w-4" />
                      {{ t('titleBar.darkMode') }}
                    </span>
                  </SelectOption>
                  <SelectOption value="system">
                    <span class="flex items-center gap-2">
                      <span class="i-ri:computer-line inline-block h-4 w-4" />
                      {{ t('titleBar.systemMode') }}
                    </span>
                  </SelectOption>
                </Select>
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
                <Select
                  v-model:value="appSettings.homePage" :loading="loading" style="width: 180px;"
                  @change="updateSettings({ homePage: appSettings.homePage })"
                >
                  <SelectOption value="/status">
                    <span class="flex items-center gap-2">
                      <span class="i-tabler:brand-steam inline-block h-4 w-4" />
                      {{ t('menu.steamStatus') }}
                    </span>
                  </SelectOption>
                  <SelectOption value="/user">
                    <span class="flex items-center gap-2">
                      <span class="i-mdi:user-group inline-block h-4 w-4" />
                      {{ t('menu.steamUser') }}
                    </span>
                  </SelectOption>
                  <SelectOption value="/app">
                    <span class="flex items-center gap-2">
                      <span class="i-iconamoon:apps inline-block h-4 w-4" />
                      {{ t('menu.steamApp') }}
                    </span>
                  </SelectOption>
                  <SelectOption value="/useRecord">
                    <span class="flex items-center gap-2">
                      <span class="i-uil:statistics inline-block h-4 w-4" />
                      {{ t('menu.steamUsage') }}
                    </span>
                  </SelectOption>
                </Select>
              </div>

              <!-- 开机自启动 -->
              <div class="setting-row">
                <div class="setting-label">
                  <span class="i-mdi:power inline-block h-5 w-5 text-primary" />
                  <div>
                    <div class="flex items-center gap-2 font-medium">
                      {{ t('settings.autoStart') }}
                      <Tag v-if="appSettings.autoStart" color="success">
                        {{ t('settings.autoStartEnabled') }}
                      </Tag>
                      <Tag v-else color="default">
                        {{ t('settings.autoStartDisabled') }}
                      </Tag>
                    </div>
                    <div class="text-xs text-gray-500">
                      {{ t('settings.autoStartDesc') }}
                    </div>
                  </div>
                </div>
                <Switch
                  v-model:checked="appSettings.autoStart" :loading="loading"
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
                      <Tag v-if="appSettings.silentStart" color="success">
                        {{ t('settings.silentStartEnabled') }}
                      </Tag>
                      <Tag v-else color="default">
                        {{ t('settings.silentStartDisabled') }}
                      </Tag>
                    </div>
                    <div class="text-xs text-gray-500">
                      {{ t('settings.silentStartDesc') }}
                    </div>
                  </div>
                </div>
                <Switch
                  v-model:checked="appSettings.silentStart" :loading="loading" :disabled="!appSettings.autoStart"
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
                <Select
                  v-model:value="appSettings.closeAction" :loading="loading" style="width: 180px;"
                  @change="updateSettings({ closeAction: appSettings.closeAction })"
                >
                  <SelectOption value="exit">
                    <span class="flex items-center gap-2">
                      <span class="i-mdi:exit-to-app inline-block h-4 w-4" />
                      {{ t('settings.exitDirectly') }}
                    </span>
                  </SelectOption>
                  <SelectOption value="minimize">
                    <span class="flex items-center gap-2">
                      <span class="i-mdi:tray-arrow-down inline-block h-4 w-4" />
                      {{ t('settings.minimizeToTray') }}
                    </span>
                  </SelectOption>
                  <SelectOption value="ask">
                    <span class="flex items-center gap-2">
                      <span class="i-mdi:help-circle inline-block h-4 w-4" />
                      {{ t('settings.askEveryTime') }}
                    </span>
                  </SelectOption>
                </Select>
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
                  v-model:checked="appSettings.updateAppRunningStatusJob.enabled" :loading="loading"
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
                  <InputNumber
                    v-model:value="appSettings.updateAppRunningStatusJob.intervalSeconds" :min="1" :max="60" :step="1" size="large"
                    :disabled="!appSettings.updateAppRunningStatusJob.enabled || loading"
                    class="w-32"
                    @blur="updateSettings({ updateAppRunningStatusJob: { intervalSeconds: appSettings.updateAppRunningStatusJob.intervalSeconds } })"
                  />
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
                  <Tag color="processing" class="flex items-center gap-1">
                    <span class="i-mdi:tag h-3 w-3" />
                    {{ updaterStore.updaterStatus.currentVersion ? `v${updaterStore.updaterStatus.currentVersion}` : t('settings.loadingVersion') }}
                  </Tag>
                  <Tag v-if="updaterStore.updaterStatus.isChecking" color="warning">
                    {{ t('settings.checkingForUpdates') }}
                  </Tag>
                  <Tag v-else-if="updaterStore.updateAvailable" color="warning">
                    {{ t('settings.updateAvailable') }}
                  </Tag>
                  <Tag v-else-if="updaterStore.hasCheckedForUpdate" color="success">
                    {{ t('settings.alreadyLatest') }}
                  </Tag>
                </div>
              </div>

              <!-- 自动更新开关 -->
              <div class="setting-row">
                <div class="setting-label">
                  <span class="i-mdi:update inline-block h-5 w-5 text-primary" />
                  <div>
                    <div class="flex items-center gap-2 font-medium">
                      {{ t('settings.autoUpdate') }}
                      <Tag v-if="appSettings.autoUpdate" color="success">
                        {{ t('settings.autoUpdateEnabled') }}
                      </Tag>
                      <Tag v-else color="default">
                        {{ t('settings.autoUpdateDisabled') }}
                      </Tag>
                    </div>
                    <div class="text-xs text-gray-500">
                      {{ t('settings.autoUpdateDesc') }}
                    </div>
                  </div>
                </div>
                <Switch
                  v-model:checked="appSettings.autoUpdate" :loading="loading"
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
                  <Button
                    type="primary"
                    :loading="updaterStore.updaterStatus.isChecking"
                    :disabled="updaterStore.updaterStatus.isDownloading"
                    class="flex items-center gap-1"
                    @click="checkForUpdates"
                  >
                    <template #icon>
                      <span class="i-mdi:refresh h-4 w-4" />
                    </template>
                    {{ updaterStore.updaterStatus.isChecking ? t('settings.checkingUpdate') : t('settings.checkUpdate') }}
                  </Button>
                  <Button
                    v-if="updaterStore.updateAvailable && !appSettings.autoUpdate"
                    type="default"
                    :loading="updaterStore.updaterStatus.isDownloading"
                    class="flex items-center gap-1"
                    @click="downloadUpdate"
                  >
                    <template #icon>
                      <span class="i-mdi:download h-4 w-4" />
                    </template>
                    {{ t('settings.downloadUpdate') }}
                  </Button>
                  <Button
                    v-if="updaterStore.updateDownloaded"
                    type="default"
                    class="flex items-center gap-1"
                    @click="quitAndInstall"
                  >
                    <template #icon>
                      <span class="i-mdi:restart h-4 w-4" />
                    </template>
                    {{ t('settings.installUpdate') }}
                  </Button>
                </div>
              </div>
            </div>

            <!-- 更新状态区域 -->
            <div v-if="(updaterStore.updateAvailable && updaterStore.latestVersion) || updaterStore.updaterStatus.isDownloading || updaterStore.updateDownloaded || updaterStore.updateError" class="mt-4 space-y-3">
              <div v-if="updaterStore.updateAvailable && updaterStore.latestVersion" class="rounded-md bg-[var(--el-color-primary-light-9)] p-3">
                <div class="flex items-start gap-2">
                  <span class="i-mdi:information mt-0.5 inline-block h-4 w-4 text-primary" />
                  <div class="text-sm">
                    <p class="font-medium">
                      {{ t('settings.newVersionAvailable', { version: updaterStore.latestVersion }) }}
                    </p>
                    <p class="mt-1 text-xs text-gray-500">
                      {{ appSettings.autoUpdate ? t('settings.downloadHint') : t('settings.downloadHintManual') }}
                    </p>
                  </div>
                </div>
              </div>

              <div v-if="updaterStore.updaterStatus.isDownloading" class="space-y-2">
                <div class="flex items-center justify-between text-sm">
                  <span class="font-medium">{{ t('settings.downloadProgress') }}</span>
                  <span>{{ t('settings.downloadSpeed') }}{{ (updaterStore.downloadSpeed / 1024 / 1024).toFixed(2) }} MB/s</span>
                </div>
                <Progress :percent="updaterStore.downloadProgress" status="active" />
              </div>

              <div v-if="updaterStore.updateDownloaded" class="rounded-md bg-[var(--el-color-success-light-9)] p-3">
                <div class="flex items-start gap-2">
                  <span class="i-mdi:check-circle mt-0.5 inline-block h-4 w-4 text-[var(--el-color-success)]" />
                  <div class="text-sm">
                    <p class="font-medium">
                      {{ t('settings.downloadComplete', { version: updaterStore.latestVersion }) }}
                    </p>
                    <p class="mt-1 text-xs text-gray-500">
                      {{ t('settings.downloadCompleteDesc') }}
                    </p>
                  </div>
                </div>
              </div>

              <div v-if="updaterStore.updateError" class="rounded-md bg-[var(--el-color-danger-light-9)] p-3">
                <div class="flex items-start gap-2">
                  <span class="i-mdi:alert-circle mt-0.5 inline-block h-4 w-4 text-[var(--el-color-danger)]" />
                  <div class="text-sm">
                    <p class="font-medium">
                      {{ t('settings.updateError') }}
                    </p>
                    <p class="mt-1 text-xs text-gray-500">
                      {{ updaterStore.updateError }}
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
