<script setup lang="ts">
import CloseConfirmDialog from '@/components/CloseConfirmDialog.vue'
import TitleBar from '@/components/TitleBar/index.vue'
import useZoom from '@/utils/composables/useZoom'
import { ua } from '@/utils/ua'
import Provider from './ui/provider/index.vue'

const electronApi = (window as Window).electron
const router = useRouter()
const route = useRoute()

const settingsStore = useSettingsStore()
const { auth } = useAuth()
const { zoomIn, zoomOut, zoomReset, initZoom } = useZoom()

// 浏览器式缩放：Ctrl + =/+ 放大，Ctrl + - 缩小，Ctrl + 0 重置
function onZoomKeydown(event: KeyboardEvent) {
  if (!event.ctrlKey || event.altKey || event.metaKey) {
    return
  }
  if (event.key === '=' || event.key === '+' || event.code === 'NumpadAdd') {
    event.preventDefault()
    zoomIn()
  }
  else if (event.key === '-' || event.code === 'NumpadSubtract') {
    event.preventDefault()
    zoomOut()
  }
  else if (event.key === '0' || event.code === 'Numpad0') {
    event.preventDefault()
    zoomReset()
  }
}

// 浏览器式缩放：Ctrl + 鼠标滚轮
function onZoomWheel(event: WheelEvent) {
  if (!event.ctrlKey) {
    return
  }
  event.preventDefault()
  if (event.deltaY < 0) {
    zoomIn()
  }
  else if (event.deltaY > 0) {
    zoomOut()
  }
}

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

// 初始化全局更新器监听
const updaterStore = useUpdaterStore()
updaterStore.initListener()
updaterStore.fetchUpdaterStatus()

onMounted(() => {
  electronApi.settingGet().then((appSetting) => {
    router.replace(appSetting.homePage ?? '/status')
  })

  // 初始化界面缩放状态（主进程已在窗口创建时应用持久化缩放）
  initZoom()
  window.addEventListener('keydown', onZoomKeydown, true)
  window.addEventListener('wheel', onZoomWheel, { passive: false, capture: true })

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
    // 禁用 Ctrl + R 和 F5 刷新
    document.addEventListener('keydown', (event) => {
      if ((event.ctrlKey && event.key === 'r') || event.key === 'F5') {
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
  width: 12px;
}

.app-content::-webkit-scrollbar-track {
  background: var(--scrollbar-bg-color);
}

.app-content::-webkit-scrollbar-thumb {
  background-color: hsl(var(--scrollbar-color));
  border-radius: 8px;
}

.app-content::-webkit-scrollbar-thumb:hover {
  background-color: hsl(var(--scrollbar-color) / 80%);
}
</style>
