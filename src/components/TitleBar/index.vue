<script setup lang="ts">
import { Button, Dropdown, Menu, MenuDivider, MenuItem } from 'ant-design-vue'
import { useI18n } from 'vue-i18n'
import imgLogo from '@/assets/images/logo.png'
import eventBus from '@/utils/eventBus'

defineOptions({
  name: 'TitleBar',
})

const { t } = useI18n()
const route = useRoute()
const mainPage = useMainPage()
const settingsStore = useSettingsStore()
const electronApi = (window as Window).electron

const appTitle = import.meta.env.VITE_APP_TITLE
const isMaximized = ref(false)

// Check initial maximized state
onMounted(async () => {
  isMaximized.value = await electronApi.windowIsMaximized()
})

// Window control functions
function handleMinimize() {
  electronApi.windowMinimize()
}

async function handleMaximize() {
  const result = await electronApi.windowMaximize()
  isMaximized.value = result ?? false
}

async function handleClose() {
  // 通知关闭确认组件处理
  eventBus.emit('vue:closeConfirm:show')
}

// Toggle color scheme
function toggleColorScheme() {
  const newScheme = settingsStore.currentColorScheme === 'dark' ? 'light' : 'dark'
  settingsStore.setColorScheme(newScheme)
  electronApi.settingUpdate({ colorScheme: newScheme } as Partial<AppSettings>)
  // 如果是设置界面，刷新界面，显示最新的主题设置
  if (route.path === '/setting') {
    // location.reload() // 浏览器原生刷新
    mainPage.reload() // 框架提供的刷新
  }
}

function setAndPersistColorScheme(scheme: 'light' | 'dark' | '') {
  settingsStore.setColorScheme(scheme)
  electronApi.settingUpdate({ colorScheme: scheme === '' ? 'system' : scheme } as Partial<AppSettings>)
  // 如果是设置界面，刷新界面，显示最新的主题设置
  if (route.path === '/setting') {
    // location.reload() // 浏览器原生刷新
    mainPage.reload() // 框架提供的刷新
  }
}

const isDark = computed(() => settingsStore.currentColorScheme === 'dark')
</script>

<template>
  <div class="fixed left-0 right-0 top-0 z-4999 h-[var(--g-title-bar-height,40px)] flex select-none items-center justify-between border-b-1 border-[hsl(var(--border))] bg-[hsl(var(--background))]">
    <!-- Drag region -->
    <div class="drag-region absolute inset-0" />

    <!-- Left: Logo and App Name -->
    <div class="z-1 flex items-center gap-2 pl-3">
      <img :src="imgLogo" class="h-6 w-6 flex-shrink-0" :draggable="false" alt="logo" style="image-rendering: -webkit-optimize-contrast; image-rendering: crisp-edges;">
      <span class="text-13px text-[hsl(var(--foreground))] font-500">{{ appTitle }}</span>
    </div>

    <!-- Right: Controls -->
    <div class="app-region-no-drag z-1 h-full flex items-center">
      <!-- Dark Mode Toggle -->
      <Button
        class="title-bar-btn"
        :title="isDark ? t('titleBar.lightMode') : t('titleBar.darkMode')"
        @click="toggleColorScheme"
      >
        <FaIcon :name="isDark ? 'i-ri:sun-line' : 'i-ri:moon-line'" class="text-16px" />
      </Button>

      <!-- More Options -->
      <Dropdown trigger="click" placement="bottom" arrow>
        <Button class="title-bar-btn" :title="t('titleBar.moreOptions')">
          <FaIcon name="i-ri:more-2-fill" class="text-16px" />
        </Button>
        <template #overlay>
          <Menu>
            <MenuItem @click="setAndPersistColorScheme('light')">
              <FaIcon name="i-ri:sun-line" class="mr-2" />
              {{ t('titleBar.lightMode') }}
            </MenuItem>
            <MenuItem @click="setAndPersistColorScheme('dark')">
              <FaIcon name="i-ri:moon-line" class="mr-2" />
              {{ t('titleBar.darkMode') }}
            </MenuItem>
            <MenuDivider />
            <MenuItem @click="setAndPersistColorScheme('')">
              <FaIcon name="i-ri:computer-line" class="mr-2" />
              {{ t('titleBar.systemMode') }}
            </MenuItem>
          </Menu>
        </template>
      </Dropdown>

      <!-- Minimize -->
      <Button class="title-bar-btn" :title="t('titleBar.minimize')" @click="handleMinimize">
        <FaIcon name="i-ri:subtract-line" class="text-16px" />
      </Button>

      <!-- Maximize / Restore -->
      <Button class="title-bar-btn" :title="isMaximized ? t('titleBar.restore') : t('titleBar.maximize')" @click="handleMaximize">
        <FaIcon :name="isMaximized ? 'i-ri:checkbox-multiple-blank-line' : 'i-ri:checkbox-blank-line'" class="text-16px" />
      </Button>

      <!-- Close -->
      <Button class="title-bar-btn close-btn" :title="t('titleBar.close')" @click="handleClose">
        <FaIcon name="i-ri:close-line" class="text-16px" />
      </Button>
    </div>
  </div>
</template>

<style scoped>
.drag-region {
  -webkit-app-region: drag;
}

.app-region-no-drag {
  -webkit-app-region: no-drag;
}

.title-bar-btn {
  --uno: flex h-full w-46px cursor-pointer items-center justify-center border-none bg-transparent text-[hsl(var(--foreground)) ] transition-background-color-150;
}

.title-bar-btn:hover {
  /* stylelint-disable-next-line @stylistic/function-whitespace-after */
  --uno: bg-[var(--g-sub-sidebar-menu-hover-bg)];
}

.title-bar-btn.close-btn:hover {
  --uno: bg-#e81123 text-white;
}
</style>
