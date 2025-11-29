<script setup lang="ts">
defineOptions({
  name: 'FaCopyright',
})

const settingsStore = useSettingsStore()
const electronApi = (window as any).electron

// 处理链接点击，在默认浏览器中打开
function handleLinkClick(event: MouseEvent, url: string) {
  event.preventDefault()
  electronApi.shellOpenExternal(url)
}
</script>

<template>
  <footer v-if="settingsStore.settings.copyright.enable" class="my-4 flex flex-wrap items-center justify-center px-4 text-sm text-secondary-foreground/50">
    <span class="px-1">Copyright</span>
    <FaIcon name="i-ri:copyright-line" class="size-5" />
    <span v-if="settingsStore.settings.copyright.dates" class="px-1">{{ settingsStore.settings.copyright.dates }}</span>
    <template v-if="settingsStore.settings.copyright.company">
      <a v-if="settingsStore.settings.copyright.website" href="javascript:void(0)" class="px-1 text-center no-underline transition hover-text-secondary-foreground" @click="handleLinkClick($event, settingsStore.settings.copyright.website)">{{ settingsStore.settings.copyright.company }}</a>
      <span v-else class="px-1">{{ settingsStore.settings.copyright.company }}</span>
    </template>
    <a v-if="settingsStore.settings.copyright.beian" href="javascript:void(0)" class="px-1 text-center no-underline transition hover-text-secondary-foreground" @click="handleLinkClick($event, 'https://beian.miit.gov.cn/')">{{ settingsStore.settings.copyright.beian }}</a>
  </footer>
</template>
