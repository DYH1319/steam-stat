<route lang="yaml">
  meta:
    title: Steam 登录测试
    icon: i-mdi:steam
  </route>

<script setup lang="ts">
import { onMounted, onUnmounted, ref } from 'vue'
import { toast } from 'vue-sonner'

const electronApi = (window as any).electron

// ==================== 账号密码登录 ====================
const accountLoginForm = ref({
  accountName: '',
  password: '',
  steamGuardMachineToken: '',
})
const accountLoginState = ref<{
  sessionId?: string
  needAction: boolean
  validActions: Array<{ type: number, detail?: string }>
  loading: boolean
  authenticated: boolean
  authResult?: any
  currentGuardType?: number
  guardCode: string
  submittingCode: boolean
}>({
  needAction: false,
  validActions: [],
  loading: false,
  authenticated: false,
  guardCode: '',
  submittingCode: false,
})

// 开始账号密码登录
async function startAccountLogin() {
  if (!accountLoginForm.value.accountName || !accountLoginForm.value.password) {
    toast.error('请输入账号和密码')
    return
  }

  accountLoginState.value.loading = true
  accountLoginState.value.authenticated = false
  accountLoginState.value.authResult = null

  try {
    const result = await electronApi.steamLoginAccountStart({
      accountName: accountLoginForm.value.accountName,
      password: accountLoginForm.value.password,
      steamGuardMachineToken: accountLoginForm.value.steamGuardMachineToken || undefined,
    })

    if (!result.success) {
      toast.error(`登录失败: ${result.error}`)
      accountLoginState.value.loading = false
      return
    }

    accountLoginState.value.sessionId = result.sessionId

    if (result.needAction) {
      accountLoginState.value.needAction = true
      accountLoginState.value.validActions = result.validActions || []
      toast.info('需要进行额外验证')
    }
    else {
      toast.success('登录请求已发送')
    }
  }
  catch (error: any) {
    toast.error(`登录失败: ${error.message}`)
    accountLoginState.value.loading = false
  }
}

// 提交验证码
async function submitGuardCode(guardType: number) {
  if (!accountLoginState.value.guardCode || !accountLoginState.value.sessionId) {
    toast.error('请输入验证码')
    return
  }

  accountLoginState.value.submittingCode = true
  accountLoginState.value.currentGuardType = guardType

  try {
    const result = await electronApi.steamLoginAccountSubmitCode({
      sessionId: accountLoginState.value.sessionId,
      code: accountLoginState.value.guardCode,
    })

    if (!result.success) {
      toast.error(`提交失败: ${result.error}`)
    }
    else {
      toast.success('验证码已提交')
      accountLoginState.value.guardCode = ''
    }
  }
  catch (error: any) {
    toast.error(`提交失败: ${error.message}`)
  }
  finally {
    accountLoginState.value.submittingCode = false
    accountLoginState.value.currentGuardType = undefined
  }
}

// 取消账号密码登录
async function cancelAccountLogin() {
  if (accountLoginState.value.sessionId) {
    await electronApi.steamLoginAccountCancel(accountLoginState.value.sessionId)
  }
  resetAccountLoginState()
}

// 重置账号密码登录状态
function resetAccountLoginState() {
  accountLoginState.value = {
    needAction: false,
    validActions: [],
    loading: false,
    authenticated: false,
    guardCode: '',
    submittingCode: false,
  }
}

// 获取守护类型名称
function getGuardTypeName(type: number): string {
  const names: Record<number, string> = {
    2: '邮箱验证码',
    3: '手机令牌验证码',
    5: '邮箱确认',
    4: '手机确认',
  }
  return names[type] || `未知类型(${type})`
}

// ==================== 二维码登录 ====================
const qrLoginState = ref<{
  sessionId?: string
  qrCodeUrl?: string
  loading: boolean
  scanned: boolean
  authenticated: boolean
  authResult?: any
  httpProxy: string
}>({
  loading: false,
  scanned: false,
  authenticated: false,
  httpProxy: '',
})

// 开始二维码登录
async function startQRLogin() {
  qrLoginState.value.loading = true
  qrLoginState.value.scanned = false
  qrLoginState.value.authenticated = false
  qrLoginState.value.authResult = null

  try {
    const result = await electronApi.steamLoginQRStart(
      qrLoginState.value.httpProxy || undefined,
    )

    if (!result.success) {
      toast.error(`启动失败: ${result.error}`)
      qrLoginState.value.loading = false
      return
    }

    qrLoginState.value.sessionId = result.sessionId
    qrLoginState.value.qrCodeUrl = result.qrCodeImageUrl
    toast.success('二维码已生成,请使用Steam手机App扫描')
  }
  catch (error: any) {
    toast.error(`启动失败: ${error.message}`)
    qrLoginState.value.loading = false
  }
}

// 取消二维码登录
async function cancelQRLogin() {
  if (qrLoginState.value.sessionId) {
    await electronApi.steamLoginQRCancel(qrLoginState.value.sessionId)
  }
  resetQRLoginState()
}

// 重置二维码登录状态
function resetQRLoginState() {
  qrLoginState.value = {
    loading: false,
    scanned: false,
    authenticated: false,
    httpProxy: qrLoginState.value.httpProxy, // 保留代理设置
  }
}

// ==================== 事件监听 ====================
function handleAccountLoginEvent(event: any) {
  const { type, sessionId, data } = event

  if (sessionId !== accountLoginState.value.sessionId) {
    return
  }

  switch (type) {
    case 'machineToken':
      toast.success('收到新的机器令牌')
      if (data.steamGuardMachineToken) {
        accountLoginForm.value.steamGuardMachineToken = data.steamGuardMachineToken
      }
      break

    case 'authenticated':
      accountLoginState.value.authenticated = true
      accountLoginState.value.authResult = data
      accountLoginState.value.loading = false
      accountLoginState.value.needAction = false
      toast.success('登录成功!')
      break

    case 'timeout':
      accountLoginState.value.loading = false
      toast.error('登录超时')
      break

    case 'error':
      accountLoginState.value.loading = false
      toast.error(`登录错误: ${data?.error}`)
      break
  }
}

function handleQRLoginEvent(event: any) {
  const { type, sessionId, data } = event

  if (sessionId !== qrLoginState.value.sessionId) {
    return
  }

  switch (type) {
    case 'remoteInteraction':
      qrLoginState.value.scanned = true
      toast.success('二维码已被扫描,请在手机上确认')
      break

    case 'authenticated':
      qrLoginState.value.authenticated = true
      qrLoginState.value.authResult = data
      qrLoginState.value.loading = false
      toast.success('登录成功!')
      break

    case 'timeout':
      qrLoginState.value.loading = false
      toast.error('二维码已过期')
      break

    case 'error':
      qrLoginState.value.loading = false
      toast.error(`登录错误: ${data?.error}`)
      break
  }
}

// 组件挂载和卸载
onMounted(() => {
  electronApi.onSteamLoginEvent(handleAccountLoginEvent)
  electronApi.onSteamQRLoginEvent(handleQRLoginEvent)
})

onUnmounted(() => {
  electronApi.removeSteamLoginEventListener()
  electronApi.removeSteamQRLoginEventListener()
})
</script>

<template>
  <div>
    <FaPageHeader title="Steam 登录测试" />
    <FaPageMain>
      <div class="space-y-6">
        <!-- 账号密码登录 -->
        <div class="rounded-xl from-blue-50 to-indigo-50 bg-gradient-to-br p-6 shadow-lg dark:from-gray-800 dark:to-gray-900">
          <div class="mb-6 flex items-center justify-between">
            <div class="flex items-center gap-3">
              <div class="i-mdi:account-key text-3xl text-blue-600 dark:text-blue-400" />
              <h3 class="text-xl text-gray-800 font-bold dark:text-gray-100">
                账号密码登录
              </h3>
            </div>
            <div class="flex gap-2">
              <el-button
                v-if="!accountLoginState.loading"
                type="primary"
                icon="i-mdi:account-key"
                @click="startAccountLogin"
              >
                开始登录
              </el-button>
              <el-button
                v-else
                type="danger"
                icon="i-mdi:account-key"
                @click="cancelAccountLogin"
              >
                取消登录
              </el-button>
            </div>
          </div>

          <!-- 登录表单 -->
          <div v-if="!accountLoginState.authenticated" class="mb-4 rounded-lg bg-white p-4 space-y-4 dark:bg-gray-800">
            <el-form label-width="120px">
              <el-form-item label="账号">
                <el-input
                  v-model="accountLoginForm.accountName"
                  placeholder="请输入Steam账号"
                  :disabled="accountLoginState.loading"
                  clearable
                >
                  <template #prefix>
                    <div class="i-mdi:account" />
                  </template>
                </el-input>
              </el-form-item>

              <el-form-item label="密码">
                <el-input
                  v-model="accountLoginForm.password"
                  type="password"
                  placeholder="请输入密码"
                  :disabled="accountLoginState.loading"
                  show-password
                  clearable
                >
                  <template #prefix>
                    <div class="i-mdi:lock" />
                  </template>
                </el-input>
              </el-form-item>

              <el-form-item label="机器令牌">
                <el-input
                  v-model="accountLoginForm.steamGuardMachineToken"
                  placeholder="可选,用于邮箱验证的机器令牌"
                  :disabled="accountLoginState.loading"
                  clearable
                >
                  <template #prefix>
                    <div class="i-mdi:key-variant" />
                  </template>
                </el-input>
              </el-form-item>
            </el-form>

            <!-- 验证操作 -->
            <div v-if="accountLoginState.needAction" class="mt-4 border-2 border-yellow-400 rounded-lg border-dashed bg-yellow-50 p-4 space-y-3 dark:border-yellow-600 dark:bg-yellow-900/20">
              <div class="mb-2 flex items-center gap-2 text-yellow-700 dark:text-yellow-400">
                <div class="i-mdi:alert-circle text-xl" />
                <span class="font-semibold">需要进行额外验证</span>
              </div>

              <div v-for="action in accountLoginState.validActions" :key="action.type" class="rounded-lg bg-white p-3 dark:bg-gray-800">
                <div class="mb-2 flex items-center gap-2">
                  <el-tag :type="[2, 3].includes(action.type) ? 'warning' : 'info'">
                    {{ getGuardTypeName(action.type) }}
                  </el-tag>
                  <span v-if="action.detail" class="text-sm text-gray-600 dark:text-gray-400">
                    {{ action.detail }}
                  </span>
                </div>

                <!-- 需要输入验证码的类型 -->
                <div v-if="[2, 3].includes(action.type)" class="flex gap-2">
                  <el-input
                    v-model="accountLoginState.guardCode"
                    placeholder="请输入验证码"
                    :disabled="accountLoginState.submittingCode"
                    @keyup.enter="submitGuardCode(action.type)"
                  >
                    <template #prefix>
                      <div class="i-mdi:shield-key" />
                    </template>
                  </el-input>
                  <el-button
                    type="primary"
                    :loading="accountLoginState.submittingCode && accountLoginState.currentGuardType === action.type"
                    @click="submitGuardCode(action.type)"
                  >
                    提交
                  </el-button>
                </div>

                <!-- 不需要输入的类型 -->
                <div v-else class="text-sm text-gray-600 dark:text-gray-400">
                  <div class="i-mdi:information inline-block" />
                  请在相应设备上确认登录
                </div>
              </div>
            </div>

            <!-- 加载状态 -->
            <div v-if="accountLoginState.loading" class="flex items-center justify-center gap-2 rounded-lg bg-blue-50 p-4 dark:bg-blue-900/20">
              <div class="i-mdi:loading animate-spin text-2xl text-blue-600" />
              <span class="text-blue-700 dark:text-blue-400">等待认证中...</span>
            </div>
          </div>

          <!-- 认证结果 -->
          <div v-if="accountLoginState.authenticated && accountLoginState.authResult" class="rounded-lg bg-green-50 p-4 dark:bg-green-900/20">
            <div class="mb-3 flex items-center gap-2 text-green-700 dark:text-green-400">
              <div class="i-mdi:check-circle text-2xl" />
              <span class="text-lg font-bold">登录成功!</span>
            </div>

            <el-descriptions bordered :column="2" size="small" class="bg-white dark:bg-gray-800">
              <el-descriptions-item label="SteamID">
                <el-tag type="success">
                  {{ accountLoginState.authResult.steamID }}
                </el-tag>
              </el-descriptions-item>
              <el-descriptions-item label="账号名">
                {{ accountLoginState.authResult.accountName }}
              </el-descriptions-item>
              <el-descriptions-item label="Access Token" :span="2">
                <div class="max-w-full break-all text-xs font-mono">
                  {{ accountLoginState.authResult.accessToken }}
                </div>
              </el-descriptions-item>
              <el-descriptions-item label="Refresh Token" :span="2">
                <div class="max-w-full break-all text-xs font-mono">
                  {{ accountLoginState.authResult.refreshToken }}
                </div>
              </el-descriptions-item>
              <el-descriptions-item v-if="accountLoginState.authResult.steamGuardMachineToken" label="机器令牌" :span="2">
                <div class="max-w-full break-all text-xs font-mono">
                  {{ accountLoginState.authResult.steamGuardMachineToken }}
                </div>
              </el-descriptions-item>
              <el-descriptions-item v-if="accountLoginState.authResult.webCookies" label="Web Cookies" :span="2">
                <el-popover placement="bottom" trigger="click" width="600">
                  <template #reference>
                    <el-button type="primary" size="small">
                      查看 Cookies ({{ accountLoginState.authResult.webCookies.length }})
                    </el-button>
                  </template>
                  <div class="max-h-96 overflow-y-auto">
                    <pre class="text-xs">{{ accountLoginState.authResult.webCookies.join('\n') }}</pre>
                  </div>
                </el-popover>
              </el-descriptions-item>
            </el-descriptions>

            <div class="mt-4">
              <el-button type="primary" @click="resetAccountLoginState">
                重新登录
              </el-button>
            </div>
          </div>
        </div>

        <!-- 二维码登录 -->
        <div class="rounded-xl from-purple-50 to-pink-50 bg-gradient-to-br p-6 shadow-lg dark:from-gray-800 dark:to-gray-900">
          <div class="mb-6 flex items-center justify-between">
            <div class="flex items-center gap-3">
              <div class="i-mdi:qrcode-scan text-3xl text-purple-600 dark:text-purple-400" />
              <h3 class="text-xl text-gray-800 font-bold dark:text-gray-100">
                二维码登录
              </h3>
            </div>
            <div class="flex gap-2">
              <el-button
                v-if="!qrLoginState.loading"
                type="primary"
                icon="i-mdi:qrcode"
                @click="startQRLogin"
              >
                生成二维码
              </el-button>
              <el-button
                v-else
                type="danger"
                icon="i-mdi:close"
                @click="cancelQRLogin"
              >
                取消登录
              </el-button>
            </div>
          </div>

          <!-- 代理设置 -->
          <div v-if="!qrLoginState.loading && !qrLoginState.authenticated" class="mb-4">
            <el-form label-width="120px">
              <el-form-item label="HTTP代理">
                <el-input
                  v-model="qrLoginState.httpProxy"
                  placeholder="可选,例如: http://127.0.0.1:10808"
                  clearable
                >
                  <template #prefix>
                    <div class="i-mdi:web" />
                  </template>
                </el-input>
              </el-form-item>
            </el-form>
          </div>

          <!-- 二维码显示 -->
          <div v-if="qrLoginState.qrCodeUrl && !qrLoginState.authenticated" class="flex flex-col items-center justify-center gap-4 rounded-lg bg-white p-6 dark:bg-gray-800">
            <div class="rounded-lg bg-white p-4 shadow-md">
              <img :src="qrLoginState.qrCodeUrl" alt="登录二维码" class="h-64 w-64">
            </div>

            <div class="text-center">
              <div v-if="!qrLoginState.scanned" class="flex items-center gap-2 text-gray-600 dark:text-gray-400">
                <div class="i-mdi:cellphone-link text-2xl" />
                <span>请使用Steam手机App扫描二维码</span>
              </div>
              <div v-else class="flex items-center gap-2 text-green-600 dark:text-green-400">
                <div class="i-mdi:check-circle text-2xl" />
                <span class="font-semibold">已扫描,请在手机上确认登录</span>
              </div>
            </div>

            <div v-if="qrLoginState.loading" class="flex items-center gap-2 text-purple-600 dark:text-purple-400">
              <div class="i-mdi:loading animate-spin text-xl" />
              <span>等待认证中...</span>
            </div>
          </div>

          <!-- 认证结果 -->
          <div v-if="qrLoginState.authenticated && qrLoginState.authResult" class="rounded-lg bg-green-50 p-4 dark:bg-green-900/20">
            <div class="mb-3 flex items-center gap-2 text-green-700 dark:text-green-400">
              <div class="i-mdi:check-circle text-2xl" />
              <span class="text-lg font-bold">登录成功!</span>
            </div>

            <el-descriptions bordered :column="2" size="small" class="bg-white dark:bg-gray-800">
              <el-descriptions-item label="SteamID">
                <el-tag type="success">
                  {{ qrLoginState.authResult.steamID }}
                </el-tag>
              </el-descriptions-item>
              <el-descriptions-item label="账号名">
                {{ qrLoginState.authResult.accountName }}
              </el-descriptions-item>
              <el-descriptions-item label="Access Token" :span="2">
                <div class="max-w-full break-all text-xs font-mono">
                  {{ qrLoginState.authResult.accessToken }}
                </div>
              </el-descriptions-item>
              <el-descriptions-item label="Refresh Token" :span="2">
                <div class="max-w-full break-all text-xs font-mono">
                  {{ qrLoginState.authResult.refreshToken }}
                </div>
              </el-descriptions-item>
              <el-descriptions-item v-if="qrLoginState.authResult.webCookies" label="Web Cookies" :span="2">
                <el-popover placement="bottom" trigger="click" width="600">
                  <template #reference>
                    <el-button type="primary" size="small">
                      查看 Cookies ({{ qrLoginState.authResult.webCookies.length }})
                    </el-button>
                  </template>
                  <div class="max-h-96 overflow-y-auto">
                    <pre class="text-xs">{{ qrLoginState.authResult.webCookies.join('\n') }}</pre>
                  </div>
                </el-popover>
              </el-descriptions-item>
            </el-descriptions>

            <div class="mt-4">
              <el-button type="primary" @click="resetQRLoginState">
                重新登录
              </el-button>
            </div>
          </div>
        </div>
      </div>
    </FaPageMain>
  </div>
</template>

<style scoped lang="scss">
.animate-spin {
  animation: spin 1s linear infinite;
}

@keyframes spin {
  from {
    transform: rotate(0deg);
  }

  to {
    transform: rotate(360deg);
  }
}
</style>
