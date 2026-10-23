<script setup lang="ts">
import { Alert, Button, Empty, InputNumber, Modal, Select, Spin, Table, Tag } from 'ant-design-vue'
import { useI18n } from 'vue-i18n'
import { toast } from 'vue-sonner'
import SteamLoginPanel from '@/components/SteamLoginPanel/index.vue'
import { useAsyncResource } from '@/composables/useAsyncResource'
import { useIpc } from '@/composables/useIpc'
import { useDataSourceStore } from '@/store/modules/dataSource'
import { useSteamStore } from '@/store/modules/steam'
import dayjs from '@/utils/dayjs.ts'
import { isExperimentalEnabled } from '@/utils/experimental'
import { isSteamLoginTokenExpired, localizeSteamLoginError, steamPersonaStateOptions } from '@/utils/steamLogin'

const { t } = useI18n()
const ipc = useIpc()
const dataSourceStore = useDataSourceStore()
const steamStore = useSteamStore()

const showLoginModal = ref(false)

// 已保存的登录 Token
const savedTokensResource = useAsyncResource(() => ipc.steamLoginSavedTokensGet())
const savedTokens = computed(() => savedTokensResource.data.value ?? [])

const personaStateOptions = steamPersonaStateOptions()

const sessionStateColorMap: Record<SteamSessionStatus['state'], string> = {
  disconnected: 'default',
  connecting: 'blue',
  authenticating: 'blue',
  ready: 'green',
  reconnectWaiting: 'orange',
  reconnecting: 'blue',
  reauthenticationRequired: 'orange',
  failed: 'red',
  stopping: 'orange',
}

onMounted(() => {
  steamStore.ensureBootstrapped().catch(() => {})
  void savedTokensResource.execute()
})

function retryBootstrap() {
  steamStore.ensureBootstrapped().catch(() => {})
}

async function onRefreshOperational() {
  try {
    await dataSourceStore.refreshOperationalStatus()
    toast.success(t('dataSource.refreshSuccess'))
  }
  catch (e: any) {
    toast.error(`${t('common.getFailed')}: ${e?.message || e}`)
  }
}

async function onRefreshAll() {
  const results = await dataSourceStore.refreshAll()
  if (results.every(result => result.status === 'fulfilled')) {
    toast.success(t('dataSource.refreshAllSuccess'))
  }
  else {
    const failed = results.find((result): result is PromiseRejectedResult => result.status === 'rejected')
    toast.error(`${t('common.getFailed')}: ${(failed?.reason as Error)?.message || failed?.reason}`)
  }
}

// 弹窗销毁即取消未完成的登录并清空密码；关闭后同步会话、账号与凭据状态
function onLoginModalClosed() {
  void dataSourceStore.refreshOperationalStatus().catch(() => undefined)
  void steamStore.refreshAccounts().catch(() => undefined)
  void savedTokensResource.execute()
}

// 会话表格中已登录账号的操作列
function isLoggedIn(accountName: string): boolean {
  return steamStore.loggedInAccounts.includes(accountName)
}

// 退出已登录用户
function handleLogoutUser(accountName: string) {
  Modal.confirm({
    title: t('steamLogin.logoutConfirm', { accountName }),
    async onOk() {
      const success = await ipc.steamLoginUserLogout({ accountName })
      if (success) {
        toast.success(t('steamLogin.logoutSuccess', { accountName }))
        steamStore.removeAccount(accountName)
        void dataSourceStore.refreshOperationalStatus().catch(() => undefined)
      }
      else {
        toast.error(t('steamLogin.logoutFailed'))
      }
    },
  })
}

// 设置用户 Persona 状态
async function handleSetPersonaState(accountName: string, personaState: number) {
  try {
    const success = await ipc.steamLoginUserSetPersonaState({ accountName, personaState })
    if (success) {
      toast.success(t('steamLogin.setPersonaState'))
    }
  }
  catch (e: any) {
    console.error('Failed to set persona state:', e)
  }
}

// 使用已保存 Token 登录
async function handleTokenLogin(token: SteamLoginToken) {
  try {
    const result = await ipc.steamLoginTokenStart({ tokenId: token.id })
    if (result.success) {
      toast.success(t('steamLogin.savedTokenLoginSuccess'))
    }
    else {
      toast.error(t('steamLogin.savedTokenLoginFailed', { error: localizeSteamLoginError(result.errorCode ?? undefined, result.error ?? undefined) }))
    }
  }
  catch (e: any) {
    toast.error(t('steamLogin.savedTokenLoginFailed', { error: localizeSteamLoginError(undefined, e?.message || String(e)) }))
  }
}

// 删除已保存 Token
function handleTokenDelete(token: SteamLoginToken) {
  Modal.confirm({
    title: t('steamLogin.savedTokenDeleteConfirm', { accountName: token.accountName }),
    async onOk() {
      const success = await ipc.steamLoginSavedTokenDelete({ id: token.id })
      if (success) {
        toast.success(t('steamLogin.savedTokenDeleteSuccess', { accountName: token.accountName }))
        void savedTokensResource.execute()
      }
    },
  })
}

// 已保存的登录与需要重新认证的账号的合并行
interface LoginTokenRow {
  accountName: string
  token: SteamLoginToken | null
  needsReauth: boolean
}

const loginTokenRows = computed<LoginTokenRow[]>(() => {
  const reauth = new Set(dataSourceStore.operationalStatus?.reauthenticationAccounts ?? [])
  const rows: LoginTokenRow[] = isExperimentalEnabled()
    ? savedTokens.value.map(token => ({
        accountName: token.accountName,
        token,
        needsReauth: reauth.has(token.accountName),
      }))
    : []
  const hasRow = new Set(rows.map(row => row.accountName))
  for (const accountName of reauth) {
    if (!hasRow.has(accountName)) {
      rows.push({ accountName, token: null, needsReauth: true })
    }
  }
  return rows
})

const columns = computed(() => [
  { title: t('dataSource.cm.columnAccount'), dataIndex: 'accountName', key: 'accountName' },
  { title: t('dataSource.cm.columnState'), dataIndex: 'state', key: 'state' },
  { title: t('dataSource.cm.columnReconnectAttempt'), dataIndex: 'reconnectAttempt', key: 'reconnectAttempt' },
  { title: t('dataSource.cm.columnGeneration'), dataIndex: 'generation', key: 'generation' },
  { title: t('dataSource.cm.columnErrorCode'), dataIndex: 'errorCode', key: 'errorCode' },
  { title: t('dataSource.cm.columnActions'), key: 'actions' },
])
</script>

<template>
  <div>
    <FaPageMain class="mb-0">
      <AppPageHeader
        icon="i-tabler:brand-steam"
        :title="t('menu.dataSourceCm')"
        :description="t('dataSource.cm.subtitle')"
      >
        <template #actions>
          <Button
            v-if="isExperimentalEnabled()"
            class="flex gap-1 items-center"
            data-testid="open-steam-login"
            @click="showLoginModal = true"
          >
            <template #icon>
              <span class="i-mdi:login h-4 w-4" />
            </template>
            {{ t('dataSource.cm.loginButton') }}
          </Button>
          <div class="flex gap-2 items-center">
            <span class="text-sm op-70">{{ t('dataSource.pollInterval') }}</span>
            <InputNumber
              :value="dataSourceStore.pollIntervalSeconds"
              :min="5"
              :max="600"
              :step="1"
              class="w-24"
              @change="(value) => dataSourceStore.setPollInterval(Number(value) || 5)"
            />
          </div>
          <Button type="primary" class="flex gap-1 items-center" @click="onRefreshAll">
            <template #icon>
              <span class="i-mdi:refresh h-4 w-4" />
            </template>
            {{ t('dataSource.refreshAll') }}
          </Button>
        </template>
      </AppPageHeader>

      <Alert
        v-if="steamStore.bootstrapStatus === 'error'"
        type="error"
        show-icon
        class="mb-6"
      >
        <template #message>
          {{ t('steamLogin.bootstrapFailed') }}
        </template>
        <template #description>
          {{ steamStore.bootstrapError?.message }}
          <Button class="ms-2" @click="retryBootstrap">
            {{ t('common.retry') }}
          </Button>
        </template>
      </Alert>

      <div class="space-y-6">
        <!-- Steam 会话卡片：会话列表与已登录用户合并显示 -->
        <Transition name="slide-fade" appear>
          <Spin :spinning="dataSourceStore.loading.operational">
            <div class="p-6 rounded-lg bg-[var(--g-container-bg)] shadow-lg">
              <div class="mb-4 flex items-center justify-between">
                <h3 class="text-xl font-bold flex gap-2 items-center">
                  <span class="i-tabler:brand-steam h-6 w-6 inline-block" />
                  {{ t('dataSource.cm.sessionsCard') }}
                </h3>
                <div class="flex gap-4 items-center">
                  <span v-if="dataSourceStore.lastRefreshedAt.operational" class="text-xs text-gray-500">
                    {{ t('common.lastRefresh') }}: {{ dayjs(dataSourceStore.lastRefreshedAt.operational).format('YYYY-MM-DD HH:mm:ss') }}
                  </span>
                  <Button
                    type="primary"
                    :loading="dataSourceStore.loading.operational"
                    class="flex gap-1 items-center"
                    @click="onRefreshOperational"
                  >
                    <template #icon>
                      <span class="i-mdi:refresh h-4 w-4" />
                    </template>
                    {{ t('common.refresh') }}
                  </Button>
                </div>
              </div>

              <Table
                v-if="dataSourceStore.operationalStatus"
                :columns="columns"
                :data-source="dataSourceStore.operationalStatus.sessions"
                :pagination="false"
                row-key="accountName"
              >
                <template #bodyCell="{ column, record }">
                  <template v-if="column.key === 'state'">
                    <Tag :color="sessionStateColorMap[record.state as SteamSessionStatus['state']]">
                      {{ t(`dataSource.sessionState.${record.state}`) }}
                    </Tag>
                  </template>
                  <template v-else-if="column.key === 'errorCode'">
                    {{ record.errorCode ?? '-' }}
                  </template>
                  <template v-else-if="column.key === 'actions'">
                    <div v-if="isExperimentalEnabled() && isLoggedIn(record.accountName)" class="flex gap-2 items-center">
                      <Select
                        :default-value="6"
                        style="width: 140px;"
                        :options="personaStateOptions"
                        @change="(value) => handleSetPersonaState(record.accountName, value as number)"
                      />
                      <Button
                        danger
                        @click="handleLogoutUser(record.accountName)"
                      >
                        {{ t('steamLogin.logout') }}
                      </Button>
                    </div>
                    <span v-else>-</span>
                  </template>
                </template>
              </Table>

              <div v-else class="py-8">
                <Empty :description="t('common.unknown')" />
              </div>
            </div>
          </Spin>
        </Transition>

        <!-- 已保存的登录与需要重新认证的账号 -->
        <Transition name="slide-fade" appear>
          <Spin :spinning="savedTokensResource.isInitialLoading.value">
            <div class="p-6 rounded-lg bg-[var(--g-container-bg)] shadow-lg">
              <div class="mb-4 flex items-center justify-between">
                <h3 class="text-xl font-bold flex gap-2 items-center">
                  <span class="i-mdi:account-alert h-6 w-6 inline-block" />
                  {{ t('dataSource.cm.tokensCard') }}
                </h3>
                <Button
                  :loading="savedTokensResource.isInitialLoading.value"
                  class="flex gap-1 items-center"
                  @click="savedTokensResource.execute()"
                >
                  <template #icon>
                    <span class="i-mdi:refresh h-4 w-4" />
                  </template>
                  {{ t('common.refresh') }}
                </Button>
              </div>

              <div v-if="loginTokenRows.length" class="space-y-3">
                <div
                  v-for="row in loginTokenRows"
                  :key="row.accountName"
                  class="p-4 border border-border rounded-lg flex transition-colors items-center justify-between hover:bg-muted/50"
                >
                  <div class="flex flex-col gap-1">
                    <div class="flex gap-2 items-center">
                      <span class="i-mdi:account-circle op-60 h-5 w-5 inline-block" />
                      <span class="font-medium">{{ row.accountName }}</span>
                      <Tag v-if="row.needsReauth" color="orange">
                        {{ t('dataSource.sessionState.reauthenticationRequired') }}
                      </Tag>
                      <template v-if="row.token">
                        <Tag v-if="isSteamLoginTokenExpired(row.token)" color="red">
                          {{ t('steamLogin.tokenExpired') }}
                        </Tag>
                        <Tag v-else color="green">
                          Token
                        </Tag>
                      </template>
                    </div>
                    <span v-if="row.token" class="text-xs ml-7 op-50">
                      {{ t('steamLogin.savedAt') }}: {{ dayjs.unix(row.token.createdAt).format('YYYY-MM-DD HH:mm:ss') }}
                    </span>
                    <span v-if="row.token && isSteamLoginTokenExpired(row.token)" class="text-xs text-red-500 ml-7">
                      {{ t('steamLogin.tokenExpiredDesc') }}
                    </span>
                  </div>

                  <div class="flex gap-2">
                    <template v-if="row.token">
                      <Button
                        type="primary"
                        :disabled="isSteamLoginTokenExpired(row.token)"
                        @click="handleTokenLogin(row.token)"
                      >
                        {{ t('steamLogin.savedTokenLogin') }}
                      </Button>
                      <Button
                        danger
                        @click="handleTokenDelete(row.token)"
                      >
                        {{ t('steamLogin.savedTokenDelete') }}
                      </Button>
                    </template>
                    <Button
                      v-else-if="isExperimentalEnabled()"
                      type="primary"
                      @click="showLoginModal = true"
                    >
                      {{ t('dataSource.cm.loginButton') }}
                    </Button>
                  </div>
                </div>
              </div>
              <div v-else class="py-8">
                <Empty :description="t('dataSource.cm.noAccounts')" />
              </div>
            </div>
          </Spin>
        </Transition>
      </div>
    </FaPageMain>

    <!-- Steam 登录弹窗：关闭即销毁面板，取消进行中的登录并清空密码 -->
    <Modal
      v-model:open="showLoginModal"
      :title="t('dataSource.cm.loginButton')"
      :footer="null"
      :width="760"
      destroy-on-close
      @after-close="onLoginModalClosed"
    >
      <SteamLoginPanel />
    </Modal>
  </div>
</template>

<style scoped>
.slide-fade-enter-active {
  transition: all 0.3s ease;
}

.slide-fade-enter-from {
  opacity: 0;
  transform: translateY(-10px);
}
</style>
