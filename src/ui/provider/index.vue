<script setup lang="ts">
import { ConfigProvider, theme } from 'ant-design-vue'
import antDesignVueLocaleEnUS from 'ant-design-vue/es/locale/en_US'
import antDesignVueLocaleZhCN from 'ant-design-vue/es/locale/zh_CN'

const settingsStore = useSettingsStore()

const token = ref({
  colorPrimary: '',
  colorInfo: '',
  colorTextBase: '',
  colorBgElevated: '',
  colorBgContainer: '',
  colorBgBase: '',
  borderRadius: 0,
})

const locale = computed(() => {
  return settingsStore.locale === 'zh-CN' ? antDesignVueLocaleZhCN : antDesignVueLocaleEnUS
})

watch([
  () => settingsStore.settings.app.colorScheme,
  () => settingsStore.settings.app.radius,
], () => {
  const rootStyles = getComputedStyle(document.documentElement)
  token.value = {
    colorPrimary: `hsl(${rootStyles.getPropertyValue('--primary')})`,
    colorInfo: `hsl(${rootStyles.getPropertyValue('--primary')})`,
    colorTextBase: `hsl(${rootStyles.getPropertyValue('--foreground')})`,
    colorBgElevated: `hsl(${rootStyles.getPropertyValue('--popover')})`,
    colorBgContainer: `hsl(${rootStyles.getPropertyValue('--card')})`,
    colorBgBase: `hsl(${rootStyles.getPropertyValue('--background')})`,
    borderRadius: Number.parseFloat(rootStyles.getPropertyValue('--radius')) * 16,
  }
}, {
  immediate: true,
})

const themeConfig = computed(() => ({
  algorithm: settingsStore.currentColorScheme === 'dark' ? [theme.darkAlgorithm] : [theme.defaultAlgorithm],
  token: token.value,
}))

// element-plus
const isSupprotColorMix = CSS.supports('color', 'color-mix(in srgb, #fff, #000)')
if (isSupprotColorMix) {
  document.body.style.setProperty('--el-bg-color', 'hsl(var(--background))')
  document.body.style.setProperty('--el-color-primary', 'hsl(var(--primary))')
  document.body.style.setProperty('--el-color-white', 'hsl(var(--primary-foreground))')
  document.body.style.setProperty('--el-color-black', 'hsl(var(--primary-foreground))')
  watch(() => settingsStore.currentColorScheme, (val) => {
    if (val === 'light') {
      for (let index = 1; index < 10; index++) {
        document.body.style.setProperty(`--el-color-primary-light-${index}`, `color-mix(in hsl, hsl(var(--primary)), #fff ${index * 10}%)`)
        document.body.style.setProperty(`--el-color-primary-dark-${index}`, `color-mix(in hsl, hsl(var(--primary)), #000 ${index * 10}%)`)
      }
    }
    else {
      for (let index = 1; index < 10; index++) {
        document.body.style.setProperty(`--el-color-primary-light-${index}`, `color-mix(in hsl, hsl(var(--primary)), #000 ${index * 10}%)`)
        document.body.style.setProperty(`--el-color-primary-dark-${index}`, `color-mix(in hsl, hsl(var(--primary)), #fff ${index * 10}%)`)
      }
    }
  }, {
    immediate: true,
  })
}
</script>

<template>
  <ConfigProvider :locale="locale" :theme="themeConfig">
    <slot />
  </ConfigProvider>
</template>
