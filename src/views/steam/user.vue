<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { toast } from 'vue-sonner'
import '@/assets/styles/steam-level.css'

const { t } = useI18n()
const electronApi = (window as any).electron

const loginUsers = ref<any[]>([])
const loading = ref(false)
const refreshButtonLoading = ref(false)
const globalStatus = ref<any>(null)

// 计算数据刷新时间
const dataRefreshTime = computed(() => {
  if (!globalStatus.value?.steamUserRefreshTime) {
    return null
  }
  return new Date(globalStatus.value.steamUserRefreshTime)
})

// 获取全局状态
async function fetchGlobalStatus() {
  try {
    globalStatus.value = await electronApi.steamGetStatus()
  }
  catch (e: any) {
    console.error('Failed to fetch global status:', e)
  }
}

// 获取登录用户
async function fetchLoginUsers(showToast = false) {
  loading.value = true
  refreshButtonLoading.value = true
  try {
    const users = await electronApi.steamGetLoginUser()
    // 按 timestamp 倒序排序
    loginUsers.value = users.sort((a: any, b: any) => {
      const timeA = a.timestamp ? a.timestamp.getTime() : 0
      const timeB = b.timestamp ? b.timestamp.getTime() : 0
      return timeB - timeA
    })
    await fetchGlobalStatus()
    if (showToast) {
      toast.success(t('user.getSuccess'))
    }
  }
  catch (e: any) {
    toast.error(`${t('common.getFailed')}: ${e?.message || e}`)
  }
  finally {
    loading.value = false
    refreshButtonLoading.value = false
  }
}

// 刷新登录用户
async function refreshLoginUsers(showToast = true) {
  // 强制等待一段时间才能刷新，避免被 steam 服务器认定恶意攻击
  const waitTimeSecond = 15
  if (new Date().getTime() - (dataRefreshTime.value?.getTime() ?? 0) < waitTimeSecond * 1000) {
    toast.error(`${t('user.waitForRefresh', { seconds: waitTimeSecond })}`)
    return
  }
  loading.value = true
  refreshButtonLoading.value = true
  // 清空当前用户列表
  loginUsers.value = []
  try {
    const users = await electronApi.steamRefreshLoginUser()
    // 按 timestamp 倒序排序
    loginUsers.value = users.sort((a: any, b: any) => {
      const timeA = a.timestamp ? a.timestamp.getTime() : 0
      const timeB = b.timestamp ? b.timestamp.getTime() : 0
      return timeB - timeA
    })
    await fetchGlobalStatus()
    if (showToast) {
      toast.success(t('user.refreshSuccess'))
    }
  }
  catch (e: any) {
    toast.error(`${t('common.refreshFailed')}: ${e?.message || e}`)
    refreshButtonLoading.value = false
  }
  finally {
    loading.value = false
  }
}

// 获取用户头像 URL（Base64）
function getUserAvatarUrl(user: any): string | null {
  // 优先使用动画头像
  if (user.animatedAvatar) {
    return `data:image/gif;base64,${user.animatedAvatar}`
  }
  // 其次使用全尺寸头像
  if (user.avatarFull) {
    return `data:image/jpeg;base64,${user.avatarFull}`
  }
  return null
}

// 获取用户头像边框 URL（Base64）
function getUserAvatarFrameUrl(user: any): string | null {
  if (user.avatarFrame) {
    return `data:image/png;base64,${user.avatarFrame}`
  }
  return null
}

// 监听用户更新事件
function onSteamUserUpdated() {
  fetchLoginUsers(false)
}

// 页面加载时自动获取数据
onMounted(() => {
  fetchLoginUsers()

  // 监听 Steam User 更新完成事件
  electronApi.onSteamUserUpdatedEvent(onSteamUserUpdated)
})

onUnmounted(() => {
  // 组件卸载时移除事件监听器
  electronApi.removeSteamUserUpdatedEventListener()
})
</script>

<template>
  <div>
    <FaPageMain>
      <Transition name="slide-fade" appear>
        <div class="rounded-lg bg-[var(--g-container-bg)] p-6 shadow-lg">
          <div class="mb-6 flex items-center justify-between">
            <div class="flex items-center gap-3">
              <span class="i-mdi:account-multiple inline-block h-8 w-8 text-primary" />
              <div>
                <h3 class="text-2xl font-bold">
                  {{ t('user.localLoginUsers') }}
                </h3>
                <p class="text-sm text-gray-500">
                  {{ t('user.subtitle') }}
                </p>
              </div>
              <el-tag size="large" type="success" effect="dark" class="ml-4 px-4 py-2">
                <span class="i-mdi:account-check mr-1 inline-block h-4 w-4" />
                {{ t('user.totalUsers', { count: loginUsers.length }) }}
              </el-tag>
            </div>
            <div class="flex items-center gap-4">
              <span v-if="dataRefreshTime" class="text-xs text-gray-500">
                {{ t('user.dataRefreshTime') }}: {{ dataRefreshTime.toLocaleString() }}
              </span>
              <el-button
                type="primary"
                :loading="refreshButtonLoading"
                size="default"
                @click="refreshLoginUsers()"
              >
                <span class="i-mdi:refresh mr-1 inline-block h-4 w-4" />
                {{ t('common.refreshData') }}
              </el-button>
            </div>
          </div>

          <div v-loading="loading">
            <template v-if="loginUsers && loginUsers.length > 0">
              <TransitionGroup name="list" tag="div" class="grid grid-cols-1 gap-6 lg:grid-cols-2 xl:grid-cols-3">
                <div
                  v-for="user in loginUsers"
                  :key="user.steamId"
                  class="group border rounded-xl from-white to-gray-50 bg-gradient-to-br p-6 shadow-md transition-all dark:from-gray-900 dark:to-gray-800 hover:shadow-xl hover:-translate-y-1"
                >
                  <!-- 用户头像和名称 -->
                  <div class="mb-4 flex items-center justify-between">
                    <div class="flex items-center gap-3">
                      <div class="relative h-16 min-h-16 min-w-16 w-16">
                        <!-- 头像 -->
                        <div class="h-full w-full flex items-center justify-center overflow-hidden rounded p-1.5">
                          <img
                            v-if="getUserAvatarUrl(user)"
                            :src="getUserAvatarUrl(user)!"
                            :alt="user.personaName || user.accountName"
                            class="h-full w-full object-cover"
                          >
                          <span v-else class="i-mdi:account-circle inline-block h-10 w-10 text-white" />
                        </div>
                        <!-- 头像边框 -->
                        <img
                          v-if="getUserAvatarFrameUrl(user)"
                          :src="getUserAvatarFrameUrl(user)!"
                          alt="avatar frame"
                          class="pointer-events-none absolute inset-0 h-full w-full object-cover"
                        >
                      </div>
                      <div class="flex-1">
                        <div class="flex items-center gap-2">
                          <div class="text-lg font-bold">
                            {{ user.personaName || user.accountName }}
                            <el-tooltip v-if="user.level !== undefined" :content="t('user.level', { level: user.level })">
                              <div class="friendPlayerLevel ml-1 font-normal" :class="user.levelClass || 'lvl_0'">
                                {{ user.level }}
                              </div>
                            </el-tooltip>
                          </div>
                        </div>
                        <div class="text-sm text-gray-600 dark:text-gray-400">
                          @{{ user.accountName }}
                        </div>
                      </div>
                    </div>
                    <div class="flex items-center gap-2">
                      <el-tooltip :content="t('user.rememberPassword')">
                        <el-tag v-if="user.rememberPassword" class="ml-1" type="success" effect="dark">
                          <span class="i-mdi:lock-check inline-block h-3 w-3" />
                        </el-tag>
                      </el-tooltip>
                    </div>
                  </div>

                  <!-- 用户详细信息 -->
                  <div class="space-y-3">
                    <div class="flex items-center gap-2 rounded-lg bg-blue-50 p-3 dark:bg-blue-900/20">
                      <span class="i-mdi:identifier inline-block h-5 w-5 text-blue-600 dark:text-blue-400" />
                      <div class="flex-1">
                        <div class="text-xs text-gray-500">
                          {{ t('user.steamId') }}
                        </div>
                        <code class="text-sm font-semibold font-mono">{{ user.steamId?.toString() }}</code>
                      </div>
                    </div>

                    <div class="flex items-center gap-2 rounded-lg bg-green-50 p-3 dark:bg-green-900/20">
                      <span class="i-mdi:account-key inline-block h-5 w-5 text-green-600 dark:text-green-400" />
                      <div class="flex-1">
                        <div class="text-xs text-gray-500">
                          {{ t('user.accountId') }}
                        </div>
                        <code class="text-sm font-semibold font-mono">{{ user.accountId }}</code>
                      </div>
                    </div>

                    <div class="flex items-center gap-2 rounded-lg bg-purple-50 p-3 dark:bg-purple-900/20">
                      <span class="i-mdi:clock-outline inline-block h-5 w-5 text-purple-600 dark:text-purple-400" />
                      <div class="flex-1">
                        <div class="text-xs text-gray-500">
                          {{ t('user.lastLoginTime') }}
                        </div>
                        <div class="text-sm font-semibold">
                          {{ user.timestamp ? user.timestamp.toLocaleString() : t('common.unknown') }}
                        </div>
                      </div>
                    </div>
                  </div>
                </div>
              </TransitionGroup>
            </template>

            <template v-else-if="!loading">
              <div class="py-12">
                <el-empty :description="t('user.noUsers')">
                  <template #image>
                    <span class="i-mdi:account-off inline-block h-20 w-20 text-gray-300" />
                  </template>
                </el-empty>
              </div>
            </template>

            <template v-else>
              <div class="py-12">
                <el-empty :description="t('common.loading')" />
              </div>
            </template>
          </div>
        </div>
      </Transition>
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
  transition: all 0.4s ease;
}

.list-enter-from {
  opacity: 0;
  transform: scale(0.9);
}

.list-move {
  transition: transform 0.4s ease;
}
</style>
