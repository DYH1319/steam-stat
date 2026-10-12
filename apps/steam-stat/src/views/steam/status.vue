<script setup lang="ts">
import { Button, Empty, Spin, Tag } from 'ant-design-vue'
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
      steamStatus.value = await electronApi.steamRefreshStatus() ?? undefined
    }
    else {
      steamStatus.value = await electronApi.steamGetStatus() ?? undefined
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
    <FaPageMain class="mb-0">
      <div class="space-y-6">
        <!-- Steam 状态卡片 -->
        <Transition name="slide-fade" appear>
          <Spin :spinning="loading.status">
            <div class="p-6 rounded-lg bg-[var(--g-container-bg)] shadow-lg">
              <div class="mb-4 flex items-center justify-between">
                <h3 class="text-xl font-bold flex gap-2 items-center">
                  <span class="i-mdi:steam h-6 w-6 inline-block" />
                  {{ t('status.statusCard') }}
                </h3>
                <div class="flex gap-4 items-center">
                  <span v-if="lastRefreshTime.status" class="text-xs text-gray-500">
                    {{ t('common.lastRefresh') }}: {{ lastRefreshTime.status }}
                  </span>
                  <Button
                    type="primary"
                    :loading="loading.status"
                    class="flex gap-1 items-center"
                    @click="fetchSteamStatus(true)"
                  >
                    <template #icon>
                      <span class="i-mdi:refresh h-4 w-4" />
                    </template>
                    {{ t('common.refresh') }}
                  </Button>
                </div>
              </div>

              <div v-if="steamStatus" class="space-y-4">
                <!-- 状态标签 -->
                <div class="flex flex-wrap gap-2 items-center">
                  <Tag
                    :color="Number(steamStatus.steamPid) > 0 ? 'blue' : 'default'"
                    class="px-4 py-2 flex gap-2 items-center"
                  >
                    <span class="i-mdi:steam h-4 w-4" />
                    {{ Number(steamStatus.steamPid) > 0 ? t('status.steamRunning') : t('status.steamNotRunning') }}
                    <span v-if="Number(steamStatus.steamPid) > 0" class="text-xs opacity-80">PID: {{ steamStatus.steamPid }}</span>
                  </Tag>

                  <Tag
                    :color="steamStatus.activeUserSteamId ? 'orange' : 'default'"
                    class="px-4 py-2 flex gap-2 items-center"
                  >
                    <span class="i-mdi:account h-4 w-4" />
                    {{ steamStatus.activeUserSteamId ? t('status.userLoggedIn') : t('status.noUser') }}
                    <span v-if="steamStatus.activeUserSteamId" class="text-xs opacity-80">
                      Steam ID: {{ steamStatus.activeUserSteamId }}
                    </span>
                  </Tag>

                  <Tag
                    :color="Number(steamStatus.runningAppId) > 0 ? 'green' : 'default'"
                    class="px-4 py-2 flex gap-2 items-center"
                  >
                    <span class="i-mdi:gamepad h-4 w-4" />
                    {{ Number(steamStatus.runningAppId) > 0 ? t('status.appRunning') : t('status.noApp') }}
                    <span v-if="Number(steamStatus.runningAppId) > 0" class="text-xs opacity-80">
                      APP ID: {{ steamStatus.runningAppId }}
                    </span>
                  </Tag>

                  <Tag
                    v-if="steamStatus.refreshTime"
                    color="pink"
                    class="px-4 py-2 flex gap-2 items-center"
                  >
                    <span class="i-mdi:clock h-4 w-4" />
                    {{ t('common.dataUpdateTime') }}: {{ dayjs.unix(steamStatus.refreshTime).format('YYYY-MM-DD HH:mm:ss') }}
                  </Tag>
                </div>

                <!-- 路径信息 -->
                <div class="gap-4 grid grid-cols-1 lg:grid-cols-2">
                  <!-- 安装路径 -->
                  <Transition name="slide-fade">
                    <div v-if="steamStatus.steamPath" class="group p-4 border rounded-lg transition-all from-blue-50 to-blue-100 bg-gradient-to-br hover:shadow-md dark:from-blue-900/20 dark:to-blue-800/20">
                      <div class="mb-2 flex gap-2 items-center">
                        <span class="i-mdi:folder text-blue-600 h-6 w-6 inline-block dark:text-blue-400" />
                        <span class="font-size-4 text-gray-800 font-semibold dark:text-gray-200">{{ t('status.steamPath') }}</span>
                      </div>
                      <div class="flex gap-2 items-center">
                        <code class="text-sm px-3 py-2 rounded-lg bg-white/50 flex-1 dark:bg-black/20">{{ steamStatus.steamPath }}</code>
                        <Button type="text" class="flex gap-2 h-9 items-center" @click="copyToClipboard(steamStatus.steamPath)">
                          <span class="i-mdi:content-copy h-4 w-4" />
                        </Button>
                      </div>
                    </div>
                  </Transition>

                  <!-- 可执行文件 -->
                  <Transition name="slide-fade">
                    <div v-if="steamStatus.steamExePath" class="group p-4 border rounded-lg transition-all from-green-50 to-green-100 bg-gradient-to-br hover:shadow-md dark:from-green-900/20 dark:to-green-800/20">
                      <div class="mb-2 flex gap-2 items-center">
                        <span class="i-mdi:application text-green-600 h-6 w-6 inline-block dark:text-green-400" />
                        <span class="font-size-4 text-gray-800 font-semibold dark:text-gray-200">{{ t('status.steamExe') }}</span>
                      </div>
                      <div class="flex gap-2 items-center">
                        <code class="text-sm px-3 py-2 rounded-lg bg-white/50 flex-1 dark:bg-black/20">{{ steamStatus.steamExePath }}</code>
                        <Button type="text" class="flex gap-2 h-9 items-center" @click="copyToClipboard(steamStatus.steamExePath)">
                          <span class="i-mdi:content-copy h-4 w-4" />
                        </Button>
                      </div>
                    </div>
                  </Transition>

                  <!-- SteamClient DLL 32位 -->
                  <Transition name="slide-fade">
                    <div v-if="steamStatus.steamClientDllPath" class="group p-4 border rounded-lg transition-all from-purple-50 to-purple-100 bg-gradient-to-br hover:shadow-md dark:from-purple-900/20 dark:to-purple-800/20">
                      <div class="mb-2 flex gap-2 items-center">
                        <span class="i-mdi:file-code text-purple-600 h-6 w-6 inline-block dark:text-purple-400" />
                        <span class="font-size-4 text-gray-800 font-semibold dark:text-gray-200">{{ t('status.steamClientDll32') }}</span>
                      </div>
                      <div class="flex gap-2 items-center">
                        <code class="text-sm px-3 py-2 rounded-lg bg-white/50 flex-1 dark:bg-black/20">{{ steamStatus.steamClientDllPath }}</code>
                        <Button type="text" class="flex gap-2 h-9 items-center" @click="copyToClipboard(steamStatus.steamClientDllPath)">
                          <span class="i-mdi:content-copy h-4 w-4" />
                        </Button>
                      </div>
                    </div>
                  </Transition>

                  <!-- SteamClient DLL 64位 -->
                  <Transition name="slide-fade">
                    <div v-if="steamStatus.steamClientDll64Path" class="group p-4 border rounded-lg transition-all from-orange-50 to-orange-100 bg-gradient-to-br hover:shadow-md dark:from-orange-900/20 dark:to-orange-800/20">
                      <div class="mb-2 flex gap-2 items-center">
                        <span class="i-mdi:file-code text-orange-600 h-6 w-6 inline-block dark:text-orange-400" />
                        <span class="font-size-4 text-gray-800 font-semibold dark:text-gray-200">{{ t('status.steamClientDll64') }}</span>
                      </div>
                      <div class="flex gap-2 items-center">
                        <code class="text-sm px-3 py-2 rounded-lg bg-white/50 flex-1 dark:bg-black/20">{{ steamStatus.steamClientDll64Path }}</code>
                        <Button type="text" class="flex gap-2 h-9 items-center" @click="copyToClipboard(steamStatus.steamClientDll64Path)">
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
          <Spin :spinning="loading.folders">
            <div class="p-6 rounded-lg bg-[var(--g-container-bg)] shadow-lg">
              <div class="mb-4 flex items-center justify-between">
                <h3 class="text-xl font-bold flex gap-2 items-center">
                  <span class="i-mdi:folder-multiple h-6 w-6 inline-block" />
                  {{ t('status.libraryFolders') }}
                </h3>
                <div class="flex gap-4 items-center">
                  <span v-if="lastRefreshTime.folders" class="text-xs text-gray-500">
                    {{ t('common.lastRefresh') }}: {{ lastRefreshTime.folders }}
                  </span>
                  <Button
                    type="primary"
                    :loading="loading.folders"
                    class="flex gap-1 items-center"
                    @click="fetchLibraryFolders(true)"
                  >
                    <template #icon>
                      <span class="i-mdi:refresh h-4 w-4" />
                    </template>
                    {{ t('common.refresh') }}
                  </Button>
                </div>
              </div>

              <div v-if="libraryFolders.length > 0" class="space-y-4">
                <div class="flex flex-wrap gap-2 items-center">
                  <Tag
                    color="green"
                    class="px-4 py-2 flex gap-2 items-center"
                  >
                    <span class="i-mdi:folder-multiple h-4 w-4" />
                    {{ t('status.totalFolders', { count: libraryFolders.length }) }}
                  </Tag>
                </div>

                <TransitionGroup name="list" tag="div" class="space-y-4">
                  <div
                    v-for="(folder, idx) in libraryFolders"
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
