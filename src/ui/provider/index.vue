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
</script>

<template>
  <ConfigProvider :locale="locale" :theme="themeConfig">
    <slot />
  </ConfigProvider>
</template>
