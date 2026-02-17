<script setup lang="ts">
import type { Dayjs } from 'dayjs'
import { useI18n } from 'vue-i18n'
import { toast } from 'vue-sonner'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuSub,
  DropdownMenuSubContent,
  DropdownMenuSubTrigger,
  DropdownMenuTrigger,
} from '@/ui/components/FaDropdown/dropdown-menu'
import { encodeFileUrl } from '@/utils'
import { copyToClipboard } from '@/utils/clipboard'
import dayjs from '@/utils/dayjs.ts'
import '@/assets/styles/steam-level.css'

const { t } = useI18n()
const settingsStore = useSettingsStore()
const electronApi = (window as Window).electron

// electron api 原始数据
const globalStatus = ref<GlobalStatus | undefined>(undefined)
const loginUsers = ref<SteamUser[]>([])

// 加载中属性
const loading = ref<{ users: boolean, refreshButton: boolean }>({
  users: false,
  refreshButton: false,
})

// 计算属性 - 上次刷新时间
const dataRefreshTime = computed<Dayjs | undefined>(() => {
  if (!globalStatus.value?.steamUserRefreshTime) {
    return
  }
  return dayjs.unix(globalStatus.value.steamUserRefreshTime)
})

const rippleColor = computed(() => settingsStore.currentColorScheme === 'dark' ? 'rgba(255, 255, 255, 0.2)' : 'rgba(0, 0, 0, 0.2)')

onMounted(async () => {
  await fetchLoginUsers(false)

  electronApi.steamUserUpdatedOnListener(onSteamUserUpdated)
})

onBeforeUnmount(() => {
  electronApi.steamUserUpdatedRemoveListener()
})

// 监听用户更新事件
function onSteamUserUpdated() {
  fetchLoginUsers(false)
}

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
async function fetchLoginUsers(isRefresh: boolean) {
  await fetchGlobalStatus()
  await nextTick()

  // 强制等待一段时间才能刷新，避免被 steam 服务器认定恶意攻击
  const waitTimeSecond = 15
  if (isRefresh && dataRefreshTime.value && dayjs().diff(dataRefreshTime.value, 'second') < waitTimeSecond) {
    toast.error(`${t('user.waitForRefresh', { seconds: waitTimeSecond })}`)
    return
  }

  loading.value.users = true
  loading.value.refreshButton = true
  try {
    let users
    if (isRefresh) {
      users = await electronApi.steamRefreshLoginUser()
    }
    else {
      users = await electronApi.steamGetLoginUser()
    }
    // 按 timestamp 倒序排序
    loginUsers.value = users.sort((a, b) => {
      const timeA = a.timestamp ? a.timestamp : 0
      const timeB = b.timestamp ? b.timestamp : 0
      return timeB - timeA
    })
    if (isRefresh) {
      toast.success(t('user.getSuccess'))
    }
  }
  catch (e: any) {
    toast.error(`${t('common.getFailed')}: ${e?.message || e}`)
    loading.value.refreshButton = false
  }
  finally {
    loading.value.users = false
    if (!isRefresh) {
      loading.value.refreshButton = false
    }
  }
}

// 切换 Steam 用户
async function changeSteamUser(user: SteamUser, offlineMode?: boolean, personaState?: number) {
  user = toRaw(user)
  const res = await electronApi.steamChangeLoginUser({
    ...user,
    offlineMode,
    personaState,
  })
  if (res) {
    toast.success(`${t('user.switchToThisAccount')}: ${user.personaName} (${user.accountName})`)
    await fetchLoginUsers(false)
  }
}

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
  disabled?: boolean
}

// 菜单配置
const menuItems = computed<MenuItem[]>(() => [
  {
    key: 'accountName',
    icon: 'i-mdi:account',
    label: `${t('user.accountName')}: ${contextMenu.value.user.accountName}`,
    disabled: true,
  },
  {
    key: 'divider0',
    icon: '',
    label: '',
    divider: true,
  },
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
    key: 'loginAs',
    icon: 'i-mdi:login',
    label: t('user.loginAs'),
    children: [
      { key: '7', icon: '', label: t('user.invisible') },
      { key: '0', icon: '', label: t('user.offline') },
      { key: '1', icon: '', label: t('user.online') },
      { key: '2', icon: '', label: t('user.busy') },
      { key: '3', icon: '', label: t('user.away') },
      { key: '4', icon: '', label: t('user.snooze') },
      { key: '5', icon: '', label: t('user.lookingToTrade') },
      { key: '6', icon: '', label: t('user.lookingToPlay') },
    ],
  },
  {
    key: 'divider1',
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
    key: 'openUserdata',
    icon: 'i-mdi:folder-open',
    label: t('user.openUserdataFolder'),
  },
])

// 处理鼠标进入（开始计时显示悬浮提示）
function handleMouseEnter(_event: MouseEvent, user: any) {
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

// 处理菜单
function handleContextMenu(event: MouseEvent, user: any) {
  if (contextMenu.value.show) {
    closeContextMenu()
  }
  else {
    contextMenu.value = {
      show: true,
      x: event.clientX,
      y: event.clientY,
      user,
    }
  }
}

// 关闭菜单
function closeContextMenu() {
  contextMenu.value.show = false
}

// 菜单项点击处理
async function handleMenuAction(label: string, key: string, parentLabel?: string, parentKey?: string) {
  const user = contextMenu.value.user
  if (!user) {
    return
  }
  closeContextMenu()

  if (key === 'switchAccount') {
    await changeSteamUser(user, undefined, undefined)
  }
  else if (key === 'offlineMode') {
    await changeSteamUser(user, true, undefined)
  }
  else if (parentKey === 'loginAs') {
    await changeSteamUser(user, undefined, Number(key))
  }
  else if (parentKey === 'openLink') {
    // TODO
  }
  else if (key === 'openUserdata') {
    // TODO
  }

  const message = parentLabel
    ? `${parentLabel} - ${label}: ${user.personaName || user.accountName}`
    : `${label}: ${user.personaName || user.accountName}`
  toast.info(message)
}
</script>

<template>
  <div>
    <FaPageMain class="mb-0">
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
                {{ t('user.dataRefreshTime') }}: {{ dataRefreshTime.format('YYYY-MM-DD HH:mm:ss') }}
              </span>
              <el-button
                type="primary"
                :loading="loading.refreshButton"
                size="default"
                @click="fetchLoginUsers(true)"
              >
                <span class="i-mdi:refresh mr-1 inline-block h-4 w-4" />
                {{ t('common.refreshData') }}
              </el-button>
            </div>
          </div>

          <div v-loading="loading.users">
            <template v-if="loginUsers && loginUsers.length > 0">
              <TransitionGroup name="list" tag="div" class="grid grid-cols-[repeat(auto-fill,minmax(450px,1fr))] gap-6">
                <div
                  v-for="user in loginUsers"
                  :key="user.steamId"
                  v-ripple="rippleColor"
                  class="group relative overflow-hidden border rounded-xl bg-white shadow-md transition-all dark:bg-[#1c1c1c] hover:shadow-xl hover:-translate-y-1"
                  @contextmenu="handleContextMenu($event, user)"
                  @dblclick="changeSteamUser(user, undefined, undefined)"
                  @mouseenter="handleMouseEnter($event, user)"
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
                            v-if="user.animatedAvatar || user.avatarFull"
                            :src="encodeFileUrl(user.animatedAvatar || user.avatarFull)"
                            :alt="user.personaName || user.accountName"
                            :draggable="false"
                            class="h-32 w-32 object-cover"
                          >
                          <span v-else class="i-mdi:account-circle inline-block h-40 w-40 text-gray-400" />
                        </div>
                        <!-- 头像边框 -->
                        <img
                          v-if="user.avatarFrame"
                          :src="encodeFileUrl(user.avatarFrame)"
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
                          type="default"
                          @click="handleContextMenu($event, user)"
                          @mousedown.stop
                          @dblclick.stop
                          @mouseenter="handleMouseLeave"
                          @mouseleave="handleMouseEnter($event, user)"
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
                        <div class="flex items-center gap-2 rounded-lg bg-blue-100 p-2.5 dark:bg-blue-950">
                          <span class="i-mdi:identifier inline-block h-4 w-4 flex-shrink-0 text-blue-600 dark:text-blue-400" />
                          <div class="min-w-0 flex-1">
                            <div class="mb-0.5 text-xs text-gray-500">
                              {{ t('user.steamId') }}
                            </div>
                            <code class="block truncate text-xs font-semibold font-mono">{{ user.steamId }}</code>
                          </div>
                          <el-button
                            text
                            class="text-gray-600 dark:text-gray-400 hover:text-primary !hover:bg-blue-500 dark:hover:bg-gray-800 dark:hover:text-primary"
                            @mouseenter="handleMouseLeave"
                            @mouseleave="handleMouseEnter($event, user)"
                            @mousedown.stop
                            @dblclick.stop
                            @click="copyToClipboard(user.steamId || '')"
                          >
                            <span class="i-mdi:content-copy inline-block h-3.5 w-3.5" />
                          </el-button>
                        </div>

                        <div class="flex items-center gap-2 rounded-lg bg-green-100 p-2.5 dark:bg-green-950">
                          <span class="i-mdi:account-key inline-block h-4 w-4 flex-shrink-0 text-green-600 dark:text-green-400" />
                          <div class="min-w-0 flex-1">
                            <div class="mb-0.5 text-xs text-gray-500">
                              {{ t('user.accountId') }}
                            </div>
                            <code class="block truncate text-xs font-semibold font-mono">{{ user.accountId }}</code>
                          </div>
                          <el-button
                            text
                            class="text-gray-600 dark:text-gray-400 hover:text-primary !hover:bg-green-500 dark:hover:bg-gray-800 dark:hover:text-primary"
                            @mouseenter="handleMouseLeave"
                            @mouseleave="handleMouseEnter($event, user)"
                            @mousedown.stop
                            @dblclick.stop
                            @click="copyToClipboard(user.accountId?.toString() || '')"
                          >
                            <span class="i-mdi:content-copy inline-block h-3.5 w-3.5" />
                          </el-button>
                        </div>

                        <div class="flex items-center gap-2 rounded-lg bg-purple-100 p-2.5 dark:bg-purple-950">
                          <span class="i-mdi:clock-outline inline-block h-4 w-4 flex-shrink-0 text-purple-600 dark:text-purple-400" />
                          <div class="min-w-0 flex-1">
                            <div class="mb-0.5 text-xs text-gray-500">
                              {{ t('user.lastLoginTime') }}
                            </div>
                            <div class="truncate text-xs font-semibold">
                              {{ user.timestamp ? new Date(user.timestamp * 1000).toLocaleString() : t('common.unknown') }}
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
    <DropdownMenu :open="contextMenu.show" :modal="true">
      <DropdownMenuTrigger as-child>
        <div
          class="pointer-events-none fixed"
          :style="{
            left: `${contextMenu.x}px`,
            top: `${contextMenu.y}px`,
          }"
        />
      </DropdownMenuTrigger>
      <DropdownMenuContent
        class="min-w-52"
        align="start"
        side="bottom"
        :side-offset="0"
        :align-offset="0"
        :avoid-collisions="true"
        @interact-outside="closeContextMenu"
      >
        <template v-for="item in menuItems" :key="item.key">
          <!-- 分隔线 -->
          <DropdownMenuSeparator v-if="item.divider" />

          <!-- 有子菜单的项 -->
          <DropdownMenuSub v-else-if="item.children">
            <DropdownMenuSubTrigger>
              <FaIcon v-if="item.icon" :name="item.icon" class="mr-2 h-4 w-4" />
              <span>{{ item.label }}</span>
            </DropdownMenuSubTrigger>
            <DropdownMenuSubContent>
              <DropdownMenuItem
                v-for="child in item.children"
                :key="child.key"
                :disabled="child.disabled"
                @click="handleMenuAction(child.label, child.key, item.label, item.key)"
              >
                <FaIcon v-if="child.icon" :name="child.icon" class="mr-2 h-4 w-4" />
                <span>{{ child.label }}</span>
              </DropdownMenuItem>
            </DropdownMenuSubContent>
          </DropdownMenuSub>

          <!-- 普通菜单项 -->
          <DropdownMenuItem
            v-else
            :disabled="item.disabled"
            @click="handleMenuAction(item.label, item.key, undefined, undefined)"
          >
            <FaIcon v-if="item.icon" :name="item.icon" class="mr-2 h-4 w-4" />
            <span>{{ item.label }}</span>
          </DropdownMenuItem>
        </template>
      </DropdownMenuContent>
    </DropdownMenu>
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
</style>
