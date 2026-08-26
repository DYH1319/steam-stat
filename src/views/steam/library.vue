<script setup lang="ts">
import type { Key } from 'ant-design-vue/es/_util/type'
import { Button, Empty, Progress, Select, Spin, Tabs, Tag, Tooltip } from 'ant-design-vue'
import { useI18n } from 'vue-i18n'
import { toast } from 'vue-sonner'
import dayjs from '@/utils/dayjs.ts'

const { t } = useI18n()
const electronApi = (window as Window).electron

type ViewMode = 'cover' | 'list'
type SortBy = 'name' | 'playtime' | 'lastPlayed' | 'appId' | 'achievements'
type LibraryScope = 'all' | 'own' | 'family' | 'wishlist'

const loggedInUsers = ref<string[]>([])
const libraryData = ref<Record<string, SteamOwnedGame[]>>({})
const loading = ref<{ initial: boolean, sync: boolean }>({ initial: false, sync: false })
const activeTab = ref<string>('')
const viewMode = ref<ViewMode>('cover')
const sortBy = ref<SortBy>('playtime')
const libraryScope = ref<LibraryScope>('all')

// 按筛选范围过滤后的游戏列表
const filteredGames = computed(() => {
  const currentGames = activeTab.value ? libraryData.value[activeTab.value] || [] : []
  switch (libraryScope.value) {
    case 'own':
      return currentGames.filter(g => g.isOwned)
    case 'family':
      return currentGames.filter(g => g.isFamilyShared)
    case 'wishlist':
      return currentGames.filter(g => g.isInWishlist)
    default:
      return currentGames.filter(g => g.isOwned || g.isFamilyShared)
  }
})

const stats = computed(() => {
  const games = filteredGames.value
  const totalPlaytime = games.reduce((sum, game) => sum + game.playtimeForever, 0)

  return {
    totalGames: games.length,
    totalPlaytimeHours: Math.floor(totalPlaytime / 60),
    totalPlaytimeMinutes: totalPlaytime % 60,
  }
})

const sortedGames = computed(() => {
  const games = [...filteredGames.value]

  switch (sortBy.value) {
    case 'name':
      return games.sort((a, b) => displayName(a).localeCompare(displayName(b)))
    case 'playtime':
      return games.sort((a, b) => b.playtimeForever - a.playtimeForever)
    case 'lastPlayed':
      return games.sort((a, b) => b.rtimeLastPlayed - a.rtimeLastPlayed)
    case 'appId':
      return games.sort((a, b) => a.appId - b.appId)
    case 'achievements':
      return games.sort((a, b) => {
        // 无成就的排在最后
        if ((a.achievementTotal > 0) !== (b.achievementTotal > 0)) {
          return a.achievementTotal > 0 ? -1 : 1
        }
        return b.achievementPercentage - a.achievementPercentage
      })
    default:
      return games
  }
})

// 展示名称（优先本地化名称）
function displayName(game: SteamOwnedGame): string {
  return game.nameLocalized || game.name || `App ${game.appId}`
}

// 英文名称与本地化名称不同时返回英文名称（用于副标题展示）
function englishName(game: SteamOwnedGame): string {
  return game.name && game.name !== game.nameLocalized ? game.name : ''
}

function getGameCoverUrl(appId: number) {
  return `https://steamcdn-a.akamaihd.net/steam/apps/${appId}/library_600x900.jpg`
}

function getGameHeaderUrl(appId: number) {
  return `https://steamcdn-a.akamaihd.net/steam/apps/${appId}/header.jpg`
}

function formatPlaytime(minutes: number) {
  if (minutes === 0) {
    return t('library.neverPlayed')
  }
  const hours = Math.floor(minutes / 60)
  const mins = minutes % 60
  if (hours > 0) {
    return `${hours} ${t('library.hours')} ${mins} ${t('library.minutes')}`
  }
  return `${mins} ${t('library.minutes')}`
}

function formatLastPlayed(timestamp: number) {
  if (timestamp === 0) {
    return t('library.neverPlayed')
  }
  return dayjs.unix(timestamp).format('YYYY-MM-DD HH:mm')
}

// 家庭拥有者的展示文本
function formatOwners(game: SteamOwnedGame): string {
  if (game.ownerNames.length > 0) {
    return game.ownerNames.join(', ')
  }
  return game.ownerSteamIds.join(', ')
}

onMounted(async () => {
  await fetchLibraryData(false)
})

async function fetchLibraryData(isSync: boolean) {
  if (isSync) {
    loading.value.sync = true
  }
  else {
    loading.value.initial = true
  }

  try {
    loggedInUsers.value = await electronApi.steamLoginLoggedInUsersGet()

    if (loggedInUsers.value.length === 0) {
      toast.error(t('library.noLoggedInUsers'))
      return
    }

    if (isSync) {
      const results = await electronApi.steamLibrarySyncForAllUsers()
      const failedUsers = Object.entries(results).filter(([_, success]) => !success).map(([user, _]) => user)

      if (failedUsers.length > 0) {
        toast.error(`${t('library.syncFailed')}: ${failedUsers.join(', ')}`)
      }
      else {
        toast.success(t('library.syncSuccess'))
      }
    }

    libraryData.value = await electronApi.steamLibraryGetForAllUsers()

    if (!activeTab.value && loggedInUsers.value.length > 0) {
      activeTab.value = loggedInUsers.value[0]
    }

    if (!isSync) {
      toast.success(t('library.getSuccess'))
    }
  }
  catch (error: any) {
    console.error('Failed to fetch library data:', error)
    toast.error(`${t('common.getFailed')}: ${error?.message || error}`)
  }
  finally {
    loading.value.initial = false
    loading.value.sync = false
  }
}

async function handleSync() {
  await fetchLibraryData(true)
}

function handleTabChange(key: Key) {
  activeTab.value = String(key)
}

function handleViewModeChange(mode: ViewMode) {
  viewMode.value = mode
}
</script>

<template>
  <FaPageMain
    :title="t('library.title')"
    :sub-title="t('library.subtitle')"
  >
    <template #header-extra>
      <div flex flex-wrap items-center gap-3>
        <div flex items-center gap-2>
          <span text-sm op-70>{{ t('library.scope') }}:</span>
          <Select
            v-model:value="libraryScope"
            size="small"
            style="width: 140px;"
          >
            <Select.Option value="all">
              {{ t('library.scopeAll') }}
            </Select.Option>
            <Select.Option value="own">
              {{ t('library.scopeOwn') }}
            </Select.Option>
            <Select.Option value="family">
              {{ t('library.scopeFamily') }}
            </Select.Option>
            <Select.Option value="wishlist">
              {{ t('library.scopeWishlist') }}
            </Select.Option>
          </Select>
        </div>

        <div flex items-center gap-2>
          <span text-sm op-70>{{ t('library.viewMode') }}:</span>
          <Button
            :type="viewMode === 'cover' ? 'primary' : 'default'"
            size="small"
            @click="handleViewModeChange('cover')"
          >
            <template #icon>
              <div i-mdi:view-grid />
            </template>
            {{ t('library.coverView') }}
          </Button>
          <Button
            :type="viewMode === 'list' ? 'primary' : 'default'"
            size="small"
            @click="handleViewModeChange('list')"
          >
            <template #icon>
              <div i-mdi:view-list />
            </template>
            {{ t('library.listView') }}
          </Button>
        </div>

        <div flex="~ items-center gap-2">
          <span text-sm op-70>{{ t('library.sortBy') }}:</span>
          <Select
            v-model:value="sortBy"
            size="small"
            style="width: 150px;"
          >
            <Select.Option value="playtime">
              {{ t('library.sortByPlaytime') }}
            </Select.Option>
            <Select.Option value="name">
              {{ t('library.sortByName') }}
            </Select.Option>
            <Select.Option value="achievements">
              {{ t('library.sortByAchievements') }}
            </Select.Option>
            <Select.Option value="lastPlayed">
              {{ t('library.sortByLastPlayed') }}
            </Select.Option>
            <Select.Option value="appId">
              {{ t('library.sortByAppId') }}
            </Select.Option>
          </Select>
        </div>

        <Button
          type="primary"
          :loading="loading.sync"
          @click="handleSync"
        >
          <template #icon>
            <div i-mdi:refresh />
          </template>
          {{ loading.sync ? t('library.syncing') : t('library.syncLibrary') }}
        </Button>
      </div>
    </template>

    <Spin :spinning="loading.initial">
      <div v-if="loggedInUsers.length === 0" flex="~ items-center justify-center" py-20>
        <Empty :description="t('library.noLoggedInUsers')" />
      </div>

      <Tabs
        v-else
        v-model:active-key="activeTab"
        type="card"
        @change="handleTabChange"
      >
        <Tabs.TabPane
          v-for="user in loggedInUsers"
          :key="user"
          :tab="user"
        >
          <div mb-4 flex="~ items-center justify-between">
            <div flex="~ items-center gap-6" text-sm>
              <div flex="~ items-center gap-2">
                <span op-70>{{ t('library.totalGames') }}:</span>
                <span text-base font-semibold>{{ stats.totalGames }}</span>
              </div>
              <div flex="~ items-center gap-2">
                <span op-70>{{ t('library.totalPlaytime') }}:</span>
                <span text-base font-semibold>
                  {{ stats.totalPlaytimeHours }} {{ t('library.hours') }}
                  {{ stats.totalPlaytimeMinutes }} {{ t('library.minutes') }}
                </span>
              </div>
            </div>
            <div text-sm op-60>
              {{ t('library.gamesCount', { count: sortedGames.length }) }}
            </div>
          </div>

          <div v-if="sortedGames.length === 0" flex="~ items-center justify-center" py-20>
            <Empty :description="t('library.noGames')" />
          </div>

          <!-- 封面视图 -->
          <div v-else-if="viewMode === 'cover'" grid="~ cols-2 md:cols-3 lg:cols-4 xl:cols-5 2xl:cols-6" gap-4>
            <div
              v-for="game in sortedGames"
              :key="game.appId"
              class="game-card"
              bg-container cursor-pointer overflow-hidden rounded-lg transition-all hover:shadow-lg
            >
              <div relative aspect="2/3" overflow-hidden>
                <img
                  :src="getGameCoverUrl(game.appId)"
                  :alt="displayName(game)"
                  h-full w-full object-cover
                  loading="lazy"
                  @error="(e) => (e.target as HTMLImageElement).src = getGameHeaderUrl(game.appId)"
                >
                <!-- 标签（家庭共享 / 愿望单 / 未拥有） -->
                <div absolute left-2 top-2 flex flex-col items-start gap-1>
                  <Tag v-if="game.isFamilyShared" color="cyan" class="!me-0">
                    {{ t('library.familyShared') }}
                  </Tag>
                  <Tag v-if="game.isInWishlist" color="purple" class="!me-0">
                    {{ t('library.inWishlist') }}
                  </Tag>
                  <Tag v-if="!game.isOwned && !game.isFamilyShared" color="default" class="!me-0">
                    {{ t('library.notOwned') }}
                  </Tag>
                </div>
                <div
                  absolute bottom-0 left-0 right-0 p-3
                  bg-gradient="to-t from-black/85 to-transparent"
                >
                  <div truncate text-sm text-white font-medium :title="displayName(game)">
                    {{ displayName(game) }}
                  </div>
                  <div v-if="englishName(game)" truncate text-xs text-white op-70 :title="englishName(game)">
                    {{ englishName(game) }}
                  </div>
                  <div mt-1 flex="~ items-center justify-between" text-xs text-white op-80>
                    <span>{{ formatPlaytime(game.playtimeForever) }}</span>
                    <span op-70>{{ game.appId }}</span>
                  </div>
                  <!-- 成就进度 -->
                  <div v-if="game.achievementTotal > 0" mt-1>
                    <Tooltip :title="`${t('library.achievements')}: ${game.achievementUnlocked} / ${game.achievementTotal}`">
                      <Progress
                        :percent="Math.round(game.achievementPercentage)"
                        size="small"
                        :show-info="false"
                        :stroke-color="game.achievementPercentage >= 100 ? '#52c41a' : '#1677ff'"
                      />
                    </Tooltip>
                  </div>
                  <!-- 家庭拥有者 -->
                  <div v-if="game.ownerSteamIds.length > 0" mt-1 truncate text-xs text-white op-60 :title="formatOwners(game)">
                    {{ t('library.familyOwners') }}: {{ formatOwners(game) }}
                  </div>
                </div>
              </div>
            </div>
          </div>

          <!-- 列表视图 -->
          <div v-else class="game-list" space-y-2>
            <div
              v-for="game in sortedGames"
              :key="game.appId"
              class="game-row"
              flex="~ items-center gap-4" bg-container hover:bg-fill-2 cursor-pointer rounded-lg p-3 transition-all
            >
              <img
                :src="getGameHeaderUrl(game.appId)"
                :alt="displayName(game)"
                h-14 w-24 flex-shrink-0 rounded object-cover
                loading="lazy"
              >
              <div min-w-0 flex-1>
                <div flex="~ items-center gap-2">
                  <span truncate font-medium :title="displayName(game)">
                    {{ displayName(game) }}
                  </span>
                  <Tag v-if="game.isFamilyShared" color="cyan">
                    {{ t('library.familyShared') }}
                  </Tag>
                  <Tag v-if="game.isInWishlist" color="purple">
                    {{ t('library.inWishlist') }}
                  </Tag>
                  <Tag v-if="!game.isOwned && !game.isFamilyShared" color="default">
                    {{ t('library.notOwned') }}
                  </Tag>
                </div>
                <div mt-1 flex="~ items-center gap-3" text-sm op-60>
                  <span v-if="englishName(game)" truncate :title="englishName(game)">{{ englishName(game) }}</span>
                  <span flex-shrink-0>{{ t('library.appId') }}: {{ game.appId }}</span>
                  <span v-if="game.ownerSteamIds.length > 0" truncate :title="formatOwners(game)">
                    {{ t('library.familyOwners') }}: {{ formatOwners(game) }}
                  </span>
                </div>
              </div>
              <!-- 成就进度 -->
              <div v-if="game.achievementTotal > 0" min-w-36 flex-shrink-0>
                <div mb-1 text-center text-xs op-70>
                  {{ t('library.achievements') }}: {{ game.achievementUnlocked }} / {{ game.achievementTotal }}
                </div>
                <Progress
                  :percent="Math.round(game.achievementPercentage)"
                  size="small"
                  :stroke-color="game.achievementPercentage >= 100 ? '#52c41a' : '#1677ff'"
                />
              </div>
              <div min-w-40 flex-shrink-0 text-right text-sm>
                <div font-medium>
                  {{ formatPlaytime(game.playtimeForever) }}
                </div>
                <div mt-1 text-xs op-60>
                  {{ t('library.lastPlayed') }}: {{ formatLastPlayed(game.rtimeLastPlayed) }}
                </div>
              </div>
            </div>
          </div>
        </Tabs.TabPane>
      </Tabs>
    </Spin>
  </FaPageMain>
</template>

<style scoped>
.game-card:hover {
  transform: translateY(-4px);
}

.game-list {
  max-height: calc(100vh - 320px);
  overflow-y: auto;
}

/*
 * Steam 库常见规模是上千个游戏，封面视图一次性绘制全部卡片会明显掉帧。
 * content-visibility: auto 让浏览器跳过视口外条目的布局与绘制；
 * contain-intrinsic-size 提供占位尺寸，避免滚动条长度跳动。
 * 注意：这不等于真正的虚拟滚动（DOM 节点仍然存在），
 * 真正的虚拟化留待视图重构时处理。
 */
.game-card {
  /* 封面为 2:3，按每列约 200px 宽估算高度 */
  contain-intrinsic-size: auto 300px;
  content-visibility: auto;
}

.game-row {
  contain-intrinsic-size: auto 80px;
  content-visibility: auto;
}
</style>
