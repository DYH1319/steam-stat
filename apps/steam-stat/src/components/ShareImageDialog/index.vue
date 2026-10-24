<script setup lang="ts">
import type { ShareImageData, ShareImageModule, ShareTemplate } from '@/features/stats/shareImage'
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { toast } from 'vue-sonner'
import { useChartTheme } from '@/composables/useChartTheme'
import { renderShareImage } from '@/features/stats/shareImage'

const props = defineProps<{
  title: string
  subtitle?: string
  accountName?: string
  stats: { label: string, value: string }[]
  topGames?: { name: string, value: number, color: string }[]
  chart?: { label: string, value: number }[]
  records?: { label: string, value: string }[]
}>()

const visible = defineModel<boolean>({ default: false })
const { t } = useI18n()
const { theme } = useChartTheme()

const template = ref<ShareTemplate>('long')
const modules = ref<ShareImageModule['id'][]>(['summary', 'topGames', 'chart', 'records'])
const hideAccountName = ref(false)
const hideGameNames = ref(false)
const preview = ref('')
const rendering = ref(false)

const templateOptions = computed(() => [
  { value: 'long', label: t('report.tplLong') },
  { value: 'square', label: t('report.tplSquare') },
  { value: 'portrait', label: t('report.tplPortrait') },
])
function toggleModule(id: ShareImageModule['id']) {
  modules.value = modules.value.includes(id)
    ? modules.value.filter(m => m !== id)
    : [...modules.value, id]
}

const moduleOptions = computed(() => [
  { value: 'summary' as const, label: t('report.modSummary') },
  { value: 'topGames' as const, label: t('report.modTopGames') },
  { value: 'chart' as const, label: t('report.modChart') },
  { value: 'records' as const, label: t('report.modRecords') },
])

async function render() {
  rendering.value = true
  try {
    const data: ShareImageData = {
      title: props.title,
      subtitle: props.subtitle,
      accountName: props.accountName,
      stats: props.stats,
      topGames: props.topGames,
      chart: props.chart,
      records: props.records,
    }
    preview.value = await renderShareImage({
      template: template.value,
      modules: modules.value,
      hideAccountName: hideAccountName.value,
      hideGameNames: hideGameNames.value,
      theme: theme.value,
      data,
    })
  }
  finally {
    rendering.value = false
  }
}

watch([visible, template, modules, hideAccountName, hideGameNames, theme], () => {
  if (visible.value) {
    void render()
  }
}, { deep: true })

async function copy() {
  try {
    const blob = await (await fetch(preview.value)).blob()
    await navigator.clipboard.write([new ClipboardItem({ 'image/png': blob })])
    toast.success(t('report.copied'))
  }
  catch {
    toast.error(t('report.copyFailed'))
  }
}

function download() {
  const a = document.createElement('a')
  a.href = preview.value
  a.download = `${props.title}.png`
  a.click()
}
</script>

<template>
  <FaModal v-model="visible" :title="t('report.shareTitle')" class="max-w-3xl">
    <div class="pb-3 flex flex-wrap gap-4 items-center">
      <FaSelect v-model="template" :options="templateOptions" class="w-36" />
      <label v-for="m in moduleOptions" :key="m.value" class="text-sm flex gap-1 items-center">
        <FaCheckbox :model-value="modules.includes(m.value)" @update:model-value="toggleModule(m.value)" />{{ m.label }}
      </label>
    </div>
    <div class="text-sm pb-3 flex flex-wrap gap-4">
      <label class="flex gap-2 items-center"><FaSwitch v-model="hideAccountName" />{{ t('report.hideAccount') }}</label>
      <label class="flex gap-2 items-center"><FaSwitch v-model="hideGameNames" />{{ t('report.hideGames') }}</label>
    </div>
    <div class="p-2 border rounded-lg bg-muted flex max-h-[60vh] items-start justify-center overflow-auto">
      <FaIcon v-if="rendering" name="i-mdi:loading" class="h-10 w-10 animate-spin" />
      <img v-else-if="preview" :src="preview" class="max-h-[56vh] object-contain">
    </div>
    <template #footer>
      <FaButton variant="outline" @click="copy">
        <FaIcon name="i-mdi:content-copy" /> {{ t('report.copy') }}
      </FaButton>
      <FaButton @click="download">
        <FaIcon name="i-mdi:download" /> {{ t('report.save') }}
      </FaButton>
    </template>
  </FaModal>
</template>
