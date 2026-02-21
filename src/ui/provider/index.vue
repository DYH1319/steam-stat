<script setup lang="ts">
import { ConfigProvider, theme } from 'ant-design-vue'
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

watch([
  () => settingsStore.settings.app.colorScheme,
  () => settingsStore.settings.app.radius,
], () => {
  const rootStyles = getComputedStyle(document.documentElement)
  token.value = {
    colorPrimary: rootStyles.getPropertyValue('--primary'),
    colorInfo: rootStyles.getPropertyValue('--primary'),
    colorTextBase: rootStyles.getPropertyValue('--foreground'),
    colorBgElevated: rootStyles.getPropertyValue('--popover'),
    colorBgContainer: rootStyles.getPropertyValue('--card'),
    colorBgBase: rootStyles.getPropertyValue('--background'),
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
  <ConfigProvider :locale="antDesignVueLocaleZhCN" :theme="themeConfig">
    <slot />
  </ConfigProvider>
</template>
