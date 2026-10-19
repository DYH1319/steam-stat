<script setup lang="ts">
import { Button, Empty, InputNumber, Modal, Spin, Table, Tag } from 'ant-design-vue'
import { useI18n } from 'vue-i18n'
import { toast } from 'vue-sonner'
import SteamLoginPanel from '@/components/SteamLoginPanel/index.vue'
import { useDataSourceStore } from '@/store/modules/dataSource'
import dayjs from '@/utils/dayjs.ts'
import { isExperimentalEnabled } from '@/utils/experimental'

const { t } = useI18n()
const dataSourceStore = useDataSourceStore()

const showLoginModal = ref(false)

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

// 弹窗销毁即取消未完成的登录并清空密码；关闭后同步会话状态
function onLoginModalClosed() {
  void dataSourceStore.refreshOperationalStatus().catch(() => undefined)
}

const columns = computed(() => [
  { title: t('dataSource.cm.columnAccount'), dataIndex: 'accountName', key: 'accountName' },
  { title: t('dataSource.cm.columnState'), dataIndex: 'state', key: 'state' },
  { title: t('dataSource.cm.columnReconnectAttempt'), dataIndex: 'reconnectAttempt', key: 'reconnectAttempt' },
  { title: t('dataSource.cm.columnGeneration'), dataIndex: 'generation', key: 'generation' },
  { title: t('dataSource.cm.columnErrorCode'), dataIndex: 'errorCode', key: 'errorCode' },
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

      <div class="space-y-6">
        <!-- Steam 会话卡片 -->
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
                </template>
              </Table>

              <div v-else class="py-8">
                <Empty :description="t('common.unknown')" />
              </div>
            </div>
          </Spin>
        </Transition>

        <!-- 需要重新认证的账号 -->
        <Transition name="slide-fade" appear>
          <div class="p-6 rounded-lg bg-[var(--g-container-bg)] shadow-lg">
            <div class="mb-4 flex items-center justify-between">
              <h3 class="text-xl font-bold flex gap-2 items-center">
                <span class="i-mdi:account-alert h-6 w-6 inline-block" />
                {{ t('dataSource.cm.reauthCard') }}
              </h3>
              <Button
                v-if="isExperimentalEnabled()"
                class="flex gap-1 items-center"
                @click="showLoginModal = true"
              >
                <template #icon>
                  <span class="i-mdi:login h-4 w-4" />
                </template>
                {{ t('dataSource.cm.loginButton') }}
              </Button>
            </div>

            <div v-if="dataSourceStore.operationalStatus?.reauthenticationAccounts.length" class="flex flex-wrap gap-2 items-center">
              <Tag
                v-for="account in dataSourceStore.operationalStatus.reauthenticationAccounts"
                :key="account"
                color="orange"
                class="px-4 py-2 flex gap-2 items-center"
              >
                <span class="i-mdi:account-alert h-4 w-4" />
                {{ account }}
              </Tag>
            </div>
            <div v-else class="py-8">
              <Empty :description="t('dataSource.cm.noReauthAccounts')" />
            </div>
          </div>
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
