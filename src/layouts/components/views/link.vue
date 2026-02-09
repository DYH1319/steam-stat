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
  <div class="absolute h-full w-full flex flex-col">
    <Transition name="slide-right" mode="out-in" appear>
      <FaPageMain :key="route.meta.link" class="mb-0 flex flex-1 flex-col justify-center">
        <div class="flex flex-col items-center">
          <FaIcon name="i-icon-park-twotone:planet" class="size-30 text-primary/80" />
          <div class="my-2 text-xl text-dark dark-text-white">
            {{ t('common.linkDesc') }}
          </div>
          <div class="my-2 max-w-[300px] cursor-pointer text-center text-[14px] text-secondary-foreground/50" @click="route.meta.link && copy(route.meta.link)">
            <FaTooltip :text="t('common.copyLink')">
              <div class="line-clamp-3">
                {{ route.meta.link }}
              </div>
            </FaTooltip>
          </div>
          <div class="flex items-center gap-4">
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
.slide-right-enter-active {
  transition: 0.2s;
}

.slide-right-leave-active {
  transition: 0.15s;
}

.slide-right-enter-from {
  margin-left: -20px;
  opacity: 0;
}

.slide-right-leave-to {
  margin-left: 20px;
  opacity: 0;
}
</style>
