<script setup lang="ts">
import { Button, Checkbox, Input, InputPassword, Modal, Select, Spin, Tabs } from 'ant-design-vue'
import { useI18n } from 'vue-i18n'
import { toast } from 'vue-sonner'
import { useIpc, useIpcListener } from '@/composables/useIpc'
import { useSteamStore } from '@/store/modules/steam'
import { localizeSteamLoginError, steamPersonaStateOptions } from '@/utils/steamLogin'

const { t } = useI18n()
const ipc = useIpc()
const steamStore = useSteamStore()

// 当前登录模式 tab
const activeTab = ref<'credentials' | 'qrCode'>('credentials')

// 凭据登录表单
const credentialsForm = reactive({
  username: '',
  password: '',
  rememberMe: true,
})

// QR 码登录
const qrRememberMe = ref(true)
const qrImageBase64 = ref('')

// 登录后的 Persona 状态 (默认在线)
const loginPersonaState = ref(1)
// 标记是否需要在登录成功后设置 Persona 状态
const shouldSetPersonaStateOnLogin = ref(false)

// 登录状态
const loginStatus = ref<'idle' | 'connecting' | 'authenticating' | 'guardCodeNeeded' | 'deviceConfirmation' | 'qrWaiting' | 'success'>('idle')
const isLoginLoading = computed(() => ['connecting', 'authenticating', 'qrWaiting', 'deviceConfirmation'].includes(loginStatus.value))

// Steam Guard
const guardModal = reactive({
  visible: false,
  guardType: '' as 'device' | 'email' | '',
  email: '',
  code: '',
  previousCodeWasIncorrect: false,
})

// 设备确认选择弹窗
const deviceConfirmModal = reactive({
  visible: false,
})

// 登录成功后延迟重置状态的定时器，需在卸载时清理，否则会在组件销毁后写入已失效的 ref
let resetStatusTimer: ReturnType<typeof setTimeout> | null = null

useIpcListener(
  callback => ipc.steamLoginEventOnListener(callback),
  () => ipc.steamLoginEventRemoveListener(),
  onLoginEvent,
)

onUnmounted(() => {
  if (resetStatusTimer) {
    clearTimeout(resetStatusTimer)
    resetStatusTimer = null
  }
  if (isLoginLoading.value) {
    handleCancelLogin()
  }
})

// 监听后端登录事件
function onLoginEvent(event: SteamLoginEvent) {
  switch (event.type) {
    case 'connecting':
      loginStatus.value = 'connecting'
      break
    case 'authenticating':
      loginStatus.value = 'authenticating'
      if (activeTab.value === 'qrCode') {
        loginStatus.value = 'qrWaiting'
      }
      break
    case 'guardCodeNeeded':
      loginStatus.value = 'guardCodeNeeded'
      guardModal.visible = true
      guardModal.guardType = event.data?.guardType || ''
      guardModal.email = event.data?.email || ''
      guardModal.previousCodeWasIncorrect = event.data?.previousCodeWasIncorrect || false
      guardModal.code = ''
      break
    case 'deviceConfirmationNeeded':
      loginStatus.value = 'deviceConfirmation'
      deviceConfirmModal.visible = true
      break
    case 'qrCode':
      loginStatus.value = 'qrWaiting'
      if (event.data?.qrImageBase64) {
        qrImageBase64.value = event.data.qrImageBase64
      }
      break
    case 'success':
      loginStatus.value = 'success'
      guardModal.visible = false
      deviceConfirmModal.visible = false
      toast.success(t('steamLogin.loginSuccess', { accountName: event.data?.accountName || '' }))
      if (event.data?.accountName) {
        steamStore.addAccount(event.data.accountName)
      }
      // 登录成功后设置 Persona 状态
      if (shouldSetPersonaStateOnLogin.value && event.data?.accountName) {
        handleSetPersonaState(event.data.accountName, loginPersonaState.value, true)
        shouldSetPersonaStateOnLogin.value = false
      }
      // 重置状态
      if (resetStatusTimer) {
        clearTimeout(resetStatusTimer)
      }
      resetStatusTimer = setTimeout(() => {
        resetStatusTimer = null
        loginStatus.value = 'idle'
        qrImageBase64.value = ''
      }, 2000)
      break
    case 'error':
      loginStatus.value = 'idle'
      guardModal.visible = false
      deviceConfirmModal.visible = false
      qrImageBase64.value = ''
      shouldSetPersonaStateOnLogin.value = false
      toast.error(t('steamLogin.loginFailed', { error: localizeSteamLoginError(event.data?.errorCode ?? undefined, event.data?.message ?? undefined) }))
      break
    case 'cancelled':
      loginStatus.value = 'idle'
      guardModal.visible = false
      deviceConfirmModal.visible = false
      qrImageBase64.value = ''
      shouldSetPersonaStateOnLogin.value = false
      toast.info(t('steamLogin.loginCancelled'))
      break
    case 'userDisconnected':
      // 用户断线，从已登录列表中移除（后端会自动尝试重连）
      if (event.data?.accountName) {
        const accountName = event.data.accountName
        steamStore.removeAccount(accountName)
        toast.warning(t('steamLogin.userDisconnected', { accountName }))
      }
      break
    case 'userReconnected':
      if (event.data?.accountName) {
        toast.success(t('steamLogin.userReconnected', { accountName: event.data.accountName }))
        steamStore.addAccount(event.data.accountName)
      }
      break
    case 'reconnectFailed':
      // 后端已放弃自动重连（凭证失效 / 账号异常 / 重试耗尽），需要用户手动处理
      if (event.data?.accountName) {
        steamStore.removeAccount(event.data.accountName)
        toast.error(t('steamLogin.reconnectFailed', {
          accountName: event.data.accountName,
          reason: localizeSteamLoginError(event.data.errorCode ?? undefined, event.data.errorCode ?? undefined),
        }), { duration: 10000 })
      }
      break
  }
}

// 凭据登录
async function handleCredentialsLogin() {
  if (!credentialsForm.username.trim()) {
    toast.warning(t('steamLogin.usernameRequired'))
    return
  }
  if (!credentialsForm.password.trim()) {
    toast.warning(t('steamLogin.passwordRequired'))
    return
  }

  shouldSetPersonaStateOnLogin.value = true
  try {
    await ipc.steamLoginCredentialsStart({
      username: credentialsForm.username.trim(),
      password: credentialsForm.password.trim(),
      rememberMe: credentialsForm.rememberMe,
    })
  }
  catch (e: any) {
    toast.error(t('steamLogin.loginFailed', { error: localizeSteamLoginError(undefined, e?.message || String(e)) }))
    loginStatus.value = 'idle'
    shouldSetPersonaStateOnLogin.value = false
  }
}

// QR 码登录
async function handleQrLogin() {
  qrImageBase64.value = ''
  shouldSetPersonaStateOnLogin.value = true
  try {
    await ipc.steamLoginQrStart({
      rememberMe: qrRememberMe.value,
    })
  }
  catch (e: any) {
    toast.error(t('steamLogin.loginFailed', { error: localizeSteamLoginError(undefined, e?.message || String(e)) }))
    loginStatus.value = 'idle'
    shouldSetPersonaStateOnLogin.value = false
  }
}

// 提交 Guard 验证码
async function handleGuardCodeSubmit() {
  if (!guardModal.code.trim()) {
    return
  }
  try {
    await ipc.steamLoginGuardCodeSubmit({ code: guardModal.code.trim() })
    guardModal.visible = false
    loginStatus.value = 'authenticating'
  }
  catch (e: any) {
    toast.error(e?.message || e)
  }
}

function handleSwitchToUseAppCode() {
  deviceConfirmModal.visible = false
  ipc.steamLoginSwitchToUseCode()
}

function handleConfirmInApp() {
  deviceConfirmModal.visible = false
  ipc.steamLoginConfirmDevice()
}

// 取消登录
function handleCancelLogin() {
  ipc.steamLoginCancel()
  loginStatus.value = 'idle'
  qrImageBase64.value = ''
  guardModal.visible = false
  deviceConfirmModal.visible = false
  shouldSetPersonaStateOnLogin.value = false
}

// 设置用户 Persona 状态
async function handleSetPersonaState(accountName: string, personaState: number, silent = false) {
  try {
    const success = await ipc.steamLoginUserSetPersonaState({ accountName, personaState })
    if (success && !silent) {
      toast.success(t('steamLogin.setPersonaState'))
    }
  }
  catch (e: any) {
    console.error('Failed to set persona state:', e)
  }
}

// Persona 状态选项
const personaStateOptions = steamPersonaStateOptions()

// Select 下拉渲染进触发节点内，避免被外层 Modal 的 z-index 遮挡
function selectPopupContainer(trigger: HTMLElement): HTMLElement {
  return trigger.parentElement ?? document.body
}

// 状态文本
const statusText = computed(() => {
  switch (loginStatus.value) {
    case 'connecting':
      return t('steamLogin.connecting')
    case 'authenticating':
      return t('steamLogin.authenticating')
    case 'guardCodeNeeded':
      return t('steamLogin.guardCodeNeeded')
    case 'deviceConfirmation':
      return t('steamLogin.deviceConfirmation')
    case 'qrWaiting':
      return t('steamLogin.qrCodeWaiting')
    default:
      return ''
  }
})
</script>

<template>
  <div>
    <div class="space-y-6">
      <!-- 登录卡片 -->
      <Transition name="slide-fade" appear>
        <div class="p-6 rounded-lg bg-[var(--g-container-bg)] shadow-lg">
          <Tabs v-model:active-key="activeTab" :disabled="isLoginLoading">
            <!-- 账号密码登录 -->
            <Tabs.TabPane key="credentials" :tab="t('steamLogin.credentialsTab')">
              <div class="mx-auto py-4 max-w-md space-y-4">
                <div>
                  <label class="text-sm font-medium mb-1 block">{{ t('steamLogin.username') }}</label>
                  <Input
                    v-model:value="credentialsForm.username"
                    :placeholder="t('steamLogin.usernamePlaceholder')"
                    :disabled="isLoginLoading"
                    size="large"
                    allow-clear
                    @press-enter="handleCredentialsLogin"
                  >
                    <template #prefix>
                      <span class="i-mdi:account op-50" />
                    </template>
                  </Input>
                </div>

                <div>
                  <label class="text-sm font-medium mb-1 block">{{ t('steamLogin.password') }}</label>
                  <InputPassword
                    v-model:value="credentialsForm.password"
                    :placeholder="t('steamLogin.passwordPlaceholder')"
                    :disabled="isLoginLoading"
                    size="large"
                    @press-enter="handleCredentialsLogin"
                  >
                    <template #prefix>
                      <span class="i-mdi:lock op-50" />
                    </template>
                  </InputPassword>
                </div>

                <div class="flex gap-2 items-center">
                  <Checkbox v-model:checked="credentialsForm.rememberMe" :disabled="isLoginLoading">
                    {{ t('steamLogin.rememberMe') }}
                  </Checkbox>
                  <span class="text-xs op-50">{{ t('steamLogin.rememberMeDesc') }}</span>
                </div>

                <div class="flex gap-2 items-center">
                  <label class="text-sm font-medium whitespace-nowrap">{{ t('steamLogin.loginAsState') }}</label>
                  <Select
                    v-model:value="loginPersonaState"
                    :options="personaStateOptions"
                    :disabled="isLoginLoading"
                    :get-popup-container="selectPopupContainer"
                    style="width: 150px;"
                  />
                </div>

                <!-- 状态信息 -->
                <div v-if="statusText && activeTab === 'credentials'" class="text-sm px-3 py-2 rounded-md bg-primary/10 flex gap-2 items-center">
                  <Spin class="flex items-center" :spinning="true" />
                  <span>{{ statusText }}</span>
                </div>

                <div class="flex gap-3">
                  <Button
                    type="primary"
                    size="large"
                    block
                    :loading="isLoginLoading"
                    :disabled="isLoginLoading"
                    @click="handleCredentialsLogin"
                  >
                    {{ t('steamLogin.loginButton') }}
                  </Button>
                  <Button
                    v-if="isLoginLoading"
                    size="large"
                    danger
                    @click="handleCancelLogin"
                  >
                    {{ t('steamLogin.cancelButton') }}
                  </Button>
                </div>
              </div>
            </Tabs.TabPane>

            <!-- 扫码登录 -->
            <Tabs.TabPane key="qrCode" :tab="t('steamLogin.qrCodeTab')">
              <div class="mx-auto py-4 max-w-md space-y-4">
                <div class="flex flex-col gap-4 items-center">
                  <!-- QR 码展示区域 -->
                  <div class="border border-border rounded-lg bg-white flex min-h-64 w-64 items-center justify-center">
                    <img
                      v-if="qrImageBase64"
                      :src="qrImageBase64"
                      class="rounded-lg h-full w-full"
                      alt="QR Code"
                    >
                    <div v-else class="text-center op-50 flex flex-col gap-2 items-center">
                      <span class="i-mdi:qrcode-scan h-12 w-12 inline-block" />
                      <span class="text-sm">{{ t('steamLogin.qrCodeInstruction') }}</span>
                    </div>
                  </div>

                  <!-- 状态提示 -->
                  <div v-if="statusText && activeTab === 'qrCode'" class="text-sm px-3 py-2 rounded-md bg-primary/10 flex gap-2 items-center">
                    <Spin :spinning="true" />
                    <span>{{ statusText }}</span>
                  </div>
                  <p v-else class="text-sm text-center op-60">
                    {{ t('steamLogin.qrCodeInstruction') }}
                  </p>
                </div>

                <div class="flex gap-2 items-center">
                  <Checkbox v-model:checked="qrRememberMe" :disabled="isLoginLoading">
                    {{ t('steamLogin.rememberMe') }}
                  </Checkbox>
                  <span class="text-xs op-50">{{ t('steamLogin.rememberMeDesc') }}</span>
                </div>

                <div class="flex gap-2 items-center">
                  <label class="text-sm font-medium whitespace-nowrap">{{ t('steamLogin.loginAsState') }}</label>
                  <Select
                    v-model:value="loginPersonaState"
                    :options="personaStateOptions"
                    :disabled="isLoginLoading"
                    :get-popup-container="selectPopupContainer"
                    style="width: 150px;"
                  />
                </div>

                <div class="flex gap-3">
                  <Button
                    v-if="!isLoginLoading"
                    type="primary"
                    size="large"
                    block
                    @click="handleQrLogin"
                  >
                    {{ t('steamLogin.qrCodeTab') }}
                  </Button>
                  <Button
                    v-if="isLoginLoading"
                    size="large"
                    danger
                    block
                    @click="handleCancelLogin"
                  >
                    {{ t('steamLogin.cancelButton') }}
                  </Button>
                </div>
              </div>
            </Tabs.TabPane>
          </Tabs>
        </div>
      </Transition>
    </div>

    <!-- Steam Guard 验证码弹窗 -->
    <Modal
      v-model:open="guardModal.visible"
      :title="t('steamLogin.guardCodeNeeded')"
      :mask-closable="false"
      :closable="false"
      :footer="null"
    >
      <div class="py-2 space-y-4">
        <p v-if="guardModal.guardType === 'device'">
          {{ t('steamLogin.guardCodeDevice') }}
        </p>
        <p v-else-if="guardModal.guardType === 'email'">
          {{ t('steamLogin.guardCodeEmail', { email: guardModal.email }) }}
        </p>

        <div v-if="guardModal.previousCodeWasIncorrect" class="text-sm text-red-500 px-3 py-2 rounded-md bg-red-500/10">
          {{ t('steamLogin.guardCodeIncorrect') }}
        </div>

        <Input
          v-model:value="guardModal.code"
          :placeholder="t('steamLogin.guardCodePlaceholder')"
          size="large"
          allow-clear
          @press-enter="handleGuardCodeSubmit"
        >
          <template #prefix>
            <span class="i-mdi:shield-key op-50" />
          </template>
        </Input>

        <div class="flex gap-3 justify-end">
          <Button @click="handleCancelLogin">
            {{ t('steamLogin.cancelButton') }}
          </Button>
          <Button
            type="primary"
            :disabled="!guardModal.code.trim()"
            @click="handleGuardCodeSubmit"
          >
            {{ t('steamLogin.guardCodeSubmit') }}
          </Button>
        </div>
      </div>
    </Modal>

    <!-- 设备确认选择弹窗 -->
    <Modal
      v-model:open="deviceConfirmModal.visible"
      :title="t('steamLogin.deviceConfirmChoice')"
      :mask-closable="false"
      :closable="false"
      :footer="null"
    >
      <div class="py-2 space-y-4">
        <p class="text-sm op-80">
          {{ t('steamLogin.deviceConfirmMessage') }}
        </p>

        <div class="flex flex-col gap-3">
          <Button
            type="primary"
            size="large"
            block
            @click="handleConfirmInApp"
          >
            <span class="i-mdi:cellphone-check mr-2" />
            {{ t('steamLogin.confirmInApp') }}
          </Button>
          <Button
            size="large"
            block
            @click="handleSwitchToUseAppCode"
          >
            <span class="i-mdi:shield-key mr-2" />
            {{ t('steamLogin.useVerificationCode') }}
          </Button>
          <Button
            size="large"
            block
            @click="handleCancelLogin"
          >
            {{ t('steamLogin.cancelButton') }}
          </Button>
        </div>
      </div>
    </Modal>
  </div>
</template>

<style scoped>
.slide-fade-enter-active {
  transition: all 0.3s ease-out;
}

.slide-fade-enter-from {
  opacity: 0;
  transform: translateY(10px);
}
</style>
