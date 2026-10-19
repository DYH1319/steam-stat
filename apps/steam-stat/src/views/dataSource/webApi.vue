<script setup lang="ts">
import { Button, Empty, InputNumber, Spin, Table, Tag } from 'ant-design-vue'
import { useI18n } from 'vue-i18n'
import { toast } from 'vue-sonner'
import { useDataSourceStore } from '@/store/modules/dataSource'
import dayjs from '@/utils/dayjs.ts'

const { t } = useI18n()
const dataSourceStore = useDataSourceStore()

const connectivityColorMap: Record<SteamOperationalStatus['connectivity'], string> = {
  online: 'green',
  degraded: 'orange',
  offline: 'red',
}

const healthColorMap: Record<SteamDependencyHealth['state'], string> = {
  unknown: 'default',
  healthy: 'green',
  degraded: 'orange',
  unavailable: 'red',
}

function dependencyLabel(name: string): string {
  const key = `dataSource.dependency.${name}`
  return t(key) === key ? name : t(key)
}

function formatUnixSeconds(value?: number | null): string {
  return typeof value === 'number' && value > 0
    ? dayjs.unix(value).format('YYYY-MM-DD HH:mm:ss')
    : '-'
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

const columns = computed(() => [
  { title: t('dataSource.webApi.columnDependency'), dataIndex: 'dependency', key: 'dependency' },
  { title: t('dataSource.webApi.columnStatus'), dataIndex: 'state', key: 'state' },
  { title: t('dataSource.webApi.columnCircuit'), dataIndex: 'isCircuitOpen', key: 'isCircuitOpen' },
  { title: t('dataSource.webApi.columnRateLimit'), dataIndex: 'isRateLimited', key: 'isRateLimited' },
  { title: t('dataSource.webApi.columnLastSuccess'), dataIndex: 'lastSuccessAt', key: 'lastSuccessAt' },
  { title: t('dataSource.webApi.columnLastFailure'), dataIndex: 'lastFailureAt', key: 'lastFailureAt' },
  { title: t('dataSource.webApi.columnFailureKind'), dataIndex: 'failureKind', key: 'failureKind' },
])
</script>

<template>
  <div>
    <FaPageMain class="mb-0">
      <AppPageHeader
        icon="i-mdi:web"
        :title="t('menu.dataSourceWebApi')"
        :description="t('dataSource.webApi.subtitle')"
      >
        <template #actions>
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
        <Transition name="slide-fade" appear>
          <Spin :spinning="dataSourceStore.loading.operational">
            <div class="p-6 rounded-lg bg-[var(--g-container-bg)] shadow-lg">
              <div class="mb-4 flex items-center justify-between">
                <h3 class="text-xl font-bold flex gap-2 items-center">
                  <span class="i-mdi:web-check h-6 w-6 inline-block" />
                  {{ t('dataSource.webApi.healthCard') }}
                </h3>
                <div class="flex gap-4 items-center">
                  <Tag
                    v-if="dataSourceStore.operationalStatus"
                    :color="connectivityColorMap[dataSourceStore.operationalStatus.connectivity]"
                    class="px-4 py-2 flex gap-2 items-center"
                  >
                    {{ t(`connectivity.${dataSourceStore.operationalStatus.connectivity}`) }}
                  </Tag>
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
                :data-source="dataSourceStore.operationalStatus.dependencies"
                :pagination="false"
                row-key="dependency"
              >
                <template #bodyCell="{ column, record }">
                  <template v-if="column.key === 'dependency'">
                    {{ dependencyLabel(record.dependency) }}
                  </template>
                  <template v-else-if="column.key === 'state'">
                    <Tag :color="healthColorMap[record.state as SteamDependencyHealth['state']]">
                      {{ t(`dataSource.health.${record.state}`) }}
                    </Tag>
                  </template>
                  <template v-else-if="column.key === 'isCircuitOpen'">
                    <Tag :color="record.isCircuitOpen ? 'red' : 'default'">
                      {{ record.isCircuitOpen ? t('dataSource.yes') : t('dataSource.no') }}
                    </Tag>
                  </template>
                  <template v-else-if="column.key === 'isRateLimited'">
                    <Tag :color="record.isRateLimited ? 'orange' : 'default'">
                      {{ record.isRateLimited ? t('dataSource.yes') : t('dataSource.no') }}
                    </Tag>
                  </template>
                  <template v-else-if="column.key === 'lastSuccessAt'">
                    {{ formatUnixSeconds(record.lastSuccessAt) }}
                  </template>
                  <template v-else-if="column.key === 'lastFailureAt'">
                    {{ formatUnixSeconds(record.lastFailureAt) }}
                  </template>
                  <template v-else-if="column.key === 'failureKind'">
                    {{ record.failureKind ?? '-' }}
                  </template>
                </template>
              </Table>

              <div v-else class="py-8">
                <Empty :description="t('common.unknown')" />
              </div>
            </div>
          </Spin>
        </Transition>
      </div>
    </FaPageMain>
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
