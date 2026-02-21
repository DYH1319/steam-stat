<script setup lang="ts">
import { Button, Checkbox, Modal, Radio, RadioGroup } from 'ant-design-vue'
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
    }, 200)
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
  <div>
    <!-- 关闭方式选择对话框 -->
    <Modal
      v-model:open="closeConfirmVisible"
      :title="t('settings.closeConfirmTitle')"
      width="400px"
    >
      <div class="space-y-4">
        <RadioGroup v-model:value="closeAction" option-type="button" class="radio-group flex flex-col items-start gap-4">
          <Radio value="exit">
            <div class="flex items-center gap-2">
              <span class="i-mdi:exit-to-app h-5 w-5" />
              <span>{{ t('settings.exitDirectly') }}</span>
            </div>
          </Radio>
          <Radio value="minimize">
            <div class="flex items-center gap-2">
              <span class="i-mdi:tray-arrow-down h-5 w-5" />
              <span>{{ t('settings.minimizeToTray') }}</span>
            </div>
          </Radio>
        </RadioGroup>

        <Checkbox v-model:checked="dontAskAgain">
          {{ t('settings.dontAskAgain') }}
        </Checkbox>
      </div>

      <template #footer>
        <div class="flex justify-end gap-3">
          <Button @click="handleCancel">
            {{ t('common.cancel') }}
          </Button>
          <Button type="primary" @click="handleClose">
            {{ t('common.confirm') }}
          </Button>
        </div>
      </template>
    </Modal>

    <!-- 运行中应用警告对话框 -->
    <Modal
      v-model:open="runningAppsWarningVisible"
      :title="t('settings.runningAppsWarningTitle')"
      width="450px"
      :mask-closable="false"
    >
      <div class="space-y-4">
        <p class="text-orange-600">
          {{ t('settings.runningAppsWarningMessage', { count: runningAppsCount }) }}
        </p>

        <RadioGroup v-model:value="runningAppsAction" option-type="button" class="radio-group flex flex-col items-start gap-4">
          <Radio value="end">
            <div class="flex items-center gap-2">
              <span class="i-mdi:clock-check h-5 w-5" />
              <span>{{ t('settings.recordCurrentTime') }}</span>
            </div>
          </Radio>
          <Radio value="discard">
            <div class="flex items-center gap-2">
              <span class="i-mdi:delete h-5 w-5" />
              <span>{{ t('settings.discardRunningRecords') }}</span>
            </div>
          </Radio>
        </RadioGroup>
      </div>

      <template #footer>
        <div class="flex justify-end gap-3">
          <Button @click="handleCancel">
            {{ t('common.cancel') }}
          </Button>
          <Button type="primary" danger @click="handleClose">
            {{ t('common.confirm') }}
          </Button>
        </div>
      </template>
    </Modal>
  </div>
</template>

<style scoped>
.radio-group :deep(.ant-radio-button-wrapper) {
  border-width: 1px;
  border-radius: var(--radius);
}

/* 去掉单选按钮间的分割线 */
.radio-group :deep(.ant-radio-button-wrapper:not(:first-child)::before) {
  width: 0;
  background-color: transparent;
}
</style>
