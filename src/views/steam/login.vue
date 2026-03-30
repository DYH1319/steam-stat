<script setup lang="ts">
import { Button, Checkbox, Empty, Input, InputPassword, Modal, Spin, Tabs, Tag } from 'ant-design-vue'
import { useI18n } from 'vue-i18n'
import { toast } from 'vue-sonner'
import dayjs from '@/utils/dayjs.ts'

const { t } = useI18n()
const electronApi = (window as Window).electron

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

// 已保存的 Token
const savedTokens = ref<SteamLoginToken[]>([])
const savedTokensLoading = ref(false)

// 已登录用户
const loggedInUsers = ref<string[]>([])
const loggedInUsersLoading = ref(false)

onMounted(() => {
  electronApi.steamLoginEventOnListener(onLoginEvent)
  fetchSavedTokens()
  fetchLoggedInUsers()
})

onUnmounted(() => {
  electronApi.steamLoginEventRemoveListener()
  if (isLoginLoading.value) {
    handleCancelLogin()
  }
})

// 获取已保存的 Token
async function fetchSavedTokens() {
  savedTokensLoading.value = true
  try {
    savedTokens.value = await electronApi.steamLoginSavedTokensGet()
  }
  catch (e: any) {
    console.error('Failed to fetch saved tokens:', e)
  }
  finally {
    savedTokensLoading.value = false
  }
}

// 获取已登录用户
async function fetchLoggedInUsers() {
  loggedInUsersLoading.value = true
  try {
    loggedInUsers.value = await electronApi.steamLoginLoggedInUsersGet()
  }
  catch (e: any) {
    console.error('Failed to fetch logged in users:', e)
  }
  finally {
    loggedInUsersLoading.value = false
  }
}

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
      fetchSavedTokens()
      fetchLoggedInUsers()
      // 重置状态
      setTimeout(() => {
        loginStatus.value = 'idle'
        qrImageBase64.value = ''
      }, 2000)
      break
    case 'error':
      loginStatus.value = 'idle'
      guardModal.visible = false
      deviceConfirmModal.visible = false
      qrImageBase64.value = ''
      toast.error(t('steamLogin.loginFailed', { error: event.data?.message || '' }))
      break
    case 'cancelled':
      loginStatus.value = 'idle'
      guardModal.visible = false
      deviceConfirmModal.visible = false
      qrImageBase64.value = ''
      toast.info(t('steamLogin.loginCancelled'))
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

  try {
    await electronApi.steamLoginCredentialsStart({
      username: credentialsForm.username.trim(),
      password: credentialsForm.password.trim(),
      rememberMe: credentialsForm.rememberMe,
    })
  }
  catch (e: any) {
    toast.error(t('steamLogin.loginFailed', { error: e?.message || e }))
    loginStatus.value = 'idle'
  }
}

// QR 码登录
async function handleQrLogin() {
  qrImageBase64.value = ''
  try {
    await electronApi.steamLoginQrStart({
      rememberMe: qrRememberMe.value,
    })
  }
  catch (e: any) {
    toast.error(t('steamLogin.loginFailed', { error: e?.message || e }))
    loginStatus.value = 'idle'
  }
}

// 提交 Guard 验证码
async function handleGuardCodeSubmit() {
  if (!guardModal.code.trim()) {
    return
  }
  try {
    await electronApi.steamLoginGuardCodeSubmit({ code: guardModal.code.trim() })
    guardModal.visible = false
    loginStatus.value = 'authenticating'
  }
  catch (e: any) {
    toast.error(e?.message || e)
  }
}

function handleSwitchToUseAppCode() {
  deviceConfirmModal.visible = false
  electronApi.steamLoginSwitchToUseCode()
}

function handleConfirmInApp() {
  deviceConfirmModal.visible = false
  electronApi.steamLoginConfirmDevice()
}

// 取消登录
function handleCancelLogin() {
  electronApi.steamLoginCancel()
  loginStatus.value = 'idle'
  qrImageBase64.value = ''
  guardModal.visible = false
  deviceConfirmModal.visible = false
}

// 退出已登录用户
function handleLogoutUser(accountName: string) {
  Modal.confirm({
    title: t('steamLogin.logoutConfirm', { accountName }),
    async onOk() {
      const success = await electronApi.steamLoginUserLogout({ accountName })
      if (success) {
        toast.success(t('steamLogin.logoutSuccess', { accountName }))
        fetchLoggedInUsers()
      }
      else {
        toast.error(t('steamLogin.logoutFailed'))
      }
    },
  })
}

// 使用已保存 Token 登录
async function handleTokenLogin(token: SteamLoginToken) {
  try {
    const result = await electronApi.steamLoginTokenStart({ tokenId: token.id })
    if (result.success) {
      toast.success(t('steamLogin.savedTokenLoginSuccess'))
    }
    else {
      toast.error(t('steamLogin.savedTokenLoginFailed', { error: result.error || '' }))
    }
  }
  catch (e: any) {
    toast.error(t('steamLogin.savedTokenLoginFailed', { error: e?.message || e }))
  }
}

// 删除已保存 Token
function handleTokenDelete(token: SteamLoginToken) {
  Modal.confirm({
    title: t('steamLogin.savedTokenDeleteConfirm', { accountName: token.accountName }),
    async onOk() {
      const success = await electronApi.steamLoginSavedTokenDelete({ id: token.id })
      if (success) {
        toast.success(t('steamLogin.savedTokenDeleteSuccess', { accountName: token.accountName }))
        fetchSavedTokens()
      }
    },
  })
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
    <FaPageMain class="mb-0">
      <div class="space-y-6">
        <!-- 登录卡片 -->
        <Transition name="slide-fade" appear>
          <div class="rounded-lg bg-[var(--g-container-bg)] p-6 shadow-lg">
            <div class="mb-4 flex items-center gap-2">
              <span class="i-tabler:brand-steam inline-block h-6 w-6" />
              <h3 class="text-xl font-bold">
                {{ t('steamLogin.title') }}
              </h3>
            </div>

            <Tabs v-model:active-key="activeTab" :disabled="isLoginLoading">
              <!-- 账号密码登录 -->
              <Tabs.TabPane key="credentials" :tab="t('steamLogin.credentialsTab')">
                <div class="mx-auto max-w-md py-4 space-y-4">
                  <div>
                    <label class="mb-1 block text-sm font-medium">{{ t('steamLogin.username') }}</label>
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
                    <label class="mb-1 block text-sm font-medium">{{ t('steamLogin.password') }}</label>
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

                  <div class="flex items-center gap-2">
                    <Checkbox v-model:checked="credentialsForm.rememberMe" :disabled="isLoginLoading">
                      {{ t('steamLogin.rememberMe') }}
                    </Checkbox>
                    <span class="text-xs op-50">{{ t('steamLogin.rememberMeDesc') }}</span>
                  </div>

                  <!-- 状态信息 -->
                  <div v-if="statusText && activeTab === 'credentials'" class="flex items-center gap-2 rounded-md bg-primary/10 px-3 py-2 text-sm">
                    <Spin class="flex items-center" :spinning="true" size="small" />
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
                <div class="mx-auto max-w-md py-4 space-y-4">
                  <div class="flex flex-col items-center gap-4">
                    <!-- QR 码展示区域 -->
                    <div class="min-h-64 w-64 flex items-center justify-center border border-border rounded-lg bg-white">
                      <img
                        v-if="qrImageBase64"
                        :src="qrImageBase64"
                        class="h-full w-full rounded-lg"
                        alt="QR Code"
                      >
                      <div v-else class="flex flex-col items-center gap-2 text-center op-50">
                        <span class="i-mdi:qrcode-scan inline-block h-12 w-12" />
                        <span class="text-sm">{{ t('steamLogin.qrCodeInstruction') }}</span>
                      </div>
                    </div>

                    <!-- 状态提示 -->
                    <div v-if="statusText && activeTab === 'qrCode'" class="flex items-center gap-2 rounded-md bg-primary/10 px-3 py-2 text-sm">
                      <Spin :spinning="true" size="small" />
                      <span>{{ statusText }}</span>
                    </div>
                    <p v-else class="text-center text-sm op-60">
                      {{ t('steamLogin.qrCodeInstruction') }}
                    </p>
                  </div>

                  <div class="flex items-center gap-2">
                    <Checkbox v-model:checked="qrRememberMe" :disabled="isLoginLoading">
                      {{ t('steamLogin.rememberMe') }}
                    </Checkbox>
                    <span class="text-xs op-50">{{ t('steamLogin.rememberMeDesc') }}</span>
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

                      danger block
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

        <!-- 已登录用户 -->
        <Transition name="slide-fade" appear>
          <Spin :spinning="loggedInUsersLoading">
            <div class="rounded-lg bg-[var(--g-container-bg)] p-6 shadow-lg">
              <div class="mb-4 flex items-center gap-2">
                <span class="i-mdi:account-multiple inline-block h-5 w-5" />
                <h3 class="text-lg font-bold">
                  {{ t('steamLogin.loggedInUsers') }}
                </h3>
              </div>

              <div v-if="loggedInUsers.length === 0" class="py-4">
                <Empty :description="t('steamLogin.noLoggedInUsers')" />
              </div>

              <div v-else class="space-y-3">
                <div
                  v-for="user in loggedInUsers"
                  :key="user"
                  class="flex items-center justify-between border border-border rounded-lg p-4 transition-colors hover:bg-muted/50"
                >
                  <div class="flex items-center gap-2">
                    <span class="i-mdi:account-circle inline-block h-5 w-5 op-60" />
                    <span class="font-medium">{{ user }}</span>
                    <Tag color="green">
                      Online
                    </Tag>
                  </div>

                  <Button
                    danger
                    size="small"
                    @click="handleLogoutUser(user)"
                  >
                    {{ t('steamLogin.logout') }}
                  </Button>
                </div>
              </div>
            </div>
          </Spin>
        </Transition>

        <!-- 已保存的登录 Token -->
        <Transition name="slide-fade" appear>
          <Spin :spinning="savedTokensLoading">
            <div class="rounded-lg bg-[var(--g-container-bg)] p-6 shadow-lg">
              <div class="mb-4 flex items-center gap-2">
                <span class="i-mdi:key inline-block h-5 w-5" />
                <h3 class="text-lg font-bold">
                  {{ t('steamLogin.savedTokens') }}
                </h3>
              </div>

              <div v-if="savedTokens.length === 0" class="py-4">
                <Empty :description="t('steamLogin.noSavedTokens')" />
              </div>

              <div v-else class="space-y-3">
                <div
                  v-for="token in savedTokens"
                  :key="token.id"
                  class="flex items-center justify-between border border-border rounded-lg p-4 transition-colors hover:bg-muted/50"
                >
                  <div class="flex flex-col gap-1">
                    <div class="flex items-center gap-2">
                      <span class="i-mdi:account-circle inline-block h-5 w-5 op-60" />
                      <span class="font-medium">{{ token.accountName }}</span>
                      <Tag color="green">
                        Token
                      </Tag>
                    </div>
                    <span class="ml-7 text-xs op-50">
                      {{ t('steamLogin.savedAt') }}: {{ dayjs.unix(token.createdAt).format('YYYY-MM-DD HH:mm:ss') }}
                    </span>
                  </div>

                  <div class="flex gap-2">
                    <Button
                      type="primary"
                      size="small"
                      :loading="isLoginLoading"
                      @click="handleTokenLogin(token)"
                    >
                      {{ t('steamLogin.savedTokenLogin') }}
                    </Button>
                    <Button
                      danger
                      size="small"
                      @click="handleTokenDelete(token)"
                    >
                      {{ t('steamLogin.savedTokenDelete') }}
                    </Button>
                  </div>
                </div>
              </div>
            </div>
          </Spin>
        </Transition>
      </div>
    </FaPageMain>

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

        <div v-if="guardModal.previousCodeWasIncorrect" class="rounded-md bg-red-500/10 px-3 py-2 text-sm text-red-500">
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

        <div class="flex justify-end gap-3">
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
