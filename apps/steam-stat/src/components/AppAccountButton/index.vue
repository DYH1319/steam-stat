<script setup lang="ts">
import type { HTMLAttributes } from 'vue'
import { useI18n } from 'vue-i18n'
import { cn } from '@/utils'

defineOptions({
  name: 'AppAccountButton',
})

const props = withDefaults(defineProps<{
  onlyAvatar?: boolean
  dropdownAlign?: 'start' | 'center' | 'end'
  dropdownSide?: 'left' | 'right' | 'top' | 'bottom'
  buttonVariant?: 'secondary' | 'ghost'
  class?: HTMLAttributes['class']
}>(), {
  dropdownAlign: 'end',
  dropdownSide: 'right',
  buttonVariant: 'ghost',
})

const { t } = useI18n()

const appAccountStore = useAppAccountStore()
</script>

<template>
  <FaDropdown
    :align="dropdownAlign" :side="dropdownSide" :items="[]" class="flex-center"
  >
    <template #header>
      <div class="select-none space-y-2">
        <div class="text-xs text-secondary-foreground/50 font-light">
          {{ t('accountButton.title') }}
        </div>
        <div class="flex-center-start gap-2">
          <FaAvatar :src="appAccountStore.avatar" :fallback="appAccountStore.account.slice(0, 5)" shape="square" />
          <div class="space-y-1">
            <div class="text-base lh-none">
              {{ t('accountButton.steamLocalData') ?? appAccountStore.account }}
            </div>
            <div class="text-xs text-secondary-foreground/50 font-normal">
              {{ t('accountButton.subtitle') }}
            </div>
          </div>
        </div>
      </div>
    </template>
    <FaButton
      :variant="buttonVariant" size="icon-sm" :class="cn('flex-center gap-1 p-2', {
        'p-1': onlyAvatar,
      }, props.class)"
    >
      <FaAvatar :src="appAccountStore.avatar" :class="cn('size-6', { 'size-full': onlyAvatar })">
        <FaIcon name="i-carbon:user-avatar-filled" class="text-secondary-foreground/50 size-6" />
      </FaAvatar>
      <div v-if="!onlyAvatar" class="flex-center-between flex-1 gap-2 min-w-0">
        <div class="text-start flex-1 truncate">
          {{ t('accountButton.steamLocalData') ?? appAccountStore.account }}
        </div>
        <FaIcon name="i-material-symbols:expand-all-rounded" />
      </div>
    </FaButton>
  </FaDropdown>
</template>
