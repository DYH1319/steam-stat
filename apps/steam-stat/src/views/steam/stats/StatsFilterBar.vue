<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import { useStatsContext } from '@/features/stats/context'
import { RANGE_PRESETS, useStatsStore } from '@/store/modules/stats'

const props = defineProps<{
  /** 单游戏详情页隐藏应用筛选 */
  hideApps?: boolean
}>()

const { t } = useI18n()
const store = useStatsStore()
const ctx = useStatsContext()

const userOptions = computed(() => ctx.data.value.users.map(user => ({
  value: user.steamId,
  label: user.personaName ? `${user.personaName}${user.accountName ? ` (${user.accountName})` : ''}` : user.steamId,
})))

const appOptions = computed(() => {
  const played = new Set(ctx.data.value.sessions.map(s => s.appId))
  return [...ctx.data.value.apps.values()]
    .filter(app => played.has(app.appId))
    .map(app => ({ value: app.appId, label: app.name }))
    .sort((a, b) => a.label.localeCompare(b.label))
})

const presetOptions = computed(() => [
  ...RANGE_PRESETS.map(value => ({ value, label: t(`stats.preset.${value}`) })),
  { value: 'custom', label: t('stats.preset.custom') },
])

const topNOptions = [5, 10, 15, 20, 30].map(value => ({ value, label: `TOP ${value}` }))

const custom = computed<[string, string] | null>({
  get: () => store.customRange ?? [store.range.from, store.range.to],
  set: (value) => {
    store.customRange = value
    store.rangePreset = value ? 'custom' : '30d'
  },
})

const showUsers = computed(() => userOptions.value.length > 1)
const drillChips = computed(() => {
  const chips: { key: 'app' | 'day', label: string }[] = []
  if (ctx.drill.value.appId != null) {
    chips.push({ key: 'app', label: ctx.appName(ctx.drill.value.appId) })
  }
  if (ctx.drill.value.day) {
    chips.push({ key: 'day', label: ctx.drill.value.day })
  }
  return chips
})

function clearChip(key: 'app' | 'day') {
  ctx.drill.value = { ...ctx.drill.value, ...(key === 'app' ? { appId: null } : { day: null }) }
}
</script>

<template>
  <div class="mb-4 p-3 border rounded-lg bg-card flex flex-wrap gap-3 items-center">
    <FaSelect
      v-if="showUsers"
      v-model="store.steamIds"
      multiple
      :options="userOptions"
      :placeholder="t('stats.filter.allUsers')"
      class="min-w-44"
    />
    <FaSelect v-model="store.rangePreset" :options="presetOptions" class="w-36" />
    <el-date-picker
      v-model="custom"
      type="daterange"
      value-format="YYYY-MM-DD"
      :start-placeholder="t('useRecord.startDate')"
      :end-placeholder="t('useRecord.endDate')"
      :clearable="false"
      class="date-range"
    />
    <template v-if="!props.hideApps">
      <el-select
        v-model="store.includeApps"
        multiple
        filterable
        collapse-tags
        collapse-tags-tooltip
        clearable
        :placeholder="t('stats.filter.onlyApps')"
        class="app-select"
      >
        <el-option v-for="app in appOptions" :key="app.value" :value="app.value" :label="app.label" />
      </el-select>
      <el-select
        v-model="store.excludeApps"
        multiple
        filterable
        collapse-tags
        collapse-tags-tooltip
        clearable
        :placeholder="t('stats.filter.excludeApps')"
        class="app-select"
      >
        <el-option v-for="app in appOptions" :key="app.value" :value="app.value" :label="app.label" />
      </el-select>
    </template>
    <FaSelect v-model="store.topN" :options="topNOptions" class="w-28" />
    <FaButton variant="outline" @click="store.resetFilters(); ctx.clearDrill()">
      <FaIcon name="i-mdi:restart" />
      {{ t('stats.filter.reset') }}
    </FaButton>
    <div v-if="drillChips.length" class="flex flex-wrap gap-2 items-center">
      <span class="text-xs text-muted-foreground">{{ t('stats.filter.drill') }}</span>
      <FaTag v-for="chip in drillChips" :key="chip.key" variant="secondary" closable @close="clearChip(chip.key)">
        {{ chip.label }}
      </FaTag>
    </div>
  </div>
</template>

<style scoped>
.date-range {
  max-width: 260px;
}

.app-select {
  width: 200px;
}
</style>
