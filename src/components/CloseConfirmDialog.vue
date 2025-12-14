<script setup lang="ts">
import { ElButton, ElCheckbox, ElDialog, ElRadioGroup } from 'element-plus'
import { onMounted, onUnmounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute } from 'vue-router'

const { t } = useI18n()
const route = useRoute()
const mainPage = useMainPage()
const electronApi = (window as any).electron

const visible = ref(false)
const closeAction = ref<'exit' | 'minimize'>('minimize')
const dontAskAgain = ref(false)
const hasRunningApps = ref(false)
const runningAppsCount = ref(0)
const runningAppsAction = ref<'end' | 'discard'>('end')
const showRunningAppsWarning = ref(false)

async function checkRunningApps() {
  try {
    const res = await electronApi.steamGetRunningApps()
    hasRunningApps.value = res.apps && res.apps.length > 0
    runningAppsCount.value = res.apps?.length || 0
  }
  catch (e) {
    console.error('检查运行中应用失败:', e)
    hasRunningApps.value = false
  }
}

async function handleClose() {
  if (dontAskAgain.value) {
    await electronApi.settingsSetCloseAction(closeAction.value)
    // 如果是设置界面，刷新界面
    if (route.path === '/setting') {
      // location.reload() // 浏览器原生刷新
      mainPage.reload() // 框架提供的刷新
    }
  }

  if (closeAction.value === 'exit') {
    if (hasRunningApps.value && !showRunningAppsWarning.value) {
      visible.value = false
      setTimeout(() => {
        showRunningAppsWarning.value = true
      }, 500)
      return
    }

    if (hasRunningApps.value) {
      if (runningAppsAction.value === 'end') {
        await electronApi.useAppRecordEndCurrentRunning()
      }
      else {
        await electronApi.useAppRecordDiscardCurrentRunning()
      }
    }

    await electronApi.appQuit()
  }
  else {
    visible.value = false
    setTimeout(async () => {
      await electronApi.appMinimizeToTray()
    }, 100)
  }

  showRunningAppsWarning.value = false
}

function handleCancel() {
  showRunningAppsWarning.value = false
  visible.value = false
}

async function showDialog(data: { action: 'minimize' | 'exit' | 'ask' | 'ignore' }) {
  await checkRunningApps()
  const action = data.action !== 'ignore' ? data.action : await electronApi.settingsGetCloseAction()
  if (action === 'ask') {
    visible.value = true
  }
  else if (action === 'exit') {
    closeAction.value = 'exit'
    await handleClose()
  }
  else {
    closeAction.value = 'minimize'
    await handleClose()
  }
}

onMounted(() => {
  electronApi.onShowCloseConfirmEvent(showDialog)
})

onUnmounted(() => {
  electronApi.removeShowCloseConfirmEventListener()
})
</script>

<template>
  <div>
    <!-- 关闭方式选择对话框 -->
    <ElDialog
      v-if="visible"
      v-model="visible"
      :title="t('settings.closeConfirmTitle')"
      width="400px"
      :close-on-click-modal="false"
      :close-on-press-escape="false"
    >
      <div class="space-y-4">
        <ElRadioGroup v-model="closeAction" class="flex flex-col items-start gap-4">
          <ElRadio value="exit" border>
            <div class="flex items-center gap-2">
              <span class="i-mdi:exit-to-app inline-block h-5 w-5" />
              <span>{{ t('settings.exitDirectly') }}</span>
            </div>
          </ElRadio>
          <ElRadio value="minimize" border>
            <div class="flex items-center gap-2">
              <span class="i-mdi:tray-arrow-down inline-block h-5 w-5" />
              <span>{{ t('settings.minimizeToTray') }}</span>
            </div>
          </ElRadio>
        </ElRadioGroup>

        <ElCheckbox v-model="dontAskAgain">
          {{ t('settings.dontAskAgain') }}
        </ElCheckbox>
      </div>

      <template #footer>
        <div class="flex justify-end gap-3">
          <ElButton @click="handleCancel">
            {{ t('common.cancel') }}
          </ElButton>
          <ElButton type="primary" @click="handleClose">
            {{ t('common.confirm') }}
          </ElButton>
        </div>
      </template>
    </ElDialog>

    <!-- 运行中应用警告对话框 -->
    <ElDialog
      v-if="showRunningAppsWarning"
      v-model="showRunningAppsWarning"
      :title="t('settings.runningAppsWarningTitle')"
      width="450px"
      :close-on-click-modal="false"
      :close-on-press-escape="false"
    >
      <div class="space-y-4">
        <p class="text-orange-600">
          {{ t('settings.runningAppsWarningMessage', { count: runningAppsCount }) }}
        </p>

        <ElRadioGroup v-model="runningAppsAction" class="flex flex-col gap-4">
          <ElRadio value="end" border>
            <div class="flex items-center gap-2">
              <span class="i-mdi:clock-check inline-block h-5 w-5" />
              <span>{{ t('settings.recordCurrentTime') }}</span>
            </div>
          </ElRadio>
          <ElRadio value="discard" border>
            <div class="flex items-center gap-2">
              <span class="i-mdi:delete inline-block h-5 w-5" />
              <span>{{ t('settings.discardRunningRecords') }}</span>
            </div>
          </ElRadio>
        </ElRadioGroup>
      </div>

      <template #footer>
        <div class="flex justify-end gap-3">
          <ElButton @click="handleCancel">
            {{ t('common.cancel') }}
          </ElButton>
          <ElButton type="warning" @click="handleClose">
            {{ t('common.confirm') }}
          </ElButton>
        </div>
      </template>
    </ElDialog>
  </div>
</template>

<style scoped>
  .el-radio {
    margin-right: 0;
  }
</style>
