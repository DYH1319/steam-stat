<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { toast } from 'vue-sonner'

const electronApi = (window as any).electron

const loginUsers = ref<any[]>([])
const loading = ref(false)
const lastRefreshTime = ref<Date | null>(null)

// 获取登录用户
async function fetchLoginUsers() {
  loading.value = true
  try {
    loginUsers.value = await electronApi.steamGetLoginUser()
    toast.success('获取登录用户信息成功', {
      duration: 700,
    })
  }
  catch (e: any) {
    toast.error(`获取失败: ${e?.message || e}`)
  }
  finally {
    loading.value = false
  }
}

// 刷新登录用户
async function refreshLoginUsers() {
  loading.value = true
  try {
    loginUsers.value = await electronApi.steamRefreshLoginUser()
    lastRefreshTime.value = new Date()
    toast.success('刷新登录用户信息成功', {
      duration: 700,
    })
  }
  catch (e: any) {
    toast.error(`刷新失败: ${e?.message || e}`)
  }
  finally {
    loading.value = false
  }
}

// 页面加载时自动获取数据
onMounted(() => {
  fetchLoginUsers()
})
</script>

<template>
  <div>
    <FaPageHeader title="Steam 用户信息" />
    <FaPageMain>
      <Transition name="slide-fade" appear>
        <div class="rounded-lg bg-[var(--g-container-bg)] p-6 shadow-lg">
          <div class="mb-6 flex items-center justify-between">
            <div class="flex items-center gap-3">
              <span class="i-mdi:account-multiple inline-block h-8 w-8 text-primary" />
              <div>
                <h3 class="text-2xl font-bold">
                  Steam 本机登录用户
                </h3>
                <p class="text-sm text-gray-500">
                  查看所有在本机登录过的 Steam 账户信息
                </p>
              </div>
              <el-tag size="large" type="success" effect="dark" class="ml-4 px-4 py-2">
                <span class="i-mdi:account-check mr-1 inline-block h-4 w-4" />
                共 {{ loginUsers.length }} 个已登录的用户
              </el-tag>
            </div>
            <div class="flex items-center gap-4">
              <span v-if="lastRefreshTime" class="text-xs text-gray-500">
                上次刷新: {{ lastRefreshTime.toLocaleTimeString() }}
              </span>
              <el-button
                type="primary"
                :loading="loading"
                size="default"
                @click="refreshLoginUsers"
              >
                <span class="i-mdi:refresh mr-1 inline-block h-4 w-4" />
                刷新数据
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
                      <div class="h-14 min-h-14 min-w-14 w-14 flex items-center justify-center rounded-full from-primary to-purple-500 bg-gradient-to-br shadow-lg">
                        <span class="i-mdi:account-circle inline-block h-8 w-8 text-white" />
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
                    <el-tag v-if="user.rememberPassword" type="success" effect="dark">
                      <span class="i-mdi:lock-check mr-1 inline-block h-3 w-3" />
                      已保存密码
                    </el-tag>
                  </div>

                  <!-- 用户详细信息 -->
                  <div class="space-y-3">
                    <div class="flex items-center gap-2 rounded-lg bg-blue-50 p-3 dark:bg-blue-900/20">
                      <span class="i-mdi:identifier inline-block h-5 w-5 text-blue-600 dark:text-blue-400" />
                      <div class="flex-1">
                        <div class="text-xs text-gray-500">
                          Steam ID
                        </div>
                        <code class="text-sm font-semibold font-mono">{{ user.steamId?.toString() }}</code>
                      </div>
                    </div>

                    <div class="flex items-center gap-2 rounded-lg bg-green-50 p-3 dark:bg-green-900/20">
                      <span class="i-mdi:account-key inline-block h-5 w-5 text-green-600 dark:text-green-400" />
                      <div class="flex-1">
                        <div class="text-xs text-gray-500">
                          Account ID
                        </div>
                        <code class="text-sm font-semibold font-mono">{{ user.accountId }}</code>
                      </div>
                    </div>

                    <div class="flex items-center gap-2 rounded-lg bg-purple-50 p-3 dark:bg-purple-900/20">
                      <span class="i-mdi:clock-outline inline-block h-5 w-5 text-purple-600 dark:text-purple-400" />
                      <div class="flex-1">
                        <div class="text-xs text-gray-500">
                          数据更新时间
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
                <el-empty description="暂无用户数据">
                  <template #image>
                    <span class="i-mdi:account-off inline-block h-20 w-20 text-gray-300" />
                  </template>
                </el-empty>
              </div>
            </template>

            <template v-else>
              <div class="py-12">
                <el-empty description="正在加载用户数据..." />
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
