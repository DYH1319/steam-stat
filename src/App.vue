<script setup lang="ts">
import CloseConfirmDialog from '@/components/CloseConfirmDialog.vue'
import TitleBar from '@/components/TitleBar/index.vue'
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

  // 禁用鼠标中键功能
  document.addEventListener('auxclick', (event) => {
    // auxclick 事件捕获鼠标中键（button 1）和其他辅助按钮的点击
    if (event.button === 1) {
      event.preventDefault()
      event.stopPropagation()
    }
  }, true)

  if (import.meta.env.PROD) {
    // 禁用 Ctrl + R 刷新
    document.addEventListener('keydown', (event) => {
      if (event.ctrlKey && event.key === 'r') {
        event.preventDefault()
        event.stopPropagation()
      }
    }, true)
  }
})
</script>

<template>
  <Provider>
    <TitleBar />
    <div class="app-content">
      <RouterView v-slot="{ Component }">
        <component :is="Component" v-if="isAuth" />
        <FaNotAllowed v-else />
      </RouterView>
    </div>
    <FaBackToTop />
    <FaToast />
    <FaNotification />
    <FaSystemInfo />
    <CloseConfirmDialog />
  </Provider>
</template>

<style scoped>
.app-content {
  position: fixed;
  inset: var(--g-title-bar-height, 40px) 0 0 0;
  overflow: hidden auto;
}

.app-content::-webkit-scrollbar {
  width: 8px;
}

.app-content::-webkit-scrollbar-track {
  background: transparent;
}

.app-content::-webkit-scrollbar-thumb {
  background-color: hsl(var(--scrollbar-color));
  border-radius: 4px;
}

.app-content::-webkit-scrollbar-thumb:hover {
  background-color: hsl(var(--scrollbar-color) / 80%);
}
</style>
