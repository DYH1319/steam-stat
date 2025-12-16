<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { toast } from 'vue-sonner'

const { t } = useI18n()
const electronApi = (window as any).electron

const loginUsers = ref<any[]>([])
const loading = ref(false)
const lastRefreshTime = ref<Date | null>(null)

// 获取登录用户
async function fetchLoginUsers(showToast = false) {
  loading.value = true
  try {
    loginUsers.value = await electronApi.steamGetLoginUser()
    if (showToast) {
      toast.success(t('user.getSuccess'))
    }
  }
  catch (e: any) {
    toast.error(`${t('common.getFailed')}: ${e?.message || e}`)
  }
  finally {
    loading.value = false
  }
}

// 刷新登录用户
async function refreshLoginUsers(showToast = true) {
  loading.value = true
  try {
    loginUsers.value = await electronApi.steamRefreshLoginUser()
    lastRefreshTime.value = new Date()
    if (showToast) {
      toast.success(t('user.refreshSuccess'))
    }
  }
  catch (e: any) {
    toast.error(`${t('common.refreshFailed')}: ${e?.message || e}`)
  }
  finally {
    loading.value = false
  }
}

// 获取用户头像 URL
function getUserAvatarUrl(avatar: string | null | undefined): string | null {
  if (!avatar) {
    return null
  }

  // 如果是 URL，直接返回
  if (avatar.startsWith('http://') || avatar.startsWith('https://')) {
    return avatar
  }

  // 如果是本地路径，使用自定义协议 steam-avatar://
  // 这样可以避免浏览器的安全限制
  const avatarUrl = `steam-avatar:///${avatar.replace(/\\/g, '/')}`
  return avatarUrl
}

// 页面加载时自动获取数据
onMounted(() => {
  fetchLoginUsers()
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
              <span v-if="lastRefreshTime" class="text-xs text-gray-500">
                {{ t('common.lastRefresh') }}: {{ lastRefreshTime.toLocaleTimeString() }}
              </span>
              <el-button
                type="primary"
                :loading="loading"
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
                      <div class="h-14 min-h-14 min-w-14 w-14 flex items-center justify-center overflow-hidden rounded-full from-primary to-purple-500 bg-gradient-to-br shadow-lg">
                        <img
                          v-if="getUserAvatarUrl(user.avatar)"
                          :src="getUserAvatarUrl(user.avatar)!"
                          :alt="user.personaName || user.accountName"
                          class="h-full w-full object-cover"
                        >
                        <span v-else class="i-mdi:account-circle inline-block h-8 w-8 text-white" />
                      </div>
                      <div>
                        <div class="text-lg font-bold">
                          {{ user.personaName || user.accountName }}
                        </div>
                        <div class="text-sm text-gray-600 dark:text-gray-400">
                          @{{ user.accountName }}
                        </div>
                      </div>
                    </div>
                    <el-tag v-if="user.rememberPassword" class="ml-1" type="success" effect="dark">
                      <span class="i-mdi:lock-check mr-1 inline-block h-3 w-3" />
                      {{ t('user.rememberPassword') }}
                    </el-tag>
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
                          {{ t('common.dataUpdateTime') }}
                        </div>
                        <div class="text-sm font-semibold">
                          {{ new Date(user.refreshTime).toLocaleString() }}
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
