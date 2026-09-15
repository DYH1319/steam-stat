<script setup lang="ts">
import { useVirtualList } from '@vueuse/core'
import { Alert, Button, Empty, Progress, Select, Spin, Tag } from 'ant-design-vue'
import { useI18n } from 'vue-i18n'
import { useAsyncResource } from '@/composables/useAsyncResource'
import { RendererIpcError, useIpc } from '@/composables/useIpc'
import { toAchievementViewModels, toOverviewViewModels } from '@/features/achievements/viewModel'
import { useSteamStore } from '@/store/modules/steam'
import dayjs from '@/utils/dayjs.ts'

const { t } = useI18n()
const ipc = useIpc()
const steamStore = useSteamStore()

const OVERVIEW_ITEM_HEIGHT = 76

const overview = useAsyncResource(
  (request: SteamAchievementOverviewRequest) => ipc.steamAchievementsOverviewGet(request),
)
const {
  data: overviewData,
  error: overviewError,
  status: overviewStatus,
  hasData: overviewHasData,
  isInitialLoading: overviewLoading,
  isRefreshing: overviewRefreshing,
} = overview

const detail = useAsyncResource(loadGameDetail)
const {
  data: detailData,
  error: detailError,
  hasData: detailHasData,
  isInitialLoading: detailLoading,
  isRefreshing: detailRefreshing,
} = detail

async function loadGameDetail(
  accountName: string,
  appId: number,
  forceRefresh: boolean,
): Promise<SteamAchievementGameResult> {
  const request: SteamAchievementGameRequest = { accountName, appId }
  const result = forceRefresh
    ? await ipc.steamAchievementsGameRefresh(request)
    : await ipc.steamAchievementsGameGet(request)
  if (forceRefresh && result.status === 'failure') {
    throw new RendererIpcError(t('achievements.refreshFailed'), result.failure)
  }
  return result
}

const selectedAppId = ref<number | null>(null)
const brokenIcons = ref<Set<string>>(new Set())

const overviewItems = computed(() => toOverviewViewModels(overviewData.value?.games ?? []))
const detailItems = computed(() =>
  detailData.value ? toAchievementViewModels(detailData.value.achievements) : [],
)
const detailProgress = computed(() => {
  const summary = detailData.value?.summary
  if (!summary || summary.total <= 0) {
    return 0
  }
  return Math.round(summary.percentage ?? (summary.unlocked / summary.total) * 100)
})

const connectivity = computed(() => steamStore.operationalStatus?.connectivity ?? null)
const connectivityColor = computed(() => {
  switch (connectivity.value) {
    case 'online':
      return 'green'
    case 'degraded':
      return 'orange'
    case 'offline':
      return 'red'
    default:
      return 'default'
  }
})
const reauthenticationCount = computed(() =>
  steamStore.operationalStatus?.reauthenticationAccounts.length ?? 0,
)

const { list: virtualGames, containerProps, wrapperProps } = useVirtualList(overviewItems, {
  itemHeight: OVERVIEW_ITEM_HEIGHT,
  overscan: 8,
})

onMounted(() => {
  steamStore.ensureBootstrapped().catch(() => {})
})

watch(
  () => steamStore.selectedAccountName,
  (accountName) => {
    overview.reset()
    detail.reset()
    selectedAppId.value = null
    brokenIcons.value = new Set()
    if (accountName) {
      void overview.execute({ accountName })
    }
  },
  { immediate: true },
)

function onAccountChange(value: unknown): void {
  steamStore.selectAccount(typeof value === 'string' ? value : null)
}

function retryBootstrap(): void {
  steamStore.ensureBootstrapped().catch(() => {})
}

function refreshOverview(): void {
  void overview.refresh()
}

function retryOverview(): void {
  void overview.retry()
}

function openGame(appId: number): void {
  const accountName = steamStore.selectedAccountName
  if (!accountName || (selectedAppId.value === appId && detailHasData.value)) {
    return
  }
  if (selectedAppId.value !== appId) {
    detail.reset()
    brokenIcons.value = new Set()
  }
  selectedAppId.value = appId
  void detail.execute(accountName, appId, false)
}

function refreshDetail(): void {
  const accountName = steamStore.selectedAccountName
  const appId = selectedAppId.value
  if (!accountName || appId === null || detailRefreshing.value) {
    return
  }
  void detail.execute(accountName, appId, true)
}

function retryDetail(): void {
  void detail.retry()
}

function failureKindLabel(kind?: string | null): string {
  return t(`achievements.failure.${kind ?? 'unknown'}`)
}

function freshnessLabel(value?: string | null): string {
  return value ? t(`achievements.freshness.${value}`) : t('common.unknown')
}

function sourceLabel(value?: string | null): string {
  return value ? t(`achievements.source.${value}`) : t('common.unknown')
}

function formatUnixSeconds(value?: number | null): string {
  return typeof value === 'number' && value > 0
    ? dayjs.unix(value).format('YYYY-MM-DD HH:mm:ss')
    : '-'
}

function formatUnlockDate(date: Date | null): string {
  return date ? dayjs(date).format('YYYY-MM-DD HH:mm') : ''
}

function achievementStateLabel(dto: SteamAchievement): string {
  if (dto.isUnlocked === true) {
    return t('achievements.unlocked')
  }
  if (dto.isUnlocked === false) {
    return t('achievements.locked')
  }
  return t('achievements.unknownState')
}

function achievementStateColor(dto: SteamAchievement): string {
  if (dto.isUnlocked === true) {
    return 'green'
  }
  if (dto.isUnlocked === false) {
    return 'default'
  }
  return 'orange'
}

function hasProgress(dto: SteamAchievement): boolean {
  return (dto.maxProgress ?? 0) > 0
}

function detailFailureDescription(result: SteamAchievementGameResult): string {
  const parts: string[] = []
  if (result.lastSuccessfulUpdate) {
    parts.push(t('achievements.cachedSnapshot', { time: formatUnixSeconds(result.lastSuccessfulUpdate) }))
  }
  if (result.diagnosticCode) {
    parts.push(`${t('achievements.diagnostic')}: ${result.diagnosticCode}`)
  }
  return parts.join(' · ')
}

function overviewFailureDescription(result: SteamAchievementOverviewResult): string {
  const parts: string[] = []
  parts.push(result.lastSuccessfulUpdate
    ? t('achievements.cachedSnapshot', { time: formatUnixSeconds(result.lastSuccessfulUpdate) })
    : t('achievements.overviewFailedOffline'))
  if (result.diagnosticCode) {
    parts.push(`${t('achievements.diagnostic')}: ${result.diagnosticCode}`)
  }
  return parts.join(' · ')
}

function onIconError(internalName: string): void {
  brokenIcons.value = new Set(brokenIcons.value).add(internalName)
}
</script>

<template>
  <FaPageMain
    :title="t('achievements.title')"
    :sub-title="t('achievements.subtitle')"
  >
    <div mb-4 flex flex-wrap items-center gap-3>
      <div flex items-center gap-2>
        <span text-sm op-70>{{ t('achievements.account') }}:</span>
        <Select
          :value="steamStore.selectedAccountName ?? undefined"
          :placeholder="t('achievements.selectAccount')"
          :disabled="steamStore.loggedInAccounts.length === 0"
          size="small"
          style="width: 200px;"
          @change="onAccountChange"
        >
          <Select.Option
            v-for="account in steamStore.loggedInAccounts"
            :key="account"
            :value="account"
          >
            {{ account }}
          </Select.Option>
        </Select>
      </div>
      <Tag v-if="connectivity" :color="connectivityColor" class="!me-0">
        {{ t(`connectivity.${connectivity}`) }}
      </Tag>
      <Tag v-if="reauthenticationCount > 0" color="orange" class="!me-0">
        {{ t('connectivity.reauthenticate', { count: reauthenticationCount }) }}
      </Tag>
      <Button
        type="primary"
        size="small"
        :loading="overviewRefreshing"
        :disabled="!steamStore.selectedAccountName"
        @click="refreshOverview"
      >
        <template #icon>
          <div i-mdi:refresh />
        </template>
        {{ t('achievements.refreshOverview') }}
      </Button>
    </div>

    <Alert
      v-if="steamStore.bootstrapStatus === 'error'"
      type="error"
      show-icon
      class="mb-4"
    >
      <template #message>
        {{ t('achievements.bootstrapFailed') }}
      </template>
      <template #description>
        {{ steamStore.bootstrapError?.message }}
        <Button size="small" class="ms-2" @click="retryBootstrap">
          {{ t('achievements.retry') }}
        </Button>
      </template>
    </Alert>

    <div
      v-if="steamStore.bootstrapStatus === 'loading' && steamStore.loggedInAccounts.length === 0"
      flex="~ items-center justify-center" py-20
    >
      <Spin :spinning="true" />
    </div>

    <div
      v-else-if="steamStore.bootstrapStatus === 'success' && steamStore.loggedInAccounts.length === 0"
      flex="~ items-center justify-center" py-20
    >
      <Empty :description="t('achievements.noAccounts')" />
    </div>

    <div v-else class="achievements-layout">
      <section min-w-0>
        <Alert
          v-if="overviewError && overviewHasData"
          type="warning"
          show-icon
          class="mb-3"
          :message="t('achievements.refreshFailed')"
          :description="overviewError?.message"
        />
        <Alert
          v-else-if="overviewError"
          type="error"
          show-icon
          class="mb-3"
        >
          <template #message>
            {{ t('achievements.overviewFailed') }}
          </template>
          <template #description>
            {{ overviewError?.message }}
            <Button size="small" class="ms-2" @click="retryOverview">
              {{ t('achievements.retry') }}
            </Button>
          </template>
        </Alert>
        <Alert
          v-else-if="overviewData?.failure"
          :type="overviewData.lastSuccessfulUpdate ? 'warning' : 'error'"
          show-icon
          class="mb-3"
          :message="failureKindLabel(overviewData.failure)"
          :description="overviewFailureDescription(overviewData)"
        />

        <Spin :spinning="overviewLoading">
          <template v-if="overviewItems.length > 0">
            <div mb-2 text-sm op-60>
              {{ t('achievements.gamesCount', { count: overviewItems.length }) }}
            </div>
            <div v-bind="containerProps" class="overview-scroll">
              <div v-bind="wrapperProps">
                <div
                  v-for="entry in virtualGames"
                  :key="entry.data.dto.appId"
                  class="overview-row"
                  data-testid="overview-row"
                  :class="{ 'is-active': entry.data.dto.appId === selectedAppId }"
                  @click="openGame(entry.data.dto.appId)"
                >
                  <div truncate font-medium :title="entry.data.displayName">
                    {{ entry.data.displayName }}
                  </div>
                  <div flex items-center gap-2 text-xs op-70>
                    <template v-if="entry.data.hasAchievements && entry.data.dto.progress">
                      <Progress
                        :percent="Math.round(entry.data.dto.progress?.percentage ?? 0)"
                        size="small"
                        :show-info="false"
                        style="width: 120px;"
                      />
                      <span flex-shrink-0>
                        {{ entry.data.dto.progress?.unlocked }} / {{ entry.data.dto.progress?.total }}
                      </span>
                    </template>
                    <span v-else op-60>{{ t('achievements.noAchievementsShort') }}</span>
                    <span ms-auto flex-shrink-0 op-60>
                      {{ entry.data.dto.lastPlayedAt > 0
                        ? formatUnixSeconds(entry.data.dto.lastPlayedAt)
                        : t('achievements.neverPlayed') }}
                    </span>
                  </div>
                </div>
              </div>
            </div>
          </template>
          <div
            v-else-if="overviewStatus === 'success' && overviewData?.failure"
            flex="~ items-center justify-center" py-16
          >
            <Empty :description="t('achievements.overviewFailedOffline')" />
          </div>
          <div
            v-else-if="overviewStatus === 'success'"
            flex="~ items-center justify-center" py-16
          >
            <Empty :description="t('achievements.noGames')" />
          </div>
          <div
            v-else-if="overviewStatus === 'idle' && !overviewError"
            flex="~ items-center justify-center" py-16
          >
            <Empty :description="t('achievements.selectAccount')" />
          </div>
        </Spin>
      </section>

      <section min-w-0 class="detail-pane">
        <div
          v-if="selectedAppId === null"
          flex="~ items-center justify-center" py-20
        >
          <Empty :description="t('achievements.selectGame')" />
        </div>
        <template v-else>
          <Alert
            v-if="detailError && detailHasData"
            type="warning"
            show-icon
            class="mb-3"
            :message="t('achievements.refreshFailed')"
            :description="detailError?.message"
          />
          <Spin :spinning="detailLoading">
            <template v-if="detailError && !detailHasData">
              <Alert type="error" show-icon>
                <template #message>
                  {{ t('achievements.detailFailed') }}
                </template>
                <template #description>
                  {{ detailError?.message }}
                  <Button size="small" class="ms-2" @click="retryDetail">
                    {{ t('achievements.retry') }}
                  </Button>
                </template>
              </Alert>
            </template>
            <template v-else-if="detailData">
              <div mb-3 flex flex-wrap items-center gap-2>
                <div min-w-0 flex-1>
                  <div truncate text-base font-semibold :title="detailData.appName">
                    {{ detailData.appName }}
                  </div>
                  <div text-xs op-60>
                    {{ t('library.appId') }}: {{ detailData.appId }}
                  </div>
                </div>
                <Tag v-if="detailData.stale" color="orange" class="!me-0">
                  {{ t('achievements.stale') }}
                </Tag>
                <Tag v-if="detailData.freshness" class="!me-0">
                  {{ freshnessLabel(detailData.freshness) }}
                </Tag>
                <Tag v-if="detailData.source" class="!me-0">
                  {{ sourceLabel(detailData.source) }}
                </Tag>
                <Tag v-if="detailData.partial" color="purple" class="!me-0">
                  {{ t('achievements.partialData') }}
                </Tag>
                <Button
                  size="small"
                  data-testid="refresh-detail"
                  :loading="detailRefreshing"
                  @click="refreshDetail"
                >
                  <template #icon>
                    <div i-mdi:refresh />
                  </template>
                  {{ t('achievements.refreshDetail') }}
                </Button>
              </div>
              <div v-if="detailData.lastSuccessfulUpdate" mb-2 text-xs op-60>
                {{ t('achievements.lastSuccessfulUpdate') }}:
                {{ formatUnixSeconds(detailData.lastSuccessfulUpdate) }}
              </div>
              <div
                v-if="detailData.progressState
                  && (detailData.progressState.failure || detailData.progressState.freshness || detailData.progressState.source)"
                mb-3 text-xs op-60
              >
                {{ t('achievements.progressSource') }}:
                {{ sourceLabel(detailData.progressState.source) }}
                · {{ freshnessLabel(detailData.progressState.freshness) }}
                <template v-if="detailData.progressState.failure">
                  · {{ failureKindLabel(detailData.progressState.failure) }}
                </template>
                <template v-if="detailData.progressState.diagnosticCode">
                  · {{ detailData.progressState.diagnosticCode }}
                </template>
              </div>

              <Alert
                v-if="detailData.status === 'failure' || detailData.failure"
                type="error"
                show-icon
                class="mb-3"
                :message="failureKindLabel(detailData.failure)"
                :description="detailFailureDescription(detailData)"
              />

              <template v-else>
                <div mb-3 flex flex-wrap items-center gap-3>
                  <Progress
                    :percent="detailProgress"
                    size="small"
                    :show-info="false"
                    style="width: 160px;"
                  />
                  <span text-sm>
                    {{ detailData.summary.unlocked }} / {{ detailData.summary.total }}
                  </span>
                  <span v-if="detailData.summary.unknown > 0" text-xs op-60>
                    {{ t('achievements.unknownProgressCount', { count: detailData.summary.unknown }) }}
                  </span>
                </div>
                <div
                  v-if="detailItems.length === 0"
                  flex="~ items-center justify-center" py-12
                >
                  <Empty
                    :description="detailData.achievements.length === 0
                      ? t('achievements.noAchievements')
                      : t('achievements.allHidden')"
                  />
                </div>
                <div v-else class="achievement-list">
                  <div
                    v-for="model in detailItems"
                    :key="model.dto.internalName"
                    class="achievement-row"
                    flex items-center gap-3
                  >
                    <img
                      v-if="model.iconUrl && !brokenIcons.has(model.dto.internalName)"
                      :src="model.iconUrl"
                      :alt="model.displayName"
                      class="achievement-icon"
                      loading="lazy"
                      @error="onIconError(model.dto.internalName)"
                    >
                    <div v-else class="achievement-icon achievement-icon-placeholder" flex="~ items-center justify-center">
                      <div i-mdi:trophy-outline h-6 w-6 op-40 />
                    </div>
                    <div min-w-0 flex-1>
                      <div flex items-center gap-2>
                        <span truncate font-medium :title="model.displayName">
                          {{ model.displayName }}
                        </span>
                        <Tag v-if="model.dto.hidden" class="flex-shrink-0 !me-0">
                          {{ t('achievements.hidden') }}
                        </Tag>
                      </div>
                      <div v-if="model.description" line-clamp-2 text-xs op-60>
                        {{ model.description }}
                      </div>
                      <div mt-1 flex flex-wrap items-center gap-x-2 text-xs op-60>
                        <span v-if="model.unlockDate">
                          {{ t('achievements.unlockedAt', { time: formatUnlockDate(model.unlockDate) }) }}
                        </span>
                        <span v-else-if="model.dto.isUnlocked === false && hasProgress(model.dto)">
                          {{ t('achievements.progressValue', {
                            current: model.dto.minProgress ?? 0,
                            total: model.dto.maxProgress ?? 0,
                          }) }}
                        </span>
                        <span v-if="model.dto.globalUnlockedPercent != null">
                          {{ t('achievements.globalUnlocked', { percent: model.dto.globalUnlockedPercent.toFixed(1) }) }}
                        </span>
                      </div>
                    </div>
                    <Tag :color="achievementStateColor(model.dto)" class="flex-shrink-0 !me-0">
                      {{ achievementStateLabel(model.dto) }}
                    </Tag>
                  </div>
                </div>
              </template>
            </template>
            <div
              v-else-if="!detailLoading"
              flex="~ items-center justify-center" py-12
            >
              <Empty :description="t('achievements.selectGame')" />
            </div>
          </Spin>
        </template>
      </section>
    </div>
  </FaPageMain>
</template>

<style scoped>
.achievements-layout {
  display: grid;
  grid-template-columns: minmax(0, 2fr) minmax(0, 3fr);
  gap: 16px;
}

@media (width <= 1024px) {
  .achievements-layout {
    grid-template-columns: minmax(0, 1fr);
  }
}

.overview-scroll {
  height: 560px;
  overflow-y: auto;
}

.overview-row {
  box-sizing: border-box;
  display: flex;
  flex-direction: column;
  justify-content: center;
  height: 76px;
  padding: 8px 12px;
  cursor: pointer;
  border-radius: var(--radius);
  transition: background-color 0.15s ease;
}

.overview-row:hover {
  background-color: hsl(var(--accent));
}

.overview-row.is-active {
  background-color: hsl(var(--accent));
}

.achievement-list {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.achievement-row {
  padding: 8px 12px;
  border: 1px solid hsl(var(--border));
  border-radius: var(--radius);
}

.achievement-icon {
  flex-shrink: 0;
  width: 40px;
  height: 40px;
  object-fit: cover;
  border-radius: var(--radius);
}

.achievement-icon-placeholder {
  background-color: hsl(var(--muted));
}
</style>
