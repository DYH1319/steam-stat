<script setup lang="ts">
import { useClipboard } from '@vueuse/core'
import { useI18n } from 'vue-i18n'
import { toast } from 'vue-sonner'

defineOptions({
  name: 'LinkView',
})

const { t } = useI18n()

const route = useRoute()
const electronApi = (window as Window).electron

const { copy, copied } = useClipboard()
watch(copied, (val) => {
  val && toast.success(t('common.copyLinkSuccess'))
})

async function open() {
  // window.open(route.meta.link, '_blank')
  if (route.meta.link) {
    try {
      electronApi.shellOpenExternal(route.meta.link as string)
    }
    catch (error: any) {
      toast.error(`${t('common.openLinkFailed')}: ${error?.message || error}`)
    }
  }
}
</script>

<template>
  <div class="flex flex-col h-full w-full absolute">
    <Transition name="fade" mode="out-in" appear>
      <FaPageMain :key="route.meta.link" class="mb-0 flex flex-1 flex-col justify-center">
        <div class="flex flex-col items-center">
          <FaIcon name="i-icon-park-twotone:planet" class="text-primary/80 size-30" />
          <div class="text-xl text-dark my-2 dark-text-white">
            {{ t('common.linkDesc') }}
          </div>
          <div class="text-[14px] text-secondary-foreground/50 my-2 text-center max-w-[300px] cursor-pointer" @click="route.meta.link && copy(route.meta.link)">
            <FaTooltip :text="t('common.copyLink')">
              <div class="line-clamp-3">
                {{ route.meta.link }}
              </div>
            </FaTooltip>
          </div>
          <div class="flex gap-4 items-center">
            <FaButton class="my-4" @click="route.meta.link && copy(route.meta.link)">
              <FaIcon name="i-streamline:copy-paste-solid" />
              {{ t('common.copyLink') }}
            </FaButton>
            <FaButton class="my-4" @click="open">
              <FaIcon name="i-ri:external-link-fill" />
              {{ t('common.openLink') }}
            </FaButton>
          </div>
        </div>
      </FaPageMain>
    </Transition>
  </div>
</template>

<style scoped>
.fade-enter-active {
  transition: 0.2s;
}

.fade-leave-active {
  transition: 0.15s;
}

.fade-enter-from {
  opacity: 0;
}

.fade-leave-to {
  opacity: 0;
}
</style>
