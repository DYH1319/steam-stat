<script setup lang="ts">
import { Button, Drawer, Empty, Image, Popconfirm, Select, Spin, Tabs, Tag } from 'ant-design-vue'
import { useI18n } from 'vue-i18n'
import { toast } from 'vue-sonner'
import dayjs from '@/utils/dayjs.ts'
import '@/assets/styles/steam-level.css'

const { t } = useI18n()
const electronApi = (window as Window).electron

// 好友数据
const friendsData = ref<SteamFriendData[]>([])
const activeTab = ref<string>('')
const loading = ref(false)

// 选择模式（用户点击进入之后可以采用复选框选择好友）
const selectMode = ref(false)
const selectedSteamIds = ref<Set<string>>(new Set())

// 当前 Tab 下已被追踪的好友 Steam ID 集合（来自后端）
const trackedSteamIds = ref<Set<string>>(new Set())

// 记录查看抽屉
const recordsDrawerVisible = ref(false)
const recordsLoading = ref(false)
const records = ref<FriendStatusRecord[]>([])
const recordsFilterType = ref<'all' | 'state' | 'game' | 'personaName' | 'richPresence'>('all')
const recordsFilterFriend = ref<string>('all')
const recordsLimit = ref<number>(500)

// 计算属性 - 当前选中的用户数据
const currentUserData = computed(() => {
  return friendsData.value.find(d => d.accountName === activeTab.value)
})

// 计算属性 - 按状态排序的好友列表
const sortedFriends = computed(() => {
  if (!currentUserData.value?.friends) {
    return []
  }

  return [...currentUserData.value.friends].sort((a, b) => {
    // 先按是否在玩游戏排序
    const aPlaying = a.gameName ? 1 : 0
    const bPlaying = b.gameName ? 1 : 0
    if (bPlaying !== aPlaying) {
      return bPlaying - aPlaying
    }

    // 再按在线状态排序 (1=在线最高, 0=离线最低)
    if (a.personaState !== b.personaState) {
      if (a.personaState === 0) {
        return 1
      }
      if (b.personaState === 0) {
        return -1
      }
      return b.personaState - a.personaState
    }

    // 最后按名字排序
    return a.personaName.localeCompare(b.personaName)
  })
})

// 在线好友数量
const onlineFriendsCount = computed(() => {
  if (!currentUserData.value?.friends) {
    return 0
  }
  return currentUserData.value.friends.filter(f => f.personaState !== 0).length
})

// 在游戏中的好友数量
const inGameFriendsCount = computed(() => {
  if (!currentUserData.value?.friends) {
    return 0
  }
  return currentUserData.value.friends.filter(f => f.gameName).length
})

// 获取失败后延迟重试的定时器，需在卸载时清理，否则会在组件销毁后继续发起 IPC 调用
let retryTimer: ReturnType<typeof setTimeout> | null = null

onMounted(async () => {
  await fetchFriendsData()
  await refreshTrackedIds()
  electronApi.steamFriendsUpdateOnListener(onFriendsUpdate)
})

onBeforeUnmount(() => {
  electronApi.steamFriendsUpdateRemoveListener()
  if (retryTimer) {
    clearTimeout(retryTimer)
    retryTimer = null
  }
})

// 切换 Tab 时同步追踪列表
watch(activeTab, async () => {
  await refreshTrackedIds()
  // 切换 Tab 时清空选择
  selectedSteamIds.value.clear()
})

// 监听好友更新事件
function onFriendsUpdate(event: SteamFriendsUpdateEvent) {
  const index = friendsData.value.findIndex(d => d.accountName === event.accountName)
  if (index !== -1) {
    friendsData.value[index] = event.data
  }
  else {
    // 新登录（或重连）的账号，直接加入列表
    friendsData.value.push(event.data)
    if (!activeTab.value) {
      activeTab.value = event.accountName
    }
  }
}

// 刷新当前 Tab 账户的已追踪好友 ID
async function refreshTrackedIds() {
  if (!activeTab.value) {
    trackedSteamIds.value = new Set()
    return
  }
  try {
    const ids = await electronApi.steamFriendsTrackGet({ accountName: activeTab.value })
    trackedSteamIds.value = new Set(ids)
  }
  catch (e: any) {
    console.error('refreshTrackedIds failed:', e)
  }
}

// 切换选择模式
function toggleSelectMode() {
  selectMode.value = !selectMode.value
  if (!selectMode.value) {
    selectedSteamIds.value.clear()
  }
}

// 切换单个好友选择状态
function toggleFriendSelected(steamId: string) {
  if (selectedSteamIds.value.has(steamId)) {
    selectedSteamIds.value.delete(steamId)
  }
  else {
    selectedSteamIds.value.add(steamId)
  }
  selectedSteamIds.value = new Set(selectedSteamIds.value)
}

// 全选 / 清空选择
function toggleSelectAll() {
  const allIds = sortedFriends.value.map(f => f.steamId)
  if (selectedSteamIds.value.size === allIds.length) {
    selectedSteamIds.value = new Set()
  }
  else {
    selectedSteamIds.value = new Set(allIds)
  }
}

// 开始追踪选中的好友
async function startTrackingSelected() {
  if (!activeTab.value) {
    return
  }
  if (selectedSteamIds.value.size === 0) {
    toast.warning(t('friends.tracking.noSelected'))
    return
  }
  try {
    await electronApi.steamFriendsTrackStart({
      accountName: activeTab.value,
      friendSteamIds: Array.from(selectedSteamIds.value),
    })
    await refreshTrackedIds()
    toast.success(t('friends.tracking.startSuccess'))
    selectedSteamIds.value.clear()
    selectMode.value = false
  }
  catch (e: any) {
    toast.error(`${t('common.actionFailed')}: ${e?.message || e}`)
  }
}

// 停止追踪选中的好友
async function stopTrackingSelected() {
  if (!activeTab.value) {
    return
  }
  if (selectedSteamIds.value.size === 0) {
    toast.warning(t('friends.tracking.noSelected'))
    return
  }
  try {
    await electronApi.steamFriendsTrackStop({
      accountName: activeTab.value,
      friendSteamIds: Array.from(selectedSteamIds.value),
    })
    await refreshTrackedIds()
    toast.success(t('friends.tracking.stopSuccess'))
    selectedSteamIds.value.clear()
    selectMode.value = false
  }
  catch (e: any) {
    toast.error(`${t('common.actionFailed')}: ${e?.message || e}`)
  }
}

// 切换单个好友的追踪状态（非选择模式下使用）
async function toggleFriendTracking(steamId: string) {
  if (!activeTab.value) {
    return
  }
  const isTracking = trackedSteamIds.value.has(steamId)
  try {
    if (isTracking) {
      await electronApi.steamFriendsTrackStop({
        accountName: activeTab.value,
        friendSteamIds: [steamId],
      })
    }
    else {
      await electronApi.steamFriendsTrackStart({
        accountName: activeTab.value,
        friendSteamIds: [steamId],
      })
    }
    await refreshTrackedIds()
  }
  catch (e: any) {
    toast.error(`${t('common.actionFailed')}: ${e?.message || e}`)
  }
}

// 打开记录抽屉并加载
async function openRecordsDrawer() {
  recordsDrawerVisible.value = true
  await fetchRecords()
}

// 加载记录
async function fetchRecords() {
  recordsLoading.value = true
  try {
    const param: {
      accountName?: string
      friendSteamId?: string
      changeType?: string
      limit?: number
    } = {
      limit: recordsLimit.value,
    }
    if (activeTab.value) {
      param.accountName = activeTab.value
    }
    if (recordsFilterFriend.value !== 'all') {
      param.friendSteamId = recordsFilterFriend.value
    }
    if (recordsFilterType.value !== 'all') {
      param.changeType = recordsFilterType.value
    }
    records.value = await electronApi.steamFriendsRecordsGet(param)
  }
  catch (e: any) {
    toast.error(`${t('common.getFailed')}: ${e?.message || e}`)
  }
  finally {
    recordsLoading.value = false
  }
}

// 清空所有记录
async function clearAllRecords() {
  try {
    const param: { accountName?: string } = {}
    if (activeTab.value) {
      param.accountName = activeTab.value
    }
    const count = await electronApi.steamFriendsRecordsClear(param)
    toast.success(t('friends.records.clearSuccess', { count }))
    await fetchRecords()
  }
  catch (e: any) {
    toast.error(`${t('common.actionFailed')}: ${e?.message || e}`)
  }
}

// 根据类型、旧值、新值形成可读描述
function formatRecordDescription(record: FriendStatusRecord): string {
  const name = record.friendPersonaName
  const prev = record.previousValue ? JSON.parse(record.previousValue) : {}
  const curr = record.currentValue ? JSON.parse(record.currentValue) : {}

  if (record.changeType === 'state') {
    return t('friends.records.stateChanged', {
      name,
      from: getPersonaStateText(prev.personaState ?? 0),
      to: getPersonaStateText(curr.personaState ?? 0),
    })
  }
  if (record.changeType === 'game') {
    const prevGame = prev.gameName as string
    const currGame = curr.gameName as string
    if (!prevGame && currGame) {
      return t('friends.records.gameStarted', { name, game: currGame })
    }
    if (prevGame && !currGame) {
      return t('friends.records.gameStopped', { name, game: prevGame })
    }
    return t('friends.records.gameSwitched', {
      name,
      from: prevGame || t('friends.records.typeState'),
      to: currGame || t('steamLibrary.noGame'),
    })
  }
  if (record.changeType === 'personaName') {
    return t('friends.records.personaNameChanged', {
      name,
      from: prev.personaName ?? '',
      to: curr.personaName ?? '',
    })
  }
  if (record.changeType === 'richPresence') {
    const currRichPresence = curr.richPresence as string
    if (!currRichPresence) {
      return t('friends.records.richPresenceCleared', { name })
    }
    return t('friends.records.richPresenceChanged', { name, to: currRichPresence })
  }
  return `${record.changeType}: ${record.previousValue || ''} -> ${record.currentValue || ''}`
}

function getChangeTypeTagColor(type: string): string {
  switch (type) {
    case 'state': return 'blue'
    case 'game': return 'green'
    case 'personaName': return 'orange'
    case 'richPresence': return 'purple'
    default: return 'default'
  }
}

function getChangeTypeLabel(type: string): string {
  switch (type) {
    case 'state': return t('friends.records.typeState')
    case 'game': return t('friends.records.typeGame')
    case 'personaName': return t('friends.records.typePersonaName')
    case 'richPresence': return t('friends.records.typeRichPresence')
    default: return type
  }
}

function formatRecordTime(timestamp: number): string {
  return dayjs.unix(timestamp).format('YYYY-MM-DD HH:mm:ss')
}

// 获取好友数据（失败时自动重试一次）
async function fetchFriendsData(isRetry = false) {
  loading.value = true
  try {
    const data = await electronApi.steamFriendsGetAll()
    friendsData.value = data

    // 设置默认选中的 Tab
    if (data.length > 0 && !activeTab.value) {
      activeTab.value = data[0].accountName
    }
  }
  catch (e: any) {
    toast.error(`${t('common.getFailed')}: ${e?.message || e}`)
    if (!isRetry) {
      // 网络波动等场景下 5 秒后自动重试一次
      if (retryTimer) {
        clearTimeout(retryTimer)
      }
      retryTimer = setTimeout(() => {
        retryTimer = null
        if (friendsData.value.length === 0) {
          fetchFriendsData(true)
        }
      }, 5000)
    }
  }
  finally {
    loading.value = false
  }
}

// 刷新好友数据
async function refreshFriendsData() {
  loading.value = true
  try {
    const data = await electronApi.steamFriendsGetAll()
    friendsData.value = data
    toast.success(t('friends.refreshSuccess'))
  }
  catch (e: any) {
    toast.error(`${t('common.getFailed')}: ${e?.message || e}`)
  }
  finally {
    loading.value = false
  }
}

// Steam 默认头像哈希（黑底白色问号）
const DEFAULT_AVATAR_HASH = 'fef49e7fa7e1997310d705b2a6158ff8dc1cdfeb'

// 获取头像URL
function getAvatarUrl(avatarHash: string, size: 'small' | 'medium' | 'full' = 'medium'): string {
  const hash = (!avatarHash || avatarHash === '0000000000000000000000000000000000000000')
    ? DEFAULT_AVATAR_HASH
    : avatarHash
  const sizeMap = {
    small: '',
    medium: '_medium',
    full: '_full',
  }
  return `https://avatars.fastly.steamstatic.com/${hash}${sizeMap[size]}.jpg`
}

// 获取状态文本
function getPersonaStateText(state: number): string {
  const stateMap: Record<number, string> = {
    0: t('friends.personaState.offline'),
    1: t('friends.personaState.online'),
    2: t('friends.personaState.busy'),
    3: t('friends.personaState.away'),
    4: t('friends.personaState.snooze'),
    5: t('friends.personaState.lookingToTrade'),
    6: t('friends.personaState.lookingToPlay'),
    7: t('friends.personaState.invisible'),
  }
  return stateMap[state] || t('common.unknown')
}

// 获取状态颜色
function getPersonaStateColor(state: number, gameName?: string): string {
  if (gameName) {
    return '#90ba3c' // 绿色 - 在游戏中
  }
  const colorMap: Record<number, string> = {
    0: '#898989', // 灰色 - 离线
    1: '#16c04a', // 绿色 - 在线
    2: '#e11d48', // 红色 - 忙碌
    3: '#57cbde', // 蓝色 - 离开
    4: '#57cbde', // 蓝色 - 打盹
    5: '#57cbde', // 蓝色 - 想交易
    6: '#57cbde', // 蓝色 - 想玩游戏
  }
  return colorMap[state] || '#898989'
}

// 获取状态背景色
function getPersonaStateBgClass(state: number, gameName?: string): string {
  if (gameName) {
    return 'border-green-500/30 bg-green-500/10'
  }
  if (state === 0) {
    return 'border-gray-500/30 bg-gray-500/10'
  }
  return 'border-blue-500/30 bg-blue-500/10'
}

// 打开 Steam 个人资料
function openSteamProfile(steamId: string) {
  electronApi.shellOpenExternal(`https://steamcommunity.com/profiles/${steamId}`)
}

// 格式化最后更新时间
function formatLastUpdate(timestamp: number): string {
  return dayjs.unix(timestamp).format('YYYY-MM-DD HH:mm:ss')
}

// 格式化 Unix 时间戳为相对时间（如 "5 分钟前"），超过 7 天显示具体日期
function formatRelativeTime(timestamp: number): string {
  if (!timestamp || timestamp <= 0) {
    return ''
  }

  const now = dayjs()
  const time = dayjs.unix(timestamp)
  const diffMinutes = now.diff(time, 'minute')
  const diffHours = now.diff(time, 'hour')
  const diffDays = now.diff(time, 'day')

  if (diffMinutes < 1) {
    return t('friends.lastOnline.justNow')
  }
  if (diffMinutes < 60) {
    return t('friends.lastOnline.minutesAgo', { count: diffMinutes })
  }
  if (diffHours < 24) {
    return t('friends.lastOnline.hoursAgo', { count: diffHours })
  }
  if (diffDays < 7) {
    return t('friends.lastOnline.daysAgo', { count: diffDays })
  }

  return time.format('YYYY-MM-DD HH:mm')
}

// 计算 Steam 等级徽章样式类（与 steam-level.css 中的 lvl_* / lvl_plus_* 类对应）
function getLevelClass(level?: number | null): string {
  if (level == null || level < 0) {
    return 'lvl_0'
  }
  if (level < 100) {
    return `lvl_${Math.floor(level / 10) * 10}`
  }
  const hundreds = Math.floor(level / 100) * 100
  const plus = Math.floor((level % 100) / 10) * 10
  return plus > 0 ? `lvl_${hundreds} lvl_plus_${plus}` : `lvl_${hundreds}`
}
</script>

<template>
  <div>
    <FaPageMain class="mb-0">
      <Transition name="slide-fade" appear>
        <div class="rounded-lg bg-[var(--g-container-bg)] p-6 shadow-lg">
          <!-- 标题栏 -->
          <div class="mb-6 flex items-center justify-between">
            <div class="flex items-center gap-3">
              <span class="i-mdi:account-group inline-block h-8 w-8" />
              <div>
                <h3 class="text-2xl font-bold">
                  {{ t('friends.title') }}
                </h3>
                <p class="text-sm text-gray-500">
                  {{ t('friends.subtitle') }}
                </p>
              </div>
            </div>
            <div class="flex flex-wrap items-center gap-2">
              <!-- 已追踪数量提示 -->
              <Tag v-if="trackedSteamIds.size > 0" color="purple" class="flex items-center gap-1">
                <span class="i-mdi:radar h-3.5 w-3.5" />
                {{ t('friends.tracking.trackingCount', { count: trackedSteamIds.size }) }}
              </Tag>

              <!-- 选择模式切换 -->
              <Button
                :type="selectMode ? 'primary' : 'default'"
                class="flex items-center gap-1"
                @click="toggleSelectMode"
              >
                <template #icon>
                  <span class="i-mdi:checkbox-multiple-marked-outline h-4 w-4" />
                </template>
                {{ selectMode ? t('friends.tracking.exitSelect') : t('friends.tracking.enterSelect') }}
              </Button>

              <!-- 选择模式下的操作按钮 -->
              <template v-if="selectMode">
                <Button class="flex items-center gap-1" @click="toggleSelectAll">
                  <template #icon>
                    <span class="i-mdi:select-all h-4 w-4" />
                  </template>
                  {{ selectedSteamIds.size === sortedFriends.length && sortedFriends.length > 0
                    ? t('friends.tracking.unselectAll')
                    : t('friends.tracking.selectAll') }}
                </Button>
                <Tag v-if="selectedSteamIds.size > 0" color="blue">
                  {{ t('friends.tracking.trackedCount', { count: selectedSteamIds.size }) }}
                </Tag>
                <Button
                  type="primary"
                  :disabled="selectedSteamIds.size === 0"
                  class="flex items-center gap-1"
                  @click="startTrackingSelected"
                >
                  <template #icon>
                    <span class="i-mdi:play-circle-outline h-4 w-4" />
                  </template>
                  {{ t('friends.tracking.startTracking') }}
                </Button>
                <Button
                  danger
                  :disabled="selectedSteamIds.size === 0"
                  class="flex items-center gap-1"
                  @click="stopTrackingSelected"
                >
                  <template #icon>
                    <span class="i-mdi:stop-circle-outline h-4 w-4" />
                  </template>
                  {{ t('friends.tracking.stopTracking') }}
                </Button>
              </template>

              <!-- 打开记录 -->
              <Button class="flex items-center gap-1" @click="openRecordsDrawer">
                <template #icon>
                  <span class="i-mdi:history h-4 w-4" />
                </template>
                {{ t('friends.records.open') }}
              </Button>

              <!-- 刷新 -->
              <Button
                type="primary"
                :loading="loading"
                class="flex items-center gap-1"
                @click="refreshFriendsData"
              >
                <template #icon>
                  <span class="i-mdi:refresh h-4 w-4" />
                </template>
                {{ t('common.refresh') }}
              </Button>
            </div>
          </div>

          <!-- 无登录用户提示 -->
          <template v-if="friendsData.length === 0 && !loading">
            <div class="py-12">
              <Empty :description="t('friends.noLoggedInUsers')">
                <template #image>
                  <span class="i-mdi:account-off inline-block h-20 w-20 text-gray-300" />
                </template>
              </Empty>
            </div>
          </template>

          <!-- Tab 面板 -->
          <template v-else>
            <Spin :spinning="loading">
              <Tabs v-model:active-key="activeTab" type="card" class="steam-friends-tabs">
                <Tabs.TabPane
                  v-for="userData in friendsData"
                  :key="userData.accountName"
                  :tab="userData.currentUser.personaName || userData.accountName"
                >
                  <!-- 当前用户信息卡片 -->
                  <div class="mb-6 overflow-hidden border rounded-xl from-blue-500/5 to-purple-500/5 bg-gradient-to-r p-6 dark:from-blue-500/10 dark:to-purple-500/10">
                    <div class="flex items-center gap-6">
                      <!-- 头像 -->
                      <div class="relative h-24 w-24 flex-shrink-0">
                        <div class="h-full w-full overflow-hidden rounded-lg">
                          <Image
                            :src="getAvatarUrl(userData.currentUser.avatarHash, 'full')"
                            :alt="userData.currentUser.personaName"
                            :width="96"
                            :height="96"
                            :preview="false"
                            class="h-full w-full object-cover"
                          />
                        </div>
                        <!-- 状态指示器 -->
                        <div
                          class="absolute h-6 w-6 border-3 border-white rounded-full -bottom-1 -right-1 dark:border-gray-800"
                          :style="{ backgroundColor: getPersonaStateColor(userData.currentUser.personaState, userData.currentUser.gameName) }"
                        />
                      </div>

                      <!-- 用户信息 -->
                      <div class="min-w-0 flex-1">
                        <div class="mb-2 flex items-center gap-3">
                          <h4 class="truncate text-2xl font-bold">
                            {{ userData.currentUser.personaName }}
                          </h4>
                          <!-- Steam 等级徽章 -->
                          <div
                            v-if="userData.currentUser.level != null"
                            class="friendPlayerLevel flex-shrink-0 font-normal"
                            :class="getLevelClass(userData.currentUser.level)"
                            :title="t('friends.level', { level: userData.currentUser.level })"
                          >
                            {{ userData.currentUser.level }}
                          </div>
                          <Tag
                            :color="userData.currentUser.gameName ? 'green' : (userData.currentUser.personaState === 0 ? 'default' : 'blue')"
                          >
                            {{ userData.currentUser.gameName || getPersonaStateText(userData.currentUser.personaState) }}
                          </Tag>
                        </div>
                        <div class="text-sm text-gray-500">
                          @{{ userData.accountName }}
                        </div>
                        <div v-if="userData.currentUser.gameName" class="mt-2 flex items-center gap-2 text-sm text-green-600 dark:text-green-400">
                          <span class="i-mdi:gamepad-variant h-4 w-4" />
                          {{ t('friends.playing') }}: {{ userData.currentUser.gameName }}
                        </div>
                      </div>

                      <!-- 统计信息 -->
                      <div class="flex gap-6">
                        <div class="text-center">
                          <div class="text-3xl text-primary font-bold">
                            {{ userData.friends.length }}
                          </div>
                          <div class="text-sm text-gray-500">
                            {{ t('friends.totalFriends') }}
                          </div>
                        </div>
                        <div class="text-center">
                          <div class="text-3xl text-blue-500 font-bold">
                            {{ onlineFriendsCount }}
                          </div>
                          <div class="text-sm text-gray-500">
                            {{ t('friends.onlineFriends') }}
                          </div>
                        </div>
                        <div class="text-center">
                          <div class="text-3xl text-green-500 font-bold">
                            {{ inGameFriendsCount }}
                          </div>
                          <div class="text-sm text-gray-500">
                            {{ t('friends.inGameFriends') }}
                          </div>
                        </div>
                      </div>
                    </div>

                    <!-- 最后更新时间 -->
                    <div class="mt-4 text-right text-xs text-gray-500">
                      {{ t('common.lastRefresh') }}: {{ formatLastUpdate(userData.lastUpdateTime) }}
                    </div>
                  </div>

                  <!-- 好友列表 -->
                  <div class="grid grid-cols-[repeat(auto-fill,minmax(320px,1fr))] gap-4">
                    <TransitionGroup name="list">
                      <div
                        v-for="friend in sortedFriends"
                        :key="friend.steamId"
                        class="friend-card group relative cursor-pointer overflow-hidden border rounded-xl p-4 transition-all hover:shadow-lg hover:-translate-y-0.5"
                        :class="[
                          getPersonaStateBgClass(friend.personaState, friend.gameName),
                          selectMode && selectedSteamIds.has(friend.steamId) ? 'ring-2 ring-primary' : '',
                        ]"
                        @click="selectMode ? toggleFriendSelected(friend.steamId) : openSteamProfile(friend.steamId)"
                      >
                        <!-- 选择模式：左上角复选框 -->
                        <div
                          v-if="selectMode"
                          class="absolute left-2 top-2 h-5 w-5 flex items-center justify-center border-2 rounded bg-white dark:bg-gray-800"
                          :class="selectedSteamIds.has(friend.steamId) ? 'border-primary bg-primary text-white' : 'border-gray-400'"
                        >
                          <span v-if="selectedSteamIds.has(friend.steamId)" class="i-mdi:check h-3.5 w-3.5 text-white" />
                        </div>

                        <!-- 追踪状态徽标（非选择模式下显示可点击的雷达图标） -->
                        <button
                          v-if="!selectMode"
                          class="absolute right-2 top-2 h-7 w-7 flex items-center justify-center rounded-full transition-all"
                          :class="trackedSteamIds.has(friend.steamId)
                            ? 'bg-purple-500 text-white shadow-md'
                            : 'bg-gray-200 text-gray-400 opacity-0 group-hover:opacity-100 hover:bg-purple-100 hover:text-purple-600 dark:bg-gray-700'"
                          :title="trackedSteamIds.has(friend.steamId)
                            ? t('friends.tracking.untrackTooltip')
                            : t('friends.tracking.trackTooltip')"
                          @click.stop="toggleFriendTracking(friend.steamId)"
                        >
                          <span class="i-mdi:radar h-4 w-4" />
                        </button>

                        <div class="flex items-center gap-4">
                          <!-- 头像 -->
                          <div class="relative h-14 w-14 flex-shrink-0">
                            <div class="h-full w-full overflow-hidden rounded-lg">
                              <Image
                                :src="getAvatarUrl(friend.avatarHash, 'medium')"
                                :alt="friend.personaName"
                                :width="56"
                                :height="56"
                                :preview="false"
                                class="h-full w-full object-cover"
                              />
                            </div>
                            <!-- 状态指示器 -->
                            <div
                              class="absolute h-4 w-4 border-2 border-white rounded-full -bottom-0.5 -right-0.5 dark:border-gray-800"
                              :style="{ backgroundColor: getPersonaStateColor(friend.personaState, friend.gameName) }"
                            />
                          </div>

                          <!-- 好友信息 -->
                          <div class="min-w-0 flex-1">
                            <div class="mb-1 flex items-center gap-2">
                              <span class="truncate font-semibold">
                                {{ friend.personaName }}
                              </span>
                              <!-- Steam 等级徽章 -->
                              <div
                                v-if="friend.level != null"
                                class="friendPlayerLevel flex-shrink-0 font-normal"
                                :class="getLevelClass(friend.level)"
                                :title="t('friends.level', { level: friend.level })"
                              >
                                {{ friend.level }}
                              </div>
                            </div>
                            <!-- 游戏状态或在线状态 -->
                            <div class="flex flex-col gap-0.5">
                              <div class="flex items-center gap-2 text-xs">
                                <span
                                  class="flex items-center gap-1"
                                  :style="{ color: getPersonaStateColor(friend.personaState, friend.gameName) }"
                                >
                                  <span
                                    v-if="friend.gameName"
                                    class="i-mdi:gamepad-variant h-3.5 w-3.5"
                                  />
                                  <span
                                    v-else-if="friend.personaState === 0"
                                    class="i-mdi:circle-outline h-3.5 w-3.5"
                                  />
                                  <span
                                    v-else
                                    class="i-mdi:circle h-3.5 w-3.5"
                                  />
                                  {{ friend.gameName || getPersonaStateText(friend.personaState) }}
                                </span>
                              </div>
                              <!-- Rich Presence 富文本状态 -->
                              <div v-if="friend.richPresence" class="truncate text-xs text-gray-600 dark:text-gray-400">
                                {{ friend.richPresence }}
                              </div>
                              <!-- 上次在线 / 上次离线时间（仅离线时显示） -->
                              <template v-if="friend.personaState === 0">
                                <div v-if="formatRelativeTime(friend.lastLogOff)" class="text-xs text-gray-500">
                                  {{ t('friends.lastOfflineTime') }}: {{ formatRelativeTime(friend.lastLogOff) }}
                                </div>
                                <div v-if="formatRelativeTime(friend.lastLogOn)" class="text-xs text-gray-500">
                                  {{ t('friends.lastOnlineTime') }}: {{ formatRelativeTime(friend.lastLogOn) }}
                                </div>
                              </template>
                              <!-- 在线时显示本次上线时间 -->
                              <div v-else-if="formatRelativeTime(friend.lastLogOn)" class="text-xs text-gray-500">
                                {{ t('friends.onlineSince') }}: {{ formatRelativeTime(friend.lastLogOn) }}
                              </div>
                            </div>
                          </div>

                          <!-- 打开链接图标 -->
                          <div class="flex-shrink-0 opacity-0 transition-opacity group-hover:opacity-100">
                            <span class="i-mdi:open-in-new h-5 w-5 text-gray-400" />
                          </div>
                        </div>
                      </div>
                    </TransitionGroup>
                  </div>

                  <!-- 无好友提示 -->
                  <div v-if="userData.friends.length === 0" class="py-12">
                    <Empty :description="t('friends.noFriends')">
                      <template #image>
                        <span class="i-mdi:account-multiple-remove inline-block h-20 w-20 text-gray-300" />
                      </template>
                    </Empty>
                  </div>
                </Tabs.TabPane>
              </Tabs>
            </Spin>
          </template>
        </div>
      </Transition>
    </FaPageMain>

    <!-- 记录查看抽屉 -->
    <Drawer
      v-model:open="recordsDrawerVisible"
      :title="t('friends.records.title')"
      placement="right"
      width="min(720px, 90vw)"
      class="friend-records-drawer"
      :root-style="{ top: 'var(--g-title-bar-height, 32px)' }"
    >
      <template #extra>
        <div class="flex items-center gap-2">
          <Button size="small" :loading="recordsLoading" class="flex items-center gap-1" @click="fetchRecords">
            <template #icon>
              <span class="i-mdi:refresh h-3.5 w-3.5" />
            </template>
            {{ t('friends.records.refresh') }}
          </Button>
          <Popconfirm
            :title="t('friends.records.clearConfirm')"
            :ok-text="t('common.confirm')"
            :cancel-text="t('common.cancel')"
            @confirm="clearAllRecords"
          >
            <Button size="small" danger class="flex items-center gap-1">
              <template #icon>
                <span class="i-mdi:trash-can-outline h-3.5 w-3.5" />
              </template>
              {{ t('friends.records.clearAll') }}
            </Button>
          </Popconfirm>
        </div>
      </template>

      <!-- 筛选条件 -->
      <div class="mb-4 flex flex-wrap items-center gap-3">
        <div class="flex items-center gap-2">
          <span class="text-sm text-gray-500">{{ t('friends.records.filterType') }}:</span>
          <Select
            v-model:value="recordsFilterType"
            size="small"
            style="width: 130px;"
            @change="fetchRecords"
          >
            <Select.Option value="all">
              {{ t('friends.records.typeAll') }}
            </Select.Option>
            <Select.Option value="state">
              {{ t('friends.records.typeState') }}
            </Select.Option>
            <Select.Option value="game">
              {{ t('friends.records.typeGame') }}
            </Select.Option>
            <Select.Option value="personaName">
              {{ t('friends.records.typePersonaName') }}
            </Select.Option>
            <Select.Option value="richPresence">
              {{ t('friends.records.typeRichPresence') }}
            </Select.Option>
          </Select>
        </div>

        <div class="flex items-center gap-2">
          <span class="text-sm text-gray-500">{{ t('friends.records.filterFriend') }}:</span>
          <Select
            v-model:value="recordsFilterFriend"
            size="small"
            style="width: 180px;"
            show-search
            option-filter-prop="label"
            @change="fetchRecords"
          >
            <Select.Option value="all" :label="t('friends.records.typeAll')">
              {{ t('friends.records.typeAll') }}
            </Select.Option>
            <Select.Option
              v-for="friend in currentUserData?.friends || []"
              :key="friend.steamId"
              :value="friend.steamId"
              :label="friend.personaName"
            >
              {{ friend.personaName }}
            </Select.Option>
          </Select>
        </div>

        <div class="flex items-center gap-2">
          <span class="text-sm text-gray-500">{{ t('friends.records.limit') }}:</span>
          <Select
            v-model:value="recordsLimit"
            size="small"
            style="width: 100px;"
            @change="fetchRecords"
          >
            <Select.Option :value="100">
              100
            </Select.Option>
            <Select.Option :value="500">
              500
            </Select.Option>
            <Select.Option :value="1000">
              1000
            </Select.Option>
            <Select.Option :value="5000">
              5000
            </Select.Option>
          </Select>
        </div>

        <Tag color="default">
          {{ t('friends.records.totalRecords', { count: records.length }) }}
        </Tag>
      </div>

      <!-- 记录列表 -->
      <Spin :spinning="recordsLoading">
        <div v-if="records.length === 0" class="py-12">
          <Empty :description="t('friends.records.noRecords')">
            <template #image>
              <span class="i-mdi:file-document-outline inline-block h-20 w-20 text-gray-300" />
            </template>
          </Empty>
        </div>

        <div v-else class="space-y-2">
          <div
            v-for="record in records"
            :key="record.id"
            class="border rounded-lg bg-[var(--g-container-bg)] p-3 transition-all hover:shadow-md"
          >
            <div class="flex items-start gap-3">
              <Tag :color="getChangeTypeTagColor(record.changeType)" class="mt-0.5 flex-shrink-0">
                {{ getChangeTypeLabel(record.changeType) }}
              </Tag>
              <div class="min-w-0 flex-1">
                <div class="text-sm">
                  {{ formatRecordDescription(record) }}
                </div>
                <div class="mt-1 flex items-center gap-2 text-xs text-gray-500">
                  <span class="i-mdi:clock-outline h-3.5 w-3.5" />
                  {{ formatRecordTime(record.timestamp) }}
                  <span class="i-mdi:account-outline ml-2 h-3.5 w-3.5" />
                  {{ record.accountName }}
                </div>
              </div>
            </div>
          </div>
        </div>
      </Spin>
    </Drawer>
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
  transition: all 0.3s ease;
}

.list-enter-from {
  opacity: 0;
  transform: scale(0.95);
}

.list-move {
  transition: transform 0.3s ease;
}

/*
 * 好友数量可达数百，一次性布局/绘制所有卡片会明显掉帧。
 * content-visibility: auto 让浏览器跳过视口外卡片的布局与绘制；
 * contain-intrinsic-size 提供占位尺寸，避免滚动条长度跳动。
 * 注意：这不等于真正的虚拟滚动（DOM 节点仍然存在），
 * 真正的虚拟化留待视图重构时处理。
 */
.friend-card {
  contain-intrinsic-size: auto 160px;
  content-visibility: auto;
}

:deep(.steam-friends-tabs .ant-tabs-nav) {
  margin-bottom: 16px;
}

:deep(.steam-friends-tabs .ant-tabs-tab) {
  padding: 8px 16px;
}
</style>
