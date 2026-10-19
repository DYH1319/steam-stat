<script setup lang="ts">
import type { ThemeColorName } from '@/themes/colors'
import type { DeepPartial } from '@/utils/types'
import { Button, Progress, Select, SelectOption, Slider, Switch, Tag, Tooltip } from 'ant-design-vue'
import { useI18n } from 'vue-i18n'
import { toast } from 'vue-sonner'
import useZoom from '@/composables/useZoom'
import { themeColors } from '@/themes/colors'
import { getAppSettings, updateAppSettings } from '@/utils/appSettings'

const electronApi = (window as Window).electron
const { t, locale } = useI18n()
const appSettingsStore = useAppSettingsStore()
const appearanceStore = useAppearanceStore()
const updaterStore = useUpdaterStore()
const { zoomFactor, ZOOM_MIN, ZOOM_MAX, ZOOM_STEP, initZoom } = useZoom()

const themeColorEntries = Object.entries(themeColors) as [ThemeColorName, typeof themeColors[ThemeColorName]][]

// electron api
const appSettings = ref<AppSettings>({ updateAppRunningStatusJob: {} } as AppSettings)

const loading = ref(false)

onMounted(async () => {
  loading.value = true

  await fetchSettings()
  await initZoom()

  loading.value = false
})

// 获取设置
async function fetchSettings() {
  try {
    appSettings.value = await getAppSettings()
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
    const result = await updateAppSettings(partialSettings as Partial<AppSettings>)
    // 更新设置后的处理
    if (result) {
      // 更新语言
      if (partialSettings.language !== undefined) {
        locale.value = partialSettings.language
        appearanceStore.setLocale(partialSettings.language)
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
        appearanceStore.setColorScheme(partialSettings.colorScheme === 'system' ? '' : partialSettings.colorScheme)
        toast.success(t('settings.colorSchemeSet'))
      }
      // 切换主题色
      if (partialSettings.themeColor !== undefined) {
        toast.success(t('settings.themeColorSet'))
      }
      // 切换圆角系数
      if (partialSettings.radius !== undefined) {
        appearanceStore.setRadius(partialSettings.radius)
        toast.success(t('settings.radiusSet'))
      }
      // 切换自动更新
      if (partialSettings.autoUpdate !== undefined) {
        toast.success(partialSettings.autoUpdate ? t('settings.autoUpdateSuccess') : t('settings.autoUpdateDisabled2'))
      }
      // 切换缩放
      if (partialSettings.zoomFactor !== undefined) {
        zoomFactor.value = partialSettings.zoomFactor
        toast.success(t('settings.zoomSet', { percent: Math.round(zoomFactor.value * 100) }))
      }
      // 切换实验性功能：需要重新注册动态路由，因此重载渲染进程
      if (partialSettings.experimentalFeatures !== undefined) {
        toast.success(t('settings.experimentalReloading'))
        setTimeout(() => window.location.reload(), 800)
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
</script>

<template>
  <div>
    <FaPageMain class="mb-0">
      <div>
        <!-- 通用设置卡片 -->
        <Transition name="slide-fade" appear>
          <div class="p-5 rounded-lg bg-[var(--g-container-bg)]">
            <div class="mb-4 flex gap-2 items-center">
              <span class="i-mdi:application-cog h-6 w-6 inline-block" />
              <h3 class="text-lg font-bold">
                {{ t('settings.general') }}
              </h3>
            </div>

            <div class="divide-[var(--el-border-color-lighter)] divide-y">
              <!-- 语言设置 -->
              <div class="setting-row">
                <div class="setting-label">
                  <span class="i-mdi:translate text-primary h-5 w-5 inline-block" />
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
                  <span class="i-mdi:theme-light-dark text-primary h-5 w-5 inline-block" />
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
                    <span class="flex gap-2 items-center">
                      <span class="i-ri:sun-line h-4 w-4 inline-block" />
                      {{ t('titleBar.lightMode') }}
                    </span>
                  </SelectOption>
                  <SelectOption value="dark">
                    <span class="flex gap-2 items-center">
                      <span class="i-ri:moon-line h-4 w-4 inline-block" />
                      {{ t('titleBar.darkMode') }}
                    </span>
                  </SelectOption>
                  <SelectOption value="system">
                    <span class="flex gap-2 items-center">
                      <span class="i-ri:computer-line h-4 w-4 inline-block" />
                      {{ t('titleBar.systemMode') }}
                    </span>
                  </SelectOption>
                </Select>
              </div>

              <!-- 主题色设置 -->
              <div class="setting-row">
                <div class="setting-label">
                  <span class="i-mdi:palette text-primary h-5 w-5 inline-block" />
                  <div>
                    <div class="font-medium">
                      {{ t('settings.themeColor') }}
                    </div>
                    <div class="text-xs text-gray-500">
                      {{ t('settings.themeColorDesc') }}
                    </div>
                  </div>
                </div>
                <div class="flex gap-3 items-center">
                  <Tooltip v-for="[name, def] in themeColorEntries" :key="name" :title="t(`settings.themeColor${name.charAt(0).toUpperCase()}${name.slice(1)}`)">
                    <button
                      class="theme-color-btn"
                      :class="{ 'theme-color-btn-active': appSettings.themeColor === name }"
                      :style="{ '--btn-color': appSettingsStore.currentColorScheme === 'dark' ? def.colorDark : def.color }"
                      :disabled="loading"
                      @click="appSettings.themeColor = name; appearanceStore.setThemeColor(name); updateSettings({ themeColor: name })"
                    >
                      <span v-if="appSettings.themeColor === name" class="i-mdi:check text-white h-3.5 w-3.5 inline-block" />
                    </button>
                  </Tooltip>
                </div>
              </div>

              <!-- 圆角系数设置 -->
              <div class="setting-row">
                <div class="setting-label">
                  <span class="i-mdi:border-radius text-primary h-5 w-5 inline-block" />
                  <div>
                    <div class="font-medium">
                      {{ t('settings.radius') }}
                    </div>
                    <div class="text-xs text-gray-500">
                      {{ t('settings.radiusDesc') }}
                    </div>
                  </div>
                </div>
                <div class="flex gap-4 items-center" style="width: 260px;">
                  <Slider
                    v-model:value="appSettings.radius"
                    :min="0"
                    :max="1"
                    :step="0.05"
                    :disabled="loading"
                    class="flex-1"
                    @after-change="updateSettings({ radius: appSettings.radius })"
                  />
                  <span class="text-sm text-gray-500 min-w-12">{{ (appSettings.radius * 100).toFixed(0) }}%</span>
                </div>
              </div>

              <!-- 界面缩放 -->
              <div class="setting-row">
                <div class="setting-label">
                  <span class="i-mdi:magnify-plus-outline text-primary h-5 w-5 inline-block" />
                  <div>
                    <div class="font-medium">
                      {{ t('settings.zoom') }}
                    </div>
                    <div class="text-xs text-gray-500">
                      {{ t('settings.zoomDesc') }}
                    </div>
                  </div>
                </div>
                <div class="flex gap-4 items-center" style="width: 340px;">
                  <Slider
                    v-model:value="zoomFactor"
                    :min="ZOOM_MIN"
                    :max="ZOOM_MAX"
                    :step="ZOOM_STEP"
                    :disabled="loading"
                    class="flex-1"
                    @after-change="updateSettings({ zoomFactor })"
                  />
                  <span class="text-sm text-gray-500 min-w-12">{{ (zoomFactor * 100).toFixed(0) }}%</span>
                  <Button :disabled="loading" @click="updateSettings({ zoomFactor: 1 })">
                    {{ t('settings.zoomReset') }}
                  </Button>
                </div>
              </div>

              <!-- 启动主页 -->
              <div class="setting-row">
                <div class="setting-label">
                  <span class="i-mdi:home text-primary h-5 w-5 inline-block" />
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
                  <SelectOption value="/data-source/local">
                    <span class="flex gap-2 items-center">
                      <span class="i-mdi:harddisk h-4 w-4 inline-block" />
                      {{ t('menu.dataSource') }} / {{ t('menu.dataSourceLocal') }}
                    </span>
                  </SelectOption>
                  <SelectOption value="/user">
                    <span class="flex gap-2 items-center">
                      <span class="i-mdi:user-group h-4 w-4 inline-block" />
                      {{ t('menu.steamUser') }}
                    </span>
                  </SelectOption>
                  <SelectOption value="/app">
                    <span class="flex gap-2 items-center">
                      <span class="i-iconamoon:apps h-4 w-4 inline-block" />
                      {{ t('menu.steamApp') }}
                    </span>
                  </SelectOption>
                  <SelectOption value="/useRecord">
                    <span class="flex gap-2 items-center">
                      <span class="i-uil:statistics h-4 w-4 inline-block" />
                      {{ t('menu.steamUsage') }}
                    </span>
                  </SelectOption>
                </Select>
              </div>

              <!-- 开机自启动 -->
              <div class="setting-row">
                <div class="setting-label">
                  <span class="i-mdi:power text-primary h-5 w-5 inline-block" />
                  <div>
                    <div class="font-medium flex gap-2 items-center">
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
                  <span class="i-mdi:eye-off text-primary h-5 w-5 inline-block" />
                  <div>
                    <div class="font-medium flex gap-2 items-center">
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
                  <span class="i-mdi:close-box text-primary h-5 w-5 inline-block" />
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
                    <span class="flex gap-2 items-center">
                      <span class="i-mdi:exit-to-app h-4 w-4 inline-block" />
                      {{ t('settings.exitDirectly') }}
                    </span>
                  </SelectOption>
                  <SelectOption value="minimize">
                    <span class="flex gap-2 items-center">
                      <span class="i-mdi:tray-arrow-down h-4 w-4 inline-block" />
                      {{ t('settings.minimizeToTray') }}
                    </span>
                  </SelectOption>
                  <SelectOption value="ask">
                    <span class="flex gap-2 items-center">
                      <span class="i-mdi:help-circle h-4 w-4 inline-block" />
                      {{ t('settings.askEveryTime') }}
                    </span>
                  </SelectOption>
                </Select>
              </div>

              <!-- 实验性功能 -->
              <div class="setting-row">
                <div class="setting-label">
                  <span class="i-mdi:flask-outline text-primary h-5 w-5 inline-block" />
                  <div>
                    <div class="font-medium flex gap-2 items-center">
                      {{ t('settings.experimentalFeatures') }}
                      <Tag v-if="appSettings.experimentalFeatures" color="warning">
                        {{ t('settings.experimentalEnabled') }}
                      </Tag>
                    </div>
                    <div class="text-xs text-gray-500">
                      {{ t('settings.experimentalFeaturesDesc') }}
                    </div>
                  </div>
                </div>
                <Switch
                  v-model:checked="appSettings.experimentalFeatures" :loading="loading"
                  @change="updateSettings({ experimentalFeatures: appSettings.experimentalFeatures })"
                />
              </div>
            </div>
          </div>
        </Transition>

        <!-- 应用更新设置卡片 -->
        <Transition name="slide-fade" appear>
          <div class="p-5 rounded-lg bg-[var(--g-container-bg)]">
            <div class="mb-4 flex gap-2 items-center">
              <span class="i-mdi:cloud-download h-6 w-6 inline-block" />
              <h3 class="text-lg font-bold">
                {{ t('settings.appUpdate') }}
              </h3>
            </div>

            <div class="divide-[var(--el-border-color-lighter)] divide-y">
              <!-- 当前版本信息 -->
              <div class="setting-row">
                <div class="setting-label">
                  <span class="i-mdi:application text-primary h-5 w-5 inline-block" />
                  <div>
                    <div class="font-medium">
                      {{ t('settings.currentVersion') }}
                    </div>
                    <div class="text-xs text-gray-500">
                      {{ t('settings.currentVersionDesc') }}
                    </div>
                  </div>
                </div>
                <div class="flex gap-2 items-center">
                  <Tag color="processing" class="flex gap-1 items-center">
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
                  <span class="i-mdi:update text-primary h-5 w-5 inline-block" />
                  <div>
                    <div class="font-medium flex gap-2 items-center">
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
                  <span class="i-mdi:magnify text-primary h-5 w-5 inline-block" />
                  <div>
                    <div class="font-medium">
                      {{ t('settings.checkForUpdates') }}
                    </div>
                    <div class="text-xs text-gray-500">
                      {{ t('settings.manualCheckDesc') }}
                    </div>
                  </div>
                </div>
                <div class="flex gap-2 items-center">
                  <Button
                    type="primary"
                    :loading="updaterStore.updaterStatus.isChecking"
                    :disabled="updaterStore.updaterStatus.isDownloading"
                    class="flex gap-1 items-center"
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
                    class="flex gap-1 items-center"
                    @click="downloadUpdate"
                  >
                    <template #icon>
                      <span class="i-mdi:download h-4 w-4" />
                    </template>
                    {{ t('settings.downloadUpdate') }}
                  </Button>
                </div>
              </div>
            </div>

            <!-- 更新状态区域 -->
            <div v-if="(updaterStore.updateAvailable && updaterStore.latestVersion) || updaterStore.updaterStatus.isDownloading || updaterStore.updateDownloaded || updaterStore.updateError" class="mt-4 space-y-3">
              <div v-if="updaterStore.updateAvailable && updaterStore.latestVersion" class="p-3 rounded-md bg-[var(--el-color-primary-light-9)]">
                <div class="flex gap-2 items-start">
                  <span class="i-mdi:information text-primary mt-0.5 h-4 w-4 inline-block" />
                  <div class="text-sm">
                    <p class="font-medium">
                      {{ t('settings.newVersionAvailable', { version: updaterStore.latestVersion }) }}
                    </p>
                    <p class="text-xs text-gray-500 mt-1">
                      {{ appSettings.autoUpdate ? t('settings.downloadHint') : t('settings.downloadHintManual') }}
                    </p>
                  </div>
                </div>
              </div>

              <div v-if="updaterStore.updaterStatus.isDownloading" class="space-y-2">
                <div class="text-sm flex items-center justify-between">
                  <span class="font-medium">{{ t('settings.downloadProgress') }}</span>
                  <span>{{ t('settings.downloadSpeed') }}{{ (updaterStore.downloadSpeed / 1024 / 1024).toFixed(2) }} MB/s</span>
                </div>
                <Progress :percent="updaterStore.downloadProgress" status="active" />
              </div>

              <div v-if="updaterStore.updateDownloaded" class="p-3 rounded-md bg-[var(--el-color-success-light-9)]">
                <div class="flex gap-2 items-start">
                  <span class="i-mdi:check-circle text-[var(--el-color-success)] mt-0.5 h-4 w-4 inline-block" />
                  <div class="text-sm">
                    <p class="font-medium">
                      {{ t('settings.downloadComplete', { version: updaterStore.latestVersion }) }}
                    </p>
                    <p class="text-xs text-gray-500 mt-1">
                      {{ t('settings.downloadCompleteDesc') }}
                    </p>
                  </div>
                </div>
              </div>

              <div v-if="updaterStore.updateError" class="p-3 rounded-md bg-[var(--el-color-danger-light-9)]">
                <div class="flex gap-2 items-start">
                  <span class="i-mdi:alert-circle text-[var(--el-color-danger)] mt-0.5 h-4 w-4 inline-block" />
                  <div class="text-sm">
                    <p class="font-medium">
                      {{ t('settings.updateError') }}
                    </p>
                    <p class="text-xs text-gray-500 mt-1">
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

.theme-color-btn {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 32px;
  height: 32px;
  padding: 0;
  cursor: pointer;
  background-color: var(--btn-color);
  border: 2px solid transparent;
  border-radius: 50%;
  transition: all 0.2s ease;
}

.theme-color-btn:hover {
  box-shadow: 0 0 0 2px var(--btn-color);
  transform: scale(1.1);
}

.theme-color-btn-active {
  border-color: var(--btn-color);
  box-shadow: 0 0 0 2px var(--btn-color);
}

.slide-fade-enter-active {
  transition: all 0.4s ease;
}

.slide-fade-enter-from {
  opacity: 0;
  transform: translateY(-20px);
}
</style>
