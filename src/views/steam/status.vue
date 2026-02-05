<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import { toast } from 'vue-sonner'
import { copyToClipboard } from '@/utils/clipboard'
import dayjs from '@/utils/dayjs.ts'

const { t } = useI18n()
const electronApi = (window as Window).electron

// electron api
const steamStatus = ref<GlobalStatus>()
const libraryFolders = ref<string[]>([])

const loading = ref<{ status: boolean, folders: boolean }>({ status: false, folders: false })
const lastRefreshTime = ref<{ status?: string, folders?: string }>({})

onMounted(() => {
  fetchSteamStatus(false)
  fetchLibraryFolders()
})

// 获取 Steam 状态
async function fetchSteamStatus(isRefresh: boolean) {
  loading.value.status = true
  try {
    if (isRefresh) {
      await new Promise(resolve => setTimeout(resolve, 200))
      steamStatus.value = await electronApi.steamRefreshStatus()
    }
    else {
      steamStatus.value = await electronApi.steamGetStatus()
    }
    lastRefreshTime.value.status = dayjs.unix(steamStatus.value?.refreshTime ?? 0).format('YYYY-MM-DD HH:mm:ss')
    if (isRefresh) {
      toast.success(t('status.getSuccess'))
    }
  }
  catch (e: any) {
    toast.error(`${t('common.getFailed')}: ${e?.message || e}`)
  }
  finally {
    loading.value.status = false
  }
}

// 获取 Steam 库目录
async function fetchLibraryFolders(isRefresh = false) {
  loading.value.folders = true
  try {
    if (isRefresh) {
      await new Promise(resolve => setTimeout(resolve, 200))
    }
    libraryFolders.value = await electronApi.steamGetLibraryFolders()
    lastRefreshTime.value.folders = dayjs().format('YYYY-MM-DD HH:mm:ss')
    if (isRefresh) {
      toast.success(t('status.getLibrarySuccess'))
    }
  }
  catch (e: any) {
    toast.error(`${t('common.getFailed')}: ${e?.message || e}`)
  }
  finally {
    loading.value.folders = false
  }
}
</script>

<template>
  <div>
    <FaPageMain>
      <div class="space-y-6">
        <!-- Steam 状态卡片 -->
        <Transition name="slide-fade" appear>
          <div v-loading="loading.status" class="rounded-lg bg-[var(--g-container-bg)] p-6 shadow-lg">
            <div class="mb-4 flex items-center justify-between">
              <h3 class="flex items-center gap-2 text-xl font-bold">
                <span class="i-mdi:steam inline-block h-6 w-6 text-primary" />
                {{ t('status.statusCard') }}
              </h3>
              <div class="flex items-center gap-4">
                <span v-if="lastRefreshTime.status" class="text-xs text-gray-500">
                  {{ t('common.lastRefresh') }}: {{ lastRefreshTime.status }}
                </span>
                <el-button
                  type="primary"
                  :loading="loading.status"
                  @click="fetchSteamStatus(true)"
                >
                  <span class="i-mdi:refresh mr-1 inline-block h-4 w-4" />
                  {{ t('common.refresh') }}
                </el-button>
              </div>
            </div>

            <div v-if="steamStatus" class="space-y-4">
              <!-- 状态标签 -->
              <div class="flex flex-wrap items-center gap-3">
                <el-tag
                  size="large"
                  :type="Number(steamStatus.steamPid) > 0 ? 'success' : 'info'"
                  effect="dark"
                  class="px-4 py-2"
                >
                  <span class="i-mdi:steam mr-1 inline-block h-4 w-4" />
                  {{ Number(steamStatus.steamPid) > 0 ? t('status.steamRunning') : t('status.steamNotRunning') }}
                  <span v-if="Number(steamStatus.steamPid) > 0" class="ml-2 text-xs opacity-80">PID: {{ steamStatus.steamPid }}</span>
                </el-tag>

                <el-tag
                  size="large"
                  :type="BigInt(steamStatus.activeUserSteamIdStr ?? 0) > 0 ? 'primary' : 'info'"
                  effect="dark"
                  class="px-4 py-2"
                >
                  <span class="i-mdi:account mr-1 inline-block h-4 w-4" />
                  {{ BigInt(steamStatus.activeUserSteamIdStr ?? 0) > 0 ? t('status.userLoggedIn') : t('status.noUser') }}
                  <span v-if="BigInt(steamStatus.activeUserSteamIdStr ?? 0) > 0" class="ml-2 text-xs opacity-80">
                    Steam ID: {{ steamStatus.activeUserSteamIdStr }}
                  </span>
                </el-tag>

                <el-tag
                  size="large"
                  :type="Number(steamStatus.runningAppId) > 0 ? 'warning' : 'info'"
                  effect="dark"
                  class="px-4 py-2"
                >
                  <span class="i-mdi:gamepad mr-1 inline-block h-4 w-4" />
                  {{ Number(steamStatus.runningAppId) > 0 ? t('status.appRunning') : t('status.noApp') }}
                  <span v-if="Number(steamStatus.runningAppId) > 0" class="ml-2 text-xs opacity-80">
                    APP ID: {{ steamStatus.runningAppId }}
                  </span>
                </el-tag>

                <el-tag v-if="steamStatus.refreshTime" size="large" type="info" effect="plain" class="px-4 py-2">
                  <span class="i-mdi:clock mr-1 inline-block h-4 w-4" />
                  {{ t('common.dataUpdateTime') }}: {{ dayjs.unix(steamStatus.refreshTime).format('YYYY-MM-DD HH:mm:ss') }}
                </el-tag>
              </div>

              <!-- 路径信息 -->
              <div class="grid grid-cols-1 gap-4 lg:grid-cols-2">
                <!-- 安装路径 -->
                <Transition name="slide-fade">
                  <div v-if="steamStatus.steamPath" class="group border rounded-lg from-blue-50 to-blue-100 bg-gradient-to-br p-4 transition-all dark:from-blue-900/20 dark:to-blue-800/20 hover:shadow-md">
                    <div class="mb-2 flex items-center gap-2">
                      <span class="i-mdi:folder inline-block h-6 w-6 text-blue-600 dark:text-blue-400" />
                      <span class="text-gray-800 font-semibold dark:text-gray-200">{{ t('status.steamPath') }}</span>
                    </div>
                    <div class="flex items-center gap-2">
                      <code class="flex-1 rounded bg-white/50 px-3 py-2 text-sm dark:bg-black/20">{{ steamStatus.steamPath }}</code>
                      <el-button text @click="copyToClipboard(steamStatus.steamPath)">
                        <span class="i-mdi:content-copy inline-block h-4 w-4" />
                      </el-button>
                    </div>
                  </div>
                </Transition>

                <!-- 可执行文件 -->
                <Transition name="slide-fade">
                  <div v-if="steamStatus.steamExePath" class="group border rounded-lg from-green-50 to-green-100 bg-gradient-to-br p-4 transition-all dark:from-green-900/20 dark:to-green-800/20 hover:shadow-md">
                    <div class="mb-2 flex items-center gap-2">
                      <span class="i-mdi:application inline-block h-6 w-6 text-green-600 dark:text-green-400" />
                      <span class="text-gray-800 font-semibold dark:text-gray-200">{{ t('status.steamExe') }}</span>
                    </div>
                    <div class="flex items-center gap-2">
                      <code class="flex-1 rounded bg-white/50 px-3 py-2 text-sm dark:bg-black/20">{{ steamStatus.steamExePath }}</code>
                      <el-button text @click="copyToClipboard(steamStatus.steamExePath)">
                        <span class="i-mdi:content-copy inline-block h-4 w-4" />
                      </el-button>
                    </div>
                  </div>
                </Transition>

                <!-- SteamClient DLL 32位 -->
                <Transition name="slide-fade">
                  <div v-if="steamStatus.steamClientDllPath" class="group border rounded-lg from-purple-50 to-purple-100 bg-gradient-to-br p-4 transition-all dark:from-purple-900/20 dark:to-purple-800/20 hover:shadow-md">
                    <div class="mb-2 flex items-center gap-2">
                      <span class="i-mdi:file-code inline-block h-6 w-6 text-purple-600 dark:text-purple-400" />
                      <span class="text-gray-800 font-semibold dark:text-gray-200">{{ t('status.steamClientDll32') }}</span>
                    </div>
                    <div class="flex items-center gap-2">
                      <code class="flex-1 rounded bg-white/50 px-3 py-2 text-sm dark:bg-black/20">{{ steamStatus.steamClientDllPath }}</code>
                      <el-button text @click="copyToClipboard(steamStatus.steamClientDllPath)">
                        <span class="i-mdi:content-copy inline-block h-4 w-4" />
                      </el-button>
                    </div>
                  </div>
                </Transition>

                <!-- SteamClient DLL 64位 -->
                <Transition name="slide-fade">
                  <div v-if="steamStatus.steamClientDll64Path" class="group border rounded-lg from-orange-50 to-orange-100 bg-gradient-to-br p-4 transition-all dark:from-orange-900/20 dark:to-orange-800/20 hover:shadow-md">
                    <div class="mb-2 flex items-center gap-2">
                      <span class="i-mdi:file-code inline-block h-6 w-6 text-orange-600 dark:text-orange-400" />
                      <span class="text-gray-800 font-semibold dark:text-gray-200">{{ t('status.steamClientDll64') }}</span>
                    </div>
                    <div class="flex items-center gap-2">
                      <code class="flex-1 rounded bg-white/50 px-3 py-2 text-sm dark:bg-black/20">{{ steamStatus.steamClientDll64Path }}</code>
                      <el-button text @click="copyToClipboard(steamStatus.steamClientDll64Path)">
                        <span class="i-mdi:content-copy inline-block h-4 w-4" />
                      </el-button>
                    </div>
                  </div>
                </Transition>
              </div>
            </div>

            <div v-else class="py-8">
              <el-empty :description="t('status.notDetected')" />
            </div>
          </div>
        </Transition>

        <!-- Steam 库目录卡片 -->
        <Transition name="slide-fade" appear>
          <div v-loading="loading.folders" class="rounded-lg bg-[var(--g-container-bg)] p-6 shadow-lg">
            <div class="mb-4 flex items-center justify-between">
              <h3 class="flex items-center gap-2 text-xl font-bold">
                <span class="i-mdi:folder-multiple inline-block h-6 w-6 text-primary" />
                {{ t('status.libraryFolders') }}
              </h3>
              <div class="flex items-center gap-4">
                <span v-if="lastRefreshTime.folders" class="text-xs text-gray-500">
                  {{ t('common.lastRefresh') }}: {{ lastRefreshTime.folders }}
                </span>
                <el-button
                  type="primary"
                  :loading="loading.folders"
                  @click="fetchLibraryFolders(true)"
                >
                  <span class="i-mdi:refresh mr-1 inline-block h-4 w-4" />
                  {{ t('common.refresh') }}
                </el-button>
              </div>
            </div>

            <div v-if="libraryFolders.length > 0" class="space-y-3">
              <el-tag size="large" type="success" effect="dark" class="mb-2 px-4 py-2">
                <span class="i-mdi:folder-multiple mr-1 inline-block h-4 w-4" />
                {{ t('status.totalFolders', { count: libraryFolders.length }) }}
              </el-tag>

              <TransitionGroup name="list" tag="div" class="space-y-2">
                <div
                  v-for="(folder, idx) in libraryFolders"
                  :key="folder"
                  class="group flex items-center gap-3 border rounded-lg from-indigo-50 to-purple-50 bg-gradient-to-r p-4 transition-all dark:from-indigo-900/20 dark:to-purple-900/20 hover:shadow-md"
                >
                  <span class="i-mdi:folder inline-block h-6 w-6 text-indigo-600 dark:text-indigo-400" />
                  <span class="text-gray-700 font-semibold dark:text-gray-300">库 {{ idx + 1 }}</span>
                  <code class="flex-1 rounded bg-white/50 px-3 py-2 text-sm dark:bg-black/20">{{ folder }}</code>
                  <el-button text @click="copyToClipboard(folder)">
                    <span class="i-mdi:content-copy inline-block h-4 w-4" />
                  </el-button>
                </div>
              </TransitionGroup>
            </div>

            <div v-else class="py-8">
              <el-empty :description="t('status.noLibraryFolders')" />
            </div>
          </div>
        </Transition>
      </div>
    </FaPageMain>
  </div>
</template>

<style scoped>
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
