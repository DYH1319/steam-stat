<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { toast } from 'vue-sonner'
import { copyToClipboard } from '@/utils/clipboard'
import '@/assets/styles/steam-level.css'

const { t } = useI18n()
const electronApi = (window as any).electron

const loginUsers = ref<any[]>([])
const loading = ref(false)
const refreshButtonLoading = ref(false)
const globalStatus = ref<any>(null)

// 鼠标悬浮提示状态
const hoverTooltip = ref<{ show: boolean, x: number, y: number, name: string, cardId: string }>({
  show: false,
  x: 0,
  y: 0,
  name: '',
  cardId: '',
})
let hoverTimer: ReturnType<typeof setTimeout> | null = null
let currentHoverCardId: string = ''

// 右键菜单状态
const contextMenu = ref<{
  show: boolean
  x: number
  y: number
  user: any
}>({
  show: false,
  x: 0,
  y: 0,
  user: null,
})

// 菜单项数据结构
interface MenuItem {
  key: string
  icon: string
  label: string
  children?: MenuItem[]
  divider?: boolean
}

// 菜单配置
const menuItems = computed<MenuItem[]>(() => [
  {
    key: 'switchAccount',
    icon: 'i-mdi:account-switch',
    label: t('user.switchToThisAccount'),
  },
  {
    key: 'offlineMode',
    icon: 'i-mdi:lan-disconnect',
    label: t('user.startOfflineMode'),
  },
  {
    key: 'divider1',
    icon: '',
    label: '',
    divider: true,
  },
  {
    key: 'loginAs',
    icon: 'i-mdi:login',
    label: t('user.loginAs'),
    children: [
      { key: 'invisible', icon: '', label: t('user.invisible') },
      { key: 'offline', icon: '', label: t('user.offline') },
      { key: 'online', icon: '', label: t('user.online') },
      { key: 'busy', icon: '', label: t('user.busy') },
      { key: 'away', icon: '', label: t('user.away') },
      { key: 'snooze', icon: '', label: t('user.snooze') },
      { key: 'lookingToTrade', icon: '', label: t('user.lookingToTrade') },
      { key: 'lookingToPlay', icon: '', label: t('user.lookingToPlay') },
    ],
  },
  {
    key: 'divider2',
    icon: '',
    label: '',
    divider: true,
  },
  {
    key: 'openLink',
    icon: 'i-mdi:open-in-new',
    label: t('user.openLink'),
    children: [
      { key: 'steamProfile', icon: '', label: t('user.steamProfile') },
      { key: 'steamDB', icon: '', label: t('user.steamDB') },
    ],
  },
  {
    key: 'divider3',
    icon: '',
    label: '',
    divider: true,
  },
  {
    key: 'openUserdata',
    icon: 'i-mdi:folder-open',
    label: t('user.openUserdataFolder'),
  },
])

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

// 处理鼠标进入（开始计时显示悬浮提示）
function handleMouseEnter(user: any) {
  const cardId = user.steamId?.toString() || ''
  currentHoverCardId = cardId

  if (hoverTimer) {
    clearTimeout(hoverTimer)
  }

  hoverTimer = setTimeout(() => {
    if (currentHoverCardId === cardId) {
      hoverTooltip.value.show = true
      hoverTooltip.value.name = user.personaName || user.accountName
      hoverTooltip.value.cardId = cardId
    }
  }, 500)
}

// 处理鼠标移动（更新悬浮提示位置）
function handleMouseMove(event: MouseEvent) {
  // 实时更新提示位置（相对于视口）
  hoverTooltip.value.x = event.clientX + 10
  hoverTooltip.value.y = event.clientY + 20
}

// 处理鼠标离开（隐藏悬浮提示）
function handleMouseLeave() {
  if (hoverTimer) {
    clearTimeout(hoverTimer)
    hoverTimer = null
  }
  currentHoverCardId = ''
  hoverTooltip.value.show = false
  hoverTooltip.value.cardId = ''
}

// 处理右键菜单
function handleContextMenu(event: MouseEvent, user: any) {
  event.preventDefault()
  contextMenu.value = {
    show: true,
    x: event.clientX,
    y: event.clientY,
    user,
  }
}

// 处理更多选项按钮
function handleMoreOptions(event: MouseEvent, user: any) {
  const rect = (event.currentTarget as HTMLElement).getBoundingClientRect()
  contextMenu.value = {
    show: true,
    x: rect.left,
    y: rect.bottom + 5,
    user,
  }
}

// 关闭菜单
function closeContextMenu() {
  contextMenu.value.show = false
}

// 菜单项点击处理
function handleMenuAction(menuKey: string, parentKey?: string) {
  const user = contextMenu.value.user
  if (!user) {
    return
  }

  // TODO: 实现菜单功能
  const message = parentKey
    ? `${menuKey} (${parentKey}): ${user.personaName || user.accountName}`
    : `${menuKey}: ${user.personaName || user.accountName}`

  toast.info(message)
  closeContextMenu()
}

// 处理双击事件（切换到此账号）
function handleDoubleClick(event: MouseEvent, user: any) {
  event.stopPropagation()
  // TODO: 切换账号功能待实现
  toast.info(`${t('user.switchToThisAccount')}: ${user.personaName || user.accountName}`)
}

// 处理复制操作
async function handleCopy(event: MouseEvent, text: string) {
  event.stopPropagation()
  event.preventDefault()
  await copyToClipboard(text)
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
                  v-ripple="'rgba(0, 0, 0, 0.15)'"
                  class="group relative cursor-pointer select-none overflow-hidden border rounded-xl from-white to-gray-50 bg-gradient-to-br shadow-md transition-all dark:from-gray-900 dark:to-gray-800 hover:shadow-xl hover:-translate-y-1"
                  @contextmenu="handleContextMenu($event, user)"
                  @dblclick="handleDoubleClick($event, user)"
                  @mouseenter="handleMouseEnter(user)"
                  @mousemove="handleMouseMove($event)"
                  @mouseleave="handleMouseLeave"
                >
                  <div class="flex gap-6 p-6">
                    <!-- 左侧：头像区域 -->
                    <div class="flex flex-shrink-0 flex-col justify-between">
                      <div class="relative h-40 w-40">
                        <!-- 头像 -->
                        <div class="h-full w-full flex items-center justify-center overflow-hidden rounded">
                          <img
                            v-if="getUserAvatarUrl(user)"
                            :src="getUserAvatarUrl(user)!"
                            :alt="user.personaName || user.accountName"
                            class="h-32 w-32 object-cover"
                          >
                          <span v-else class="i-mdi:account-circle inline-block h-40 w-40 text-gray-400" />
                        </div>
                        <!-- 头像边框 -->
                        <img
                          v-if="getUserAvatarFrameUrl(user)"
                          :src="getUserAvatarFrameUrl(user)!"
                          alt="avatar frame"
                          class="pointer-events-none absolute inset-0 h-full w-full object-cover"
                        >
                      </div>
                      <div class="mt-4 flex flex-col items-center gap-2">
                        <!-- 最近登录标签 -->
                        <div v-if="user.mostRecent" class="flex items-center gap-1.5 text-xs text-red-600 dark:text-red-400">
                          <span class="i-mdi:star inline-block h-4 w-4" />
                          <span>{{ t('user.mostRecent') }}</span>
                        </div>
                        <!-- 记住密码 -->
                        <div v-if="user.rememberPassword" class="flex items-center gap-1.5 text-xs text-green-600 dark:text-green-400">
                          <span class="i-mdi:lock-check inline-block h-4 w-4" />
                          <span>{{ t('user.rememberPassword') }}</span>
                        </div>
                        <!-- 更多选项 -->
                        <el-button
                          class="flex items-center text-xs text-gray-600 dark:text-gray-400 hover:text-primary dark:hover:text-primary"
                          @click.stop="(e) => handleMoreOptions(e, user)"
                        >
                          <span class="i-mdi:dots-vertical mr-1 inline-block h-4 w-4" />
                          <span>{{ t('user.more') }}</span>
                        </el-button>
                      </div>
                    </div>

                    <!-- 右侧：用户信息区域 -->
                    <div class="min-w-0 flex-1">
                      <!-- 用户名称和操作按钮 -->
                      <div class="mb-4 flex items-start justify-between gap-4">
                        <div class="min-w-0 flex-1">
                          <div class="mb-1 flex items-center gap-2">
                            <h4 class="truncate text-xl font-bold">
                              {{ user.personaName || user.accountName }}
                            </h4>
                            <div v-if="user.level !== undefined" class="friendPlayerLevel font-normal" :class="user.levelClass || 'lvl_0'">
                              {{ user.level }}
                            </div>
                          </div>
                          <div class="text-sm text-gray-600 dark:text-gray-400">
                            @{{ user.accountName }}
                          </div>
                        </div>
                      </div>

                      <!-- 用户详细信息 -->
                      <div class="space-y-2">
                        <div class="flex items-center gap-2 rounded-lg bg-blue-50 p-2.5 dark:bg-blue-900/20">
                          <span class="i-mdi:identifier inline-block h-4 w-4 flex-shrink-0 text-blue-600 dark:text-blue-400" />
                          <div class="min-w-0 flex-1">
                            <div class="mb-0.5 text-xs text-gray-500">
                              {{ t('user.steamId') }}
                            </div>
                            <code class="block truncate text-xs font-semibold font-mono">{{ user.steamId?.toString() }}</code>
                          </div>
                          <el-button
                            text
                            @click="(e) => handleCopy(e, user.steamId?.toString() || '')"
                          >
                            <span class="i-mdi:content-copy inline-block h-3.5 w-3.5" />
                          </el-button>
                        </div>

                        <div class="flex items-center gap-2 rounded-lg bg-green-50 p-2.5 dark:bg-green-900/20">
                          <span class="i-mdi:account-key inline-block h-4 w-4 flex-shrink-0 text-green-600 dark:text-green-400" />
                          <div class="min-w-0 flex-1">
                            <div class="mb-0.5 text-xs text-gray-500">
                              {{ t('user.accountId') }}
                            </div>
                            <code class="block truncate text-xs font-semibold font-mono">{{ user.accountId }}</code>
                          </div>
                          <el-button
                            text
                            @click="(e) => handleCopy(e, user.accountId?.toString() || '')"
                          >
                            <span class="i-mdi:content-copy inline-block h-3.5 w-3.5" />
                          </el-button>
                        </div>

                        <div class="flex items-center gap-2 rounded-lg bg-purple-50 p-2.5 dark:bg-purple-900/20">
                          <span class="i-mdi:clock-outline inline-block h-4 w-4 flex-shrink-0 text-purple-600 dark:text-purple-400" />
                          <div class="min-w-0 flex-1">
                            <div class="mb-0.5 text-xs text-gray-500">
                              {{ t('user.lastLoginTime') }}
                            </div>
                            <div class="truncate text-xs font-semibold">
                              {{ user.timestamp ? user.timestamp.toLocaleString() : t('common.unknown') }}
                            </div>
                          </div>
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

    <!-- 悬浮提示 -->
    <Teleport to="body">
      <Transition name="fade">
        <div
          v-if="hoverTooltip.show"
          class="pointer-events-none fixed z-[10000] rounded-lg bg-gray-900 px-3 py-2 text-xs text-white shadow-lg dark:bg-gray-100 dark:text-gray-900"
          :style="{
            left: `${hoverTooltip.x}px`,
            top: `${hoverTooltip.y}px`,
          }"
        >
          {{ t('user.doubleClickToSwitch') }}: {{ hoverTooltip.name }}
        </div>
      </Transition>
    </Teleport>

    <!-- 右键菜单 -->
    <Teleport to="body">
      <Transition name="menu-fade">
        <div
          v-if="contextMenu.show"
          class="fixed inset-0 z-[9999]"
          @click="closeContextMenu"
          @contextmenu.prevent="closeContextMenu"
        >
          <div
            class="absolute min-w-52 overflow-hidden border border-gray-200 rounded-xl bg-white shadow-xl dark:border-gray-700 dark:bg-gray-900"
            :style="{
              left: `${contextMenu.x}px`,
              top: `${contextMenu.y}px`,
            }"
            @click.stop
          >
            <div class="py-1">
              <template v-for="item in menuItems" :key="item.key">
                <!-- 分隔线 -->
                <div v-if="item.divider" class="mx-2 my-1 h-px bg-gray-100 dark:bg-gray-800" />

                <!-- 有子菜单的项 -->
                <div v-else-if="item.children" class="group/menu relative">
                  <button class="w-full flex items-center gap-3 px-4 py-2 text-sm text-gray-700 transition-colors hover:bg-gray-50 dark:text-gray-300 dark:hover:bg-gray-800">
                    <span v-if="item.icon" class="h-4 w-4" :class="item.icon" />
                    <span class="flex-1">{{ item.label }}</span>
                    <span class="i-mdi:chevron-right h-4 w-4 text-gray-400" />
                  </button>
                  <div class="pointer-events-none absolute left-full top-0 ml-1 min-w-40 overflow-hidden border border-gray-200 rounded-lg bg-white opacity-0 shadow-xl transition-all group-hover/menu:pointer-events-auto dark:border-gray-700 dark:bg-gray-900 group-hover/menu:opacity-100">
                    <div class="py-1">
                      <button
                        v-for="child in item.children"
                        :key="child.key"
                        class="w-full px-4 py-2 text-left text-sm text-gray-700 transition-colors hover:bg-gray-50 dark:text-gray-300 dark:hover:bg-gray-800"
                        @click="handleMenuAction(child.key, item.key)"
                      >
                        {{ child.label }}
                      </button>
                    </div>
                  </div>
                </div>

                <!-- 普通菜单项 -->
                <button
                  v-else
                  class="w-full flex items-center gap-3 px-4 py-2 text-sm text-gray-700 transition-colors hover:bg-gray-50 dark:text-gray-300 dark:hover:bg-gray-800"
                  @click="handleMenuAction(item.key)"
                >
                  <span v-if="item.icon" class="h-4 w-4" :class="item.icon" />
                  <span class="flex-1">{{ item.label }}</span>
                </button>
              </template>
            </div>
          </div>
        </div>
      </Transition>
    </Teleport>
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

.fade-enter-active,
.fade-leave-active {
  transition: opacity 0.2s ease;
}

.fade-enter-from,
.fade-leave-to {
  opacity: 0;
}

.menu-fade-enter-active,
.menu-fade-leave-active {
  transition: all 0.2s cubic-bezier(0.4, 0, 0.2, 1);
}

.menu-fade-enter-from,
.menu-fade-leave-to {
  opacity: 0;
  transform: scale(0.95) translateY(-8px);
}
</style>
