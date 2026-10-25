<script setup lang="ts">
import type { ShareModel, ShareModule, ShareTemplate } from '@/features/stats/shareImage'
import { useI18n } from 'vue-i18n'
import { toast } from 'vue-sonner'
import { useChartTheme } from '@/composables/useChartTheme'
import { renderShareImage } from '@/features/stats/shareImage'
import { downloadBlob, safeFileName } from '@/utils/download'

defineOptions({
  name: 'ShareImageDialog',
})

const props = defineProps<{
  model: ShareModel | null
}>()

const open = defineModel<boolean>({ default: false })

const { t } = useI18n()
const theme = useChartTheme()

const template = ref<ShareTemplate>('long')
const modules = ref<Record<ShareModule, boolean>>({ summary: true, topGames: true, highlights: true, routine: true })
const maskAccount = ref(true)
const maskGames = ref(false)
const preview = ref('')

const templateOptions = computed(() => (['long', 'square', 'portrait'] as const).map(value => ({ value, label: t(`share.template.${value}`) })))
const moduleKeys: ShareModule[] = ['summary', 'topGames', 'highlights', 'routine']

function render(): HTMLCanvasElement | null {
  if (!props.model) {
    return null
  }
  const gradient = theme.value.gradients(2)
  return renderShareImage(props.model, {
    template: template.value,
    modules: moduleKeys.filter(key => modules.value[key]),
    maskAccount: maskAccount.value,
    maskGames: maskGames.value,
    gradient: [gradient[0][0], gradient[1][1]],
    accent: '#ffffff',
    labels: {
      topGames: t('share.module.topGames'),
      highlights: t('share.module.highlights'),
      routine: t('share.module.routine'),
      maskedGame: t('share.maskedGame'),
    },
  })
}

watch([open, template, modules, maskAccount, maskGames, () => props.model, theme], () => {
  if (open.value) {
    preview.value = render()?.toDataURL('image/png') ?? ''
  }
}, { deep: true, immediate: true })

function toBlob(): Promise<Blob | null> {
  const canvas = render()
  return new Promise(resolve => canvas ? canvas.toBlob(resolve, 'image/png') : resolve(null))
}

async function copy() {
  try {
    const blob = await toBlob()
    if (!blob) {
      return
    }
    await navigator.clipboard.write([new ClipboardItem({ 'image/png': blob })])
    toast.success(t('share.copied'))
  }
  catch (e: any) {
    toast.error(`${t('share.copyFailed')}: ${e?.message || e}`)
  }
}

async function save() {
  const blob = await toBlob()
  if (blob) {
    downloadBlob(`${safeFileName(props.model?.title ?? 'steam-stat')}.png`, blob)
  }
}
</script>

<template>
  <FaModal v-model="open" :title="t('share.title')" :footer="false" class="max-w-5xl w-[92vw]">
    <div class="flex flex-col gap-6 md:flex-row">
      <div class="shrink-0 space-y-4 md:w-60">
        <div>
          <div class="text-sm font-medium mb-2">
            {{ t('share.templateLabel') }}
          </div>
          <FaRadioGroup v-model="template" :options="templateOptions" />
        </div>
        <div>
          <div class="text-sm font-medium mb-2">
            {{ t('share.modulesLabel') }}
          </div>
          <div class="space-y-2">
            <FaCheckbox v-for="key in moduleKeys" :key="key" v-model="modules[key]">
              {{ t(`share.module.${key}`) }}
            </FaCheckbox>
          </div>
        </div>
        <div class="space-y-2">
          <div class="text-sm font-medium">
            {{ t('share.privacyLabel') }}
          </div>
          <label class="text-sm flex gap-2 items-center justify-between">
            {{ t('share.maskAccount') }}
            <FaSwitch v-model="maskAccount" />
          </label>
          <label class="text-sm flex gap-2 items-center justify-between">
            {{ t('share.maskGames') }}
            <FaSwitch v-model="maskGames" />
          </label>
        </div>
        <div class="flex flex-wrap gap-2">
          <FaButton @click="copy">
            <FaIcon name="i-mdi:content-copy" />
            {{ t('share.copy') }}
          </FaButton>
          <FaButton variant="outline" @click="save">
            <FaIcon name="i-mdi:download" />
            {{ t('share.save') }}
          </FaButton>
        </div>
      </div>
      <div class="p-3 rounded-lg bg-muted flex flex-1 max-h-[70vh] justify-center overflow-auto">
        <img v-if="preview" :src="preview" class="rounded-md h-auto max-w-full shadow-lg object-contain" alt="">
      </div>
    </div>
  </FaModal>
</template>
