<script setup lang="ts">
import CloseConfirmDialog from '@/components/CloseConfirmDialog.vue'
import TitleBar from '@/components/TitleBar/index.vue'
import useZoom from '@/composables/useZoom'
import { asyncRoutes } from '@/router/routes'
import { useDataSourceStore } from '@/store/modules/dataSource'
import { getAppSettings } from '@/utils/appSettings'
import dayjs from '@/utils/dayjs'
import { collectExperimentalPaths, isExperimentalEnabled } from '@/utils/experimental'
import { ua } from '@/utils/ua'
import Provider from './ui/provider/index.vue'
import 'dayjs/locale/zh-cn'

const route = useRoute()

const experimentalPaths = collectExperimentalPaths(asyncRoutes)

const router = useRouter()

const appSettingsStore = useAppSettingsStore()

const { auth } = useAppAuth()
const { generateTitle } = useAppMenu()

document.body.setAttribute('data-os', ua.getOS().name || '')

const isAuth = computed(() => {
  return route.matched.every((item) => {
    return auth(item.meta.auth ?? '')
  })
})

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

// 设置网页 title
watch([
  () => appSettingsStore.settings.app.dynamicTitle,
  () => appSettingsStore.title,
], () => {
  nextTick(() => {
    if (appSettingsStore.settings.app.dynamicTitle && appSettingsStore.title) {
      document.title = `${generateTitle(appSettingsStore.title)} - ${import.meta.env.VITE_APP_TITLE}`
    }
    else {
      document.title = import.meta.env.VITE_APP_TITLE
    }
  })
}, {
  immediate: true,
  deep: true,
})

// 初始化全局更新器监听
const updaterStore = useUpdaterStore()
updaterStore.initListener()
updaterStore.fetchUpdaterStatus()

onMounted(() => {
  // 桌面应用不做移动端适配（v5 中 layout.enableMobileAdaptation=false 的效果）：
  // Electron 窗口最小宽度为 1400 逻辑像素，但用户缩放（Ctrl+滚轮）会降低视口宽度，
  // 若交给框架的 setMode 会在缩放后误判为 mobile，故 mode 保持默认 'pc'
  dayjs.locale('zh-cn')

  getAppSettings().then((appSetting) => {
    const homePage = appSetting.homePage ?? '/data-source/local'
    // 主页可能指向一个因实验性功能关闭而未注册的路由，此时回退到 /data-source/local
    const target = experimentalPaths.includes(homePage) && !isExperimentalEnabled() ? '/data-source/local' : homePage
    router.replace(target)
  }).catch(() => undefined)

  // 初始化界面缩放状态（主进程已在窗口创建时应用持久化缩放）
  initZoom()
  window.addEventListener('keydown', onZoomKeydown, true)
  window.addEventListener('wheel', onZoomWheel, { passive: false, capture: true })

  // 启动数据源健康轮询，路由守卫与菜单置灰依赖其结果
  useDataSourceStore().startPolling()

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
        <AppNotSupportedMobile v-if="!appSettingsStore.settings.app.mobile && appSettingsStore.mode === 'mobile'" />
        <Component :is="Component" v-else-if="isAuth" />
        <AppNotAllowed v-else />
      </RouterView>
    </div>
    <AppBackToTop />
    <FaToast :theme="appSettingsStore.currentColorScheme" :duration="1500" :visible-toasts="1" />
    <AppSystemInfo />
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
