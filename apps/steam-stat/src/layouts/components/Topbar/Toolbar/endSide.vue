<script setup lang="ts">
import { Button, Tag, Tooltip } from 'ant-design-vue'
import { useI18n } from 'vue-i18n'
import { useSlots } from '@/slots'
import Tools from './tools.vue'

defineOptions({
  name: 'ToolbarEndSide',
})

const { t } = useI18n()
const router = useRouter()
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
    <Tools mode="right-side" />
    <Component :is="useSlots('toolbar-end')" />
  </div>
</template>
