<route lang="yaml">
meta:
  title: 进程监控
  icon: i-material-symbols:monitor-heart-outline
</route>

<script setup lang="ts">
import { toast } from 'vue-sonner'

interface SteamGame {
  appId: string
  name: string
  installDir: string
  exePath: string
  libraryFolder: string
  isRunning?: boolean
}

interface SteamUser {
  steamId: string
  accountName: string
}

interface MonitorStatus {
  isRunning: boolean
  runningGames: number
}

interface SteamStatus {
  user: SteamUser | null
  installedGames: SteamGame[]
  runningGames: SteamGame[]
  isSteamRunning: boolean
}

const loading = ref(false)
const steamLoading = ref(false)
const autoRefresh = ref(false)
const refreshInterval = ref(5)
const steamStatus = ref<SteamStatus | null>(null)
const steamUsers = ref<SteamUser[]>([])
const installedGames = ref<SteamGame[]>([])
const runningGames = ref<SteamGame[]>([])
const showUserList = ref(false)
const showGameList = ref(false)
const showRunningGames = ref(false)
const monitorEnabled = ref(false)
const monitorInterval = ref(60)
const monitorStatus = ref<MonitorStatus | null>(null)
let timer: NodeJS.Timeout | null = null

// 获取 Steam 用户列表（从数据库）
async function fetchSteamUsers() {
  const electronApi = (window as any).electron
  if (!electronApi?.getSteamUsers) {
    return
  }

  try {
    const users = await electronApi.getSteamUsers()
    steamUsers.value = users
    console.warn('[Steam] 用户列表（数据库）:', users)
  }
  catch (error) {
    console.error('[Steam] 获取用户列表失败:', error)
  }
}

// 重新获取 Steam 用户列表（从文件）
async function refreshSteamUsers() {
  const electronApi = (window as any).electron
  if (!electronApi?.refreshSteamUsers) {
    return
  }

  try {
    loading.value = true
    const users = await electronApi.refreshSteamUsers()
    steamUsers.value = users
    toast.success(`成功更新 ${users.length} 个用户信息`)
    console.warn('[Steam] 用户列表（重新获取）:', users)
  }
  catch (error) {
    console.error('[Steam] 重新获取用户列表失败:', error)
    toast.error('重新获取用户列表失败')
  }
  finally {
    loading.value = false
  }
}

// 获取已安装游戏列表（从数据库）
async function fetchInstalledGames() {
  const electronApi = (window as any).electron
  if (!electronApi?.getSteamGames) {
    return
  }

  try {
    const games = await electronApi.getSteamGames()
    installedGames.value = games
    console.warn('[Steam] 已安装游戏（数据库）:', games.length)
  }
  catch (error) {
    console.error('[Steam] 获取游戏列表失败:', error)
  }
}

// 重新获取已安装游戏列表（从文件）
async function refreshInstalledGames() {
  const electronApi = (window as any).electron
  if (!electronApi?.refreshSteamGames) {
    return
  }

  try {
    loading.value = true
    const games = await electronApi.refreshSteamGames()
    installedGames.value = games
    toast.success(`成功更新 ${games.length} 个游戏信息`)
    console.warn('[Steam] 已安装游戏（重新获取）:', games.length)
  }
  catch (error) {
    console.error('[Steam] 重新获取游戏列表失败:', error)
    toast.error('重新获取游戏列表失败')
  }
  finally {
    loading.value = false
  }
}

// 获取 Steam 状态
async function fetchSteamStatus() {
  const electronApi = (window as any).electron
  if (!electronApi?.getSteamStatus) {
    console.error('[Steam] Electron API 不可用')
    return
  }

  steamLoading.value = true
  try {
    console.warn('[Steam] 调用 getSteamStatus()')
    const status = await electronApi.getSteamStatus()
    console.warn('[Steam] Steam状态:', status)
    steamStatus.value = status
    runningGames.value = status.runningGames || []
  }
  catch (error) {
    console.error('[Steam] 获取 Steam 状态失败:', error)
  }
  finally {
    steamLoading.value = false
  }
}

// 刷新所有数据
async function refreshAll() {
  await Promise.all([
    fetchSteamStatus(),
    fetchSteamUsers(),
    fetchInstalledGames(),
  ])
}

function startAutoRefresh() {
  if (timer) {
    clearInterval(timer)
  }
  if (autoRefresh.value) {
    timer = setInterval(() => {
      refreshAll()
    }, refreshInterval.value * 1000)
    toast.success(`已启动自动刷新，间隔 ${refreshInterval.value} 秒`)
  }
  else {
    toast.info('已停止自动刷新')
  }
}

function handleRefreshIntervalChange() {
  if (autoRefresh.value) {
    startAutoRefresh()
  }
}

// 获取监控状态
async function fetchMonitorStatus() {
  const electronApi = (window as any).electron
  if (!electronApi?.getMonitorStatus) {
    return
  }

  try {
    const status = await electronApi.getMonitorStatus()
    monitorStatus.value = status
    monitorEnabled.value = status.isRunning
    console.warn('[Monitor] 监控状态:', status)
  }
  catch (error) {
    console.error('[Monitor] 获取监控状态失败:', error)
  }
}

// 切换监控状态
async function toggleMonitor() {
  const electronApi = (window as any).electron
  if (!electronApi?.startMonitor || !electronApi?.stopMonitor) {
    return
  }

  try {
    if (monitorEnabled.value) {
      const status = await electronApi.startMonitor(monitorInterval.value)
      monitorStatus.value = status
      toast.success(`已启动游戏监控，间隔 ${monitorInterval.value} 秒`)
    }
    else {
      const status = await electronApi.stopMonitor()
      monitorStatus.value = status
      toast.info('已停止游戏监控')
    }
  }
  catch (error) {
    console.error('[Monitor] 切换监控状态失败:', error)
    toast.error('切换监控状态失败')
    monitorEnabled.value = !monitorEnabled.value
  }
}

// 更新监控间隔
async function handleMonitorIntervalChange() {
  const electronApi = (window as any).electron
  if (!electronApi?.updateMonitorInterval || !monitorEnabled.value) {
    return
  }

  try {
    const status = await electronApi.updateMonitorInterval(monitorInterval.value)
    monitorStatus.value = status
    toast.success(`监控间隔已更新为 ${monitorInterval.value} 秒`)
  }
  catch (error) {
    console.error('[Monitor] 更新监控间隔失败:', error)
    toast.error('更新监控间隔失败')
  }
}

// 监听监控状态变化
watch(monitorEnabled, toggleMonitor)
watch(autoRefresh, startAutoRefresh)

onMounted(() => {
  refreshAll()
  fetchMonitorStatus()
})

onUnmounted(() => {
  if (timer) {
    clearInterval(timer)
  }
})
</script>

<template>
  <div>
    <FaPageHeader title="进程监控" />
    <FaPageMain>
      <SteamAuth />
      <div>
        <!-- Steam 信息卡片 -->
        <div v-if="steamStatus" class="mb-5 rounded-lg bg-[var(--g-container-bg)] p-4">
          <div class="mb-3 flex items-center justify-between">
            <div class="flex items-center gap-3">
              <span class="text-lg font-semibold">Steam 状态</span>
              <el-tag v-if="steamStatus.isSteamRunning" type="success" size="small">
                <span class="flex items-center gap-1">
                  <span class="i-mdi:check-circle inline-block h-4 w-4" />
                  Steam 运行中
                </span>
              </el-tag>
              <el-tag v-else type="info" size="small">
                Steam 未运行
              </el-tag>
            </div>
            <el-button :loading="steamLoading" size="small" @click="fetchSteamStatus">
              刷新 Steam 状态
            </el-button>
          </div>

          <!-- 用户信息 -->
          <div v-if="steamStatus.user" class="mb-3 flex items-center gap-4 text-sm">
            <div class="flex items-center gap-2">
              <span class="text-muted-foreground">账号:</span>
              <span class="font-medium">{{ steamStatus.user.accountName }}</span>
            </div>
            <div class="flex items-center gap-2">
              <span class="text-muted-foreground">Steam ID:</span>
              <span class="text-xs font-mono">{{ steamStatus.user.steamId }}</span>
            </div>
          </div>

          <!-- 游戏统计 -->
          <div class="flex items-center gap-6 text-sm">
            <div class="flex items-center gap-2">
              <span class="text-muted-foreground">已安装游戏:</span>
              <span class="text-primary font-semibold">{{ steamStatus.installedGames.length }}</span>
            </div>
            <div class="flex items-center gap-2">
              <span class="text-muted-foreground">正在运行:</span>
              <span class="text-success font-semibold">{{ steamStatus.runningGames.length }}</span>
            </div>
          </div>

          <!-- 正在运行的游戏列表 -->
          <div v-if="runningGames.length > 0" class="mt-4 border-t pt-3">
            <div class="mb-2 text-sm font-medium">
              正在运行的游戏:
            </div>
            <div class="flex flex-wrap gap-2">
              <el-tag
                v-for="game in runningGames"
                :key="game.appId"
                type="success"
                size="large"
              >
                <div class="flex items-center gap-2">
                  <span class="i-mdi:gamepad-variant inline-block h-4 w-4" />
                  <span>{{ game.name }}</span>
                  <span class="text-xs text-muted-foreground">(AppID: {{ game.appId }})</span>
                </div>
              </el-tag>
            </div>
          </div>
        </div>

        <!-- Steam 用户列表 -->
        <div v-if="steamUsers.length > 0" class="mb-5">
          <div class="mb-3 flex items-center justify-between">
            <span class="text-base font-semibold">Steam 用户列表</span>
            <div class="flex items-center gap-2">
              <el-button size="small" :loading="loading" @click="refreshSteamUsers">
                重新获取用户列表
              </el-button>
              <el-button size="small" @click="showUserList = !showUserList">
                {{ showUserList ? '收起' : '展开' }}
              </el-button>
            </div>
          </div>
          <el-collapse-transition>
            <div v-show="showUserList" class="rounded-lg bg-[var(--g-container-bg)] p-4">
              <div class="grid grid-cols-1 gap-3 lg:grid-cols-3 md:grid-cols-2">
                <div
                  v-for="user in steamUsers"
                  :key="user.steamId"
                  class="flex items-center justify-between border rounded-lg p-3"
                  :class="{ 'border-primary bg-primary/5': (user as any).mostRecent }"
                >
                  <div class="flex flex-col gap-1">
                    <div class="flex items-center gap-2">
                      <span class="font-medium">{{ (user as any).personaName || user.accountName }}</span>
                      <el-tag v-if="(user as any).mostRecent" type="success" size="small">
                        当前
                      </el-tag>
                    </div>
                    <div class="text-xs text-muted-foreground">
                      账号: {{ user.accountName }}
                    </div>
                    <div class="text-xs text-muted-foreground font-mono">
                      ID: {{ user.steamId }}
                    </div>
                  </div>
                </div>
              </div>
            </div>
          </el-collapse-transition>
        </div>

        <!-- 已安装游戏列表 -->
        <div v-if="installedGames.length > 0" class="mb-5">
          <div class="mb-3 flex items-center justify-between">
            <div class="flex items-center gap-3">
              <span class="text-base font-semibold">已安装游戏</span>
              <el-tag type="info" size="small">
                {{ installedGames.length }} 个游戏
              </el-tag>
            </div>
            <div class="flex items-center gap-2">
              <el-button size="small" :loading="loading" @click="refreshInstalledGames">
                重新获取游戏列表
              </el-button>
              <el-button size="small" @click="showGameList = !showGameList">
                {{ showGameList ? '收起' : '展开' }}
              </el-button>
            </div>
          </div>
          <el-collapse-transition>
            <div v-show="showGameList" class="rounded-lg bg-[var(--g-container-bg)]">
              <el-table :data="installedGames" stripe max-height="400">
                <el-table-column prop="name" label="游戏名称" min-width="200" />
                <el-table-column prop="appId" label="AppID" width="100" />
                <el-table-column label="exe文件数" width="120">
                  <template #default="{ row }">
                    <el-tag size="small">
                      {{ row.exePaths?.length || 0 }}
                    </el-tag>
                  </template>
                </el-table-column>
                <el-table-column prop="gameFolder" label="安装目录" min-width="300" show-overflow-tooltip />
              </el-table>
            </div>
          </el-collapse-transition>
        </div>

        <!-- 正在运行的游戏详细列表 -->
        <div v-if="runningGames.length > 0" class="mb-5">
          <div class="mb-3 flex items-center justify-between">
            <div class="flex items-center gap-3">
              <span class="text-base font-semibold">正在运行的游戏</span>
              <el-tag type="success" size="small">
                {{ runningGames.length }} 个运行中
              </el-tag>
            </div>
            <el-button size="small" @click="showRunningGames = !showRunningGames">
              {{ showRunningGames ? '收起' : '展开' }}
            </el-button>
          </div>
          <el-collapse-transition>
            <div v-show="showRunningGames" class="rounded-lg bg-[var(--g-container-bg)]">
              <el-table :data="runningGames" stripe>
                <el-table-column prop="name" label="游戏名称" min-width="200">
                  <template #default="{ row }">
                    <div class="flex items-center gap-2">
                      <span class="text-success i-mdi:gamepad-variant inline-block h-4 w-4" />
                      <span class="font-medium">{{ row.name }}</span>
                    </div>
                  </template>
                </el-table-column>
                <el-table-column prop="appId" label="AppID" width="100" />
                <el-table-column label="进程信息" width="150">
                  <template #default="{ row }">
                    <div v-if="row.process" class="text-xs">
                      <div>PID: {{ row.process.pid }}</div>
                      <div>CPU: {{ row.process.cpu.toFixed(1) }}%</div>
                      <div>内存: {{ row.process.mem.toFixed(1) }}%</div>
                    </div>
                  </template>
                </el-table-column>
                <el-table-column prop="matchedExePath" label="匹配的exe路径" min-width="300" show-overflow-tooltip />
              </el-table>
            </div>
          </el-collapse-transition>
        </div>

        <!-- 工具栏 -->
        <div class="mb-5 flex items-center justify-between rounded-lg bg-[var(--g-container-bg)] p-4">
          <div class="flex items-center gap-4">
            <el-button
              type="primary"
              :loading="loading || steamLoading"
              icon="i-mdi:refresh"
              @click="refreshAll"
            >
              刷新全部
            </el-button>
            <el-switch
              v-model="autoRefresh"
              active-text="自动刷新"
              inactive-text="手动刷新"
            />
            <div v-if="autoRefresh" class="flex items-center gap-2">
              <span>刷新间隔：</span>
              <el-input-number
                v-model="refreshInterval"
                :min="1"
                :max="60"
                :step="1"
                @change="handleRefreshIntervalChange"
              />
              <span>秒</span>
            </div>
          </div>
        </div>

        <!-- 游戏监控工具栏 -->
        <div class="flex items-center justify-between rounded-lg bg-[var(--g-container-bg)] p-4">
          <div class="flex items-center gap-4">
            <el-switch
              v-model="monitorEnabled"
              active-text="游戏监控"
              inactive-text="游戏监控"
            />
            <div v-if="monitorEnabled" class="flex items-center gap-2">
              <span>监控间隔：</span>
              <el-input-number
                v-model="monitorInterval"
                :min="10"
                :max="3600"
                :step="10"
                @change="handleMonitorIntervalChange"
              />
              <span>秒</span>
            </div>
          </div>
          <div v-if="monitorStatus" class="flex items-center gap-3">
            <el-tag :type="monitorStatus.isRunning ? 'success' : 'info'">
              {{ monitorStatus.isRunning ? '监控中' : '未启动' }}
            </el-tag>
            <el-tag v-if="monitorStatus.isRunning" type="warning">
              记录中: {{ monitorStatus.runningGames }} 个游戏
            </el-tag>
          </div>
        </div>
      </div>
    </FaPageMain>
  </div>
</template>
