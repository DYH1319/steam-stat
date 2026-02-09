<script setup lang="ts">
import { ElButton, ElCheckbox, ElDialog, ElRadioGroup } from 'element-plus'
import { useI18n } from 'vue-i18n'
import eventBus from '@/utils/eventBus'

const { t } = useI18n()
const route = useRoute()
const mainPage = useMainPage()
const electronApi = (window as Window).electron

const closeConfirmVisible = ref(false)
const runningAppsWarningVisible = ref(false)

const closeAction = ref<'exit' | 'minimize' | 'ask'>('ask')

const dontAskAgain = ref(false)
const runningAppsCount = ref(0)
const runningAppsAction = ref<'end' | 'discard'>('end')

async function handleClose() {
  // 如果选择了不再询问
  if (dontAskAgain.value && closeAction.value !== 'ask') {
    dontAskAgain.value = false
    await electronApi.settingUpdate({ closeAction: closeAction.value })
    // 如果是设置界面，刷新界面，显示最新的应用关闭行为
    if (route.path === '/setting') {
      // location.reload() // 浏览器原生刷新
      mainPage.reload() // 框架提供的刷新
    }
  }

  if (closeAction.value === 'minimize') {
    closeConfirmVisible.value = false
    runningAppsWarningVisible.value = false
    setTimeout(() => {
      electronApi.windowMinimizeToTray()
    }, 100)
  }

  if (closeAction.value === 'exit') {
    const res = await electronApi.steamGetRunningApps()
    const hasRunningApps = res.apps && res.apps.length > 0
    runningAppsCount.value = res.apps?.length || 0

    // 要求选择如何处理正在运行的应用
    if (hasRunningApps) {
      if (!runningAppsWarningVisible.value) {
        closeConfirmVisible.value = false
        setTimeout(() => {
          runningAppsWarningVisible.value = true
        }, 500)
        return
      }
      else {
        switch (runningAppsAction.value) {
          case 'end':
            await electronApi.steamEndUseAppRecording()
            break
          case 'discard':
            await electronApi.steamDiscardUseAppRecording()
            break
          default:
            return
        }
      }
    }

    electronApi.appQuit()
  }
}

function handleCancel() {
  closeConfirmVisible.value = false
  runningAppsWarningVisible.value = false
}

async function showDialog() {
  closeAction.value = (await electronApi.settingGet()).closeAction
  if (closeAction.value === 'ask') {
    closeConfirmVisible.value = true
  }
  else {
    await handleClose()
  }
}

onMounted(() => {
  eventBus.on('vue:closeConfirm:show', showDialog)
})

onBeforeUnmount(() => {
  eventBus.off('vue:closeConfirm:show', showDialog)
})
</script>

<template>
  <div class="dialog-div">
    <!-- 关闭方式选择对话框 -->
    <ElDialog
      v-if="closeConfirmVisible"
      v-model="closeConfirmVisible"
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
      v-if="runningAppsWarningVisible"
      v-model="runningAppsWarningVisible"
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

  .dialog-div:deep(.el-overlay) {
    z-index: 10000 !important;
  }
</style>
