<route lang="yaml">
meta:
  title: Steam 测试
  icon: i-mdi:flask-outline
</route>

<script setup lang="ts">
import { ref } from 'vue'
import { toast } from 'vue-sonner'

interface SteamUser {
  steamId: string
  accountName: string
  personaName: string
  mostRecent: boolean
}

interface SteamGame {
  appId: string
  name: string
  installDir: string
  gameFolder: string
  exePaths: string[]
  libraryFolder: string
}

interface RunningGame extends SteamGame {
  process: {
    pid: number
    name: string
    cpu: number
    mem: number
    path: string
  }
  isRunning: boolean
  matchedExePath: string
}

interface SteamStatus {
  user: { steamId: string, accountName: string } | null
  installedGames: SteamGame[]
  runningGames: RunningGame[]
  isSteamRunning: boolean
  steamProcess: any
}

// 获取 Electron API
const electronApi = (window as any).electron

// 测试结果状态
const testResults = ref<Record<string, any>>({
  login: null,
  steamRunning: null,
  users: null,
  runningGames: null,
  installedGames: null,
})

// 加载状态
const loading = ref<Record<string, boolean>>({
  login: false,
  steamRunning: false,
  users: false,
  runningGames: false,
  installedGames: false,
})

// Steam 登录表单
const loginForm = ref({
  accountName: '',
  password: '',
  twoFactorCode: '',
  authCode: '',
  rememberPassword: true,
})

// 测试1: 测试Steam登录
async function testSteamLogin() {
  if (!electronApi?.steamLogin) {
    toast.error('Electron API 不可用')
    return
  }

  if (!loginForm.value.accountName || !loginForm.value.password) {
    toast.error('请输入账号和密码')
    return
  }

  loading.value.login = true
  try {
    const result = await electronApi.steamLogin({
      accountName: loginForm.value.accountName,
      password: loginForm.value.password,
      twoFactorCode: loginForm.value.twoFactorCode || undefined,
      authCode: loginForm.value.authCode || undefined,
      rememberPassword: loginForm.value.rememberPassword,
    })

    testResults.value.login = result

    if (result.success) {
      toast.success('Steam 登录成功')
    }
    else {
      toast.error(`登录失败: ${result.error}`)
    }
  }
  catch (error: any) {
    testResults.value.login = { success: false, error: error.message }
    toast.error(`登录错误: ${error.message}`)
  }
  finally {
    loading.value.login = false
  }
}

// 清除测试1
function clearLoginTest() {
  testResults.value.login = null
  loginForm.value = {
    accountName: '',
    password: '',
    twoFactorCode: '',
    authCode: '',
    rememberPassword: true,
  }
  toast.info('已清除登录测试')
}

// 测试2: 测试获取Steam是否正在运行
async function testSteamRunning() {
  if (!electronApi?.getSteamStatus) {
    toast.error('Electron API 不可用')
    return
  }

  loading.value.steamRunning = true
  try {
    const status: SteamStatus = await electronApi.getSteamStatus()
    testResults.value.steamRunning = {
      isSteamRunning: status.isSteamRunning,
      steamProcess: status.steamProcess,
    }

    if (status.isSteamRunning) {
      toast.success('Steam 正在运行')
    }
    else {
      toast.info('Steam 未运行')
    }
  }
  catch (error: any) {
    testResults.value.steamRunning = { error: error.message }
    toast.error(`获取失败: ${error.message}`)
  }
  finally {
    loading.value.steamRunning = false
  }
}

// 清除测试2
function clearSteamRunningTest() {
  testResults.value.steamRunning = null
  toast.info('已清除Steam运行状态测试')
}

// 测试3: 测试获取当前登录的Steam账号列表
async function testSteamUsers() {
  if (!electronApi?.getSteamUsers) {
    toast.error('Electron API 不可用')
    return
  }

  loading.value.users = true
  try {
    const users: SteamUser[] = await electronApi.getSteamUsers()
    testResults.value.users = users
    toast.success(`成功获取 ${users.length} 个Steam账号`)
  }
  catch (error: any) {
    testResults.value.users = { error: error.message }
    toast.error(`获取失败: ${error.message}`)
  }
  finally {
    loading.value.users = false
  }
}

// 清除测试3
function clearUsersTest() {
  testResults.value.users = null
  toast.info('已清除Steam账号列表测试')
}

// 测试4: 测试获取当前运行的Steam游戏列表
async function testRunningGames() {
  if (!electronApi?.getSteamStatus) {
    toast.error('Electron API 不可用')
    return
  }

  loading.value.runningGames = true
  try {
    const status: SteamStatus = await electronApi.getSteamStatus()
    testResults.value.runningGames = status.runningGames
    toast.success(`成功获取 ${status.runningGames.length} 个运行中的游戏`)
  }
  catch (error: any) {
    testResults.value.runningGames = { error: error.message }
    toast.error(`获取失败: ${error.message}`)
  }
  finally {
    loading.value.runningGames = false
  }
}

// 清除测试4
function clearRunningGamesTest() {
  testResults.value.runningGames = null
  toast.info('已清除运行中游戏列表测试')
}

// 测试5: 测试获取本地安装的游戏列表
async function testInstalledGames() {
  if (!electronApi?.getSteamGames) {
    toast.error('Electron API 不可用')
    return
  }

  loading.value.installedGames = true
  try {
    const games: SteamGame[] = await electronApi.getSteamGames()
    testResults.value.installedGames = games
    toast.success(`成功获取 ${games.length} 个已安装的游戏`)
  }
  catch (error: any) {
    testResults.value.installedGames = { error: error.message }
    toast.error(`获取失败: ${error.message}`)
  }
  finally {
    loading.value.installedGames = false
  }
}

// 清除测试5
function clearInstalledGamesTest() {
  testResults.value.installedGames = null
  toast.info('已清除已安装游戏列表测试')
}
</script>

<template>
  <div>
    <FaPageHeader title="Steam 测试" />
    <FaPageMain>
      <div class="space-y-6">
        <!-- 测试1: Steam登录 -->
        <div class="rounded-lg bg-[var(--g-container-bg)] p-6">
          <div class="mb-4 flex items-center justify-between">
            <h3 class="text-lg font-semibold">
              <span class="i-mdi:login inline-block h-5 w-5 align-text-bottom" />
              测试1: Steam 登录
            </h3>
            <div class="flex gap-2">
              <el-button
                type="primary"
                :loading="loading.login"
                @click="testSteamLogin"
              >
                执行测试
              </el-button>
              <el-button
                :disabled="!testResults.login"
                @click="clearLoginTest"
              >
                清除结果
              </el-button>
            </div>
          </div>

          <div class="mb-4">
            <el-form :model="loginForm" label-width="120px">
              <el-form-item label="账号名称">
                <el-input
                  v-model="loginForm.accountName"
                  placeholder="Steam 账号"
                />
              </el-form-item>
              <el-form-item label="密码">
                <el-input
                  v-model="loginForm.password"
                  type="password"
                  placeholder="密码"
                  show-password
                />
              </el-form-item>
              <el-form-item label="移动验证码">
                <el-input
                  v-model="loginForm.twoFactorCode"
                  placeholder="5位验证码（如有）"
                  maxlength="5"
                />
              </el-form-item>
              <el-form-item label="邮箱验证码">
                <el-input
                  v-model="loginForm.authCode"
                  placeholder="邮箱验证码（如有）"
                />
              </el-form-item>
              <el-form-item label="记住密码">
                <el-switch v-model="loginForm.rememberPassword" />
              </el-form-item>
            </el-form>
          </div>

          <div v-if="testResults.login" class="rounded-lg bg-gray-100 p-4 dark:bg-gray-800">
            <div class="mb-2 text-sm text-muted-foreground font-medium">
              测试结果:
            </div>
            <el-alert
              v-if="testResults.login.success"
              title="登录成功"
              type="success"
              :closable="false"
            />
            <el-alert
              v-else
              :title="`登录失败: ${testResults.login.error}`"
              type="error"
              :closable="false"
            />
          </div>
        </div>

        <!-- 测试2: Steam运行状态 -->
        <div class="rounded-lg bg-[var(--g-container-bg)] p-6">
          <div class="mb-4 flex items-center justify-between">
            <h3 class="text-lg font-semibold">
              <span class="i-mdi:steam inline-block h-5 w-5 align-text-bottom" />
              测试2: 获取 Steam 是否正在运行
            </h3>
            <div class="flex gap-2">
              <el-button
                type="primary"
                :loading="loading.steamRunning"
                @click="testSteamRunning"
              >
                执行测试
              </el-button>
              <el-button
                :disabled="!testResults.steamRunning"
                @click="clearSteamRunningTest"
              >
                清除结果
              </el-button>
            </div>
          </div>

          <div v-if="testResults.steamRunning" class="rounded-lg bg-gray-100 p-4 dark:bg-gray-800">
            <div class="mb-2 text-sm text-muted-foreground font-medium">
              测试结果:
            </div>
            <div v-if="testResults.steamRunning.error">
              <el-alert
                :title="`错误: ${testResults.steamRunning.error}`"
                type="error"
                :closable="false"
              />
            </div>
            <div v-else>
              <el-tag
                :type="testResults.steamRunning.isSteamRunning ? 'success' : 'info'"
                size="large"
              >
                {{ testResults.steamRunning.isSteamRunning ? 'Steam 正在运行' : 'Steam 未运行' }}
              </el-tag>
              <div v-if="testResults.steamRunning.steamProcess" class="mt-4">
                <div class="mb-2 text-sm font-medium">
                  进程信息:
                </div>
                <pre class="text-xs">{{ JSON.stringify(testResults.steamRunning.steamProcess, null, 2) }}</pre>
              </div>
            </div>
          </div>
        </div>

        <!-- 测试3: Steam账号列表 -->
        <div class="rounded-lg bg-[var(--g-container-bg)] p-6">
          <div class="mb-4 flex items-center justify-between">
            <h3 class="text-lg font-semibold">
              <span class="i-mdi:account-multiple inline-block h-5 w-5 align-text-bottom" />
              测试3: 获取当前登录的 Steam 账号列表
            </h3>
            <div class="flex gap-2">
              <el-button
                type="primary"
                :loading="loading.users"
                @click="testSteamUsers"
              >
                执行测试
              </el-button>
              <el-button
                :disabled="!testResults.users"
                @click="clearUsersTest"
              >
                清除结果
              </el-button>
            </div>
          </div>

          <div v-if="testResults.users" class="rounded-lg bg-gray-100 p-4 dark:bg-gray-800">
            <div class="mb-2 text-sm text-muted-foreground font-medium">
              测试结果:
            </div>
            <div v-if="testResults.users.error">
              <el-alert
                :title="`错误: ${testResults.users.error}`"
                type="error"
                :closable="false"
              />
            </div>
            <div v-else>
              <el-tag class="mb-4" type="success" size="large">
                找到 {{ testResults.users.length }} 个账号
              </el-tag>
              <div class="grid grid-cols-1 gap-3 lg:grid-cols-3 md:grid-cols-2">
                <div
                  v-for="user in testResults.users"
                  :key="user.steamId"
                  class="flex items-center justify-between border rounded-lg p-3"
                  :class="{ 'border-primary bg-primary/5': user.mostRecent }"
                >
                  <div class="flex flex-col gap-1">
                    <div class="flex items-center gap-2">
                      <span class="font-medium">{{ user.personaName || user.accountName }}</span>
                      <el-tag v-if="user.mostRecent" type="success" size="small">
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
          </div>
        </div>

        <!-- 测试4: 运行中的游戏 -->
        <div class="rounded-lg bg-[var(--g-container-bg)] p-6">
          <div class="mb-4 flex items-center justify-between">
            <h3 class="text-lg font-semibold">
              <span class="i-mdi:gamepad-variant inline-block h-5 w-5 align-text-bottom" />
              测试4: 获取当前运行的 Steam 游戏列表
            </h3>
            <div class="flex gap-2">
              <el-button
                type="primary"
                :loading="loading.runningGames"
                @click="testRunningGames"
              >
                执行测试
              </el-button>
              <el-button
                :disabled="!testResults.runningGames"
                @click="clearRunningGamesTest"
              >
                清除结果
              </el-button>
            </div>
          </div>

          <div v-if="testResults.runningGames" class="rounded-lg bg-gray-100 p-4 dark:bg-gray-800">
            <div class="mb-2 text-sm text-muted-foreground font-medium">
              测试结果:
            </div>
            <div v-if="testResults.runningGames.error">
              <el-alert
                :title="`错误: ${testResults.runningGames.error}`"
                type="error"
                :closable="false"
              />
            </div>
            <div v-else>
              <el-tag
                class="mb-4"
                :type="testResults.runningGames.length > 0 ? 'success' : 'info'"
                size="large"
              >
                找到 {{ testResults.runningGames.length }} 个运行中的游戏
              </el-tag>
              <div v-if="testResults.runningGames.length > 0">
                <el-table :data="testResults.runningGames" stripe>
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
              <div v-else class="text-sm text-muted-foreground">
                当前没有运行中的游戏
              </div>
            </div>
          </div>
        </div>

        <!-- 测试5: 已安装的游戏 -->
        <div class="rounded-lg bg-[var(--g-container-bg)] p-6">
          <div class="mb-4 flex items-center justify-between">
            <h3 class="text-lg font-semibold">
              <span class="i-mdi:folder-download inline-block h-5 w-5 align-text-bottom" />
              测试5: 获取本地安装的游戏列表
            </h3>
            <div class="flex gap-2">
              <el-button
                type="primary"
                :loading="loading.installedGames"
                @click="testInstalledGames"
              >
                执行测试
              </el-button>
              <el-button
                :disabled="!testResults.installedGames"
                @click="clearInstalledGamesTest"
              >
                清除结果
              </el-button>
            </div>
          </div>

          <div v-if="testResults.installedGames" class="rounded-lg bg-gray-100 p-4 dark:bg-gray-800">
            <div class="mb-2 text-sm text-muted-foreground font-medium">
              测试结果:
            </div>
            <div v-if="testResults.installedGames.error">
              <el-alert
                :title="`错误: ${testResults.installedGames.error}`"
                type="error"
                :closable="false"
              />
            </div>
            <div v-else>
              <el-tag class="mb-4" type="success" size="large">
                找到 {{ testResults.installedGames.length }} 个已安装的游戏
              </el-tag>
              <div v-if="testResults.installedGames.length > 0">
                <el-table :data="testResults.installedGames" stripe max-height="400">
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
              <div v-else class="text-sm text-muted-foreground">
                没有找到已安装的游戏
              </div>
            </div>
          </div>
        </div>
      </div>
    </FaPageMain>
  </div>
</template>

<style scoped lang="scss">
pre {
  padding: 12px;
  overflow-x: auto;
  background: var(--el-bg-color);
  border-radius: 4px;
}
</style>
