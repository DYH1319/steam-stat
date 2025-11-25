<script setup lang="ts">
import { ua } from '@/utils/ua'
import Provider from './ui/provider/index.vue'

const route = useRoute()

const settingsStore = useSettingsStore()
const { auth } = useAuth()

document.body.setAttribute('data-os', ua.getOS().name || '')

const isAuth = computed(() => {
  return route.matched.every((item) => {
    return auth(item.meta.auth ?? '')
  })
})

// 设置网页 title
watch([
  () => settingsStore.settings.app.enableDynamicTitle,
  () => settingsStore.title,
], () => {
  if (settingsStore.settings.app.enableDynamicTitle && settingsStore.title) {
    const title = typeof settingsStore.title === 'function' ? settingsStore.title() : settingsStore.title
    document.title = `${title} - ${import.meta.env.VITE_APP_TITLE}`
  }
  else {
    document.title = import.meta.env.VITE_APP_TITLE
  }
}, {
  immediate: true,
  deep: true,
})

onMounted(() => {
  settingsStore.setMode(document.documentElement.clientWidth)
  window.addEventListener('resize', () => {
    settingsStore.setMode(document.documentElement.clientWidth)
  })

  // 禁用鼠标中键功能（防止在 Electron 中打开新窗口）
  document.addEventListener('auxclick', (event) => {
    // auxclick 事件捕获鼠标中键（button 1）和其他辅助按钮的点击
    if (event.button === 1) {
      event.preventDefault()
      event.stopPropagation()
    }
  }, true)

  // 也禁用 mousedown 中的中键事件作为额外保险
  document.addEventListener('mousedown', (event) => {
    if (event.button === 1) {
      event.preventDefault()
      event.stopPropagation()
    }
  }, true)
})
</script>

<template>
  <Provider>
    <RouterView v-slot="{ Component }">
      <component :is="Component" v-if="isAuth" />
      <FaNotAllowed v-else />
    </RouterView>
    <FaBackToTop />
    <FaToast />
    <FaNotification />
    <FaSystemInfo />
  </Provider>
</template>
