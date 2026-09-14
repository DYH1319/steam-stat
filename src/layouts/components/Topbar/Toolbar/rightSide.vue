<script setup lang="ts">
import { Button, Tag, Tooltip } from 'ant-design-vue'
import { useI18n } from 'vue-i18n'
import { useSlots } from '@/slots'
import ColorScheme from './ColorScheme/index.vue'
import Fullscreen from './Fullscreen/index.vue'
import NavSearch from './NavSearch/index.vue'
import PageReload from './PageReload/index.vue'

defineOptions({
  name: 'ToolbarRightSide',
})

const { t } = useI18n()
const router = useRouter()
const settingsStore = useSettingsStore()
const operationalStatus = ref<SteamOperationalStatus | null>(null)
let statusTimer: ReturnType<typeof setInterval> | null = null

const statusColor = computed(() => {
  switch (operationalStatus.value?.connectivity) {
    case 'online': return 'success'
    case 'degraded': return 'warning'
    default: return 'default'
  }
})

async function refreshOperationalStatus() {
  try {
    operationalStatus.value = await window.electron.steamOperationalStatusGet()
  }
  catch {
    operationalStatus.value = null
  }
}

onMounted(() => {
  refreshOperationalStatus()
  statusTimer = setInterval(refreshOperationalStatus, 15000)
})

onBeforeUnmount(() => {
  if (statusTimer) {
    clearInterval(statusTimer)
    statusTimer = null
  }
})
</script>

<template>
  <div class="flex items-center">
    <Tooltip v-if="operationalStatus" :title="t(`connectivity.${operationalStatus.connectivity}Description`)">
      <Tag :color="statusColor" class="m-0 me-2">
        {{ t(`connectivity.${operationalStatus.connectivity}`) }}
      </Tag>
    </Tooltip>
    <Button
      v-if="operationalStatus?.reauthenticationAccounts.length"
      type="link"
      danger
      size="small"
      class="me-1"
      @click="router.push('/steamLogin')"
    >
      {{ t('connectivity.reauthenticate', { count: operationalStatus.reauthenticationAccounts.length }) }}
    </Button>
    <NavSearch v-if="settingsStore.settings.toolbar.navSearch" />
    <Fullscreen v-if="settingsStore.settings.toolbar.fullscreen" />
    <PageReload v-if="settingsStore.settings.toolbar.pageReload" />
    <ColorScheme v-if="settingsStore.settings.toolbar.colorScheme" />
    <component :is="useSlots('toolbar-end')" />
  </div>
</template>
