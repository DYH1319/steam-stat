import { useI18n } from 'vue-i18n'
import { toast } from 'vue-sonner'

export const useUpdaterStore = defineStore(
  'updater',
  () => {
    const electronApi = (window as Window).electron
    const { t } = useI18n()

    // 更新相关状态
    const updaterStatus = ref<UpdaterStatus>({} as UpdaterStatus)
    const updateAvailable = ref(false)
    const latestVersion = ref('')
    const downloadProgress = ref(0)
    const downloadSpeed = ref(0)
    const updateDownloaded = ref(false)
    const updateError = ref('')
    const hasCheckedForUpdate = ref(false)

    // 监听更新事件
    function handleUpdateEvent(data: { updaterEvent: string, data?: any }) {
      const { updaterEvent, data: eventData } = data

      switch (updaterEvent) {
        case 'checking-for-update':
          toast.info(t('settings.checkingForUpdates'))
          updaterStatus.value.isChecking = true
          updateError.value = ''
          break

        case 'update-available':
          updaterStatus.value.isChecking = false
          updateAvailable.value = true
          latestVersion.value = eventData.version
          toast.success(t('settings.foundNewVersion', { version: eventData.version }), {
            duration: 3000,
          })
          break

        case 'update-not-available':
          updaterStatus.value.isChecking = false
          updateAvailable.value = false
          hasCheckedForUpdate.value = true
          toast.info(t('settings.alreadyLatest'), {
            duration: 2000,
          })
          break

        case 'download-progress':
          updaterStatus.value.isDownloading = true
          downloadProgress.value = Math.floor(eventData.percent)
          downloadSpeed.value = eventData.bytesPerSecond
          break

        case 'update-downloaded':
          updaterStatus.value.isDownloading = false
          downloadProgress.value = 100
          updateDownloaded.value = true
          toast.success(t('settings.downloadComplete', { version: eventData.version }), {
            duration: 3000,
          })
          break

        case 'update-error':
          updaterStatus.value.isChecking = false
          updaterStatus.value.isDownloading = false
          updateError.value = eventData.message
          toast.error(`${t('settings.updateError')}: ${eventData.message}`)
          break
      }
    }

    // 初始化：注册全局监听器
    function initListener() {
      electronApi.updaterEventOnListener(handleUpdateEvent)
    }

    // 获取更新状态
    async function fetchUpdaterStatus() {
      try {
        updaterStatus.value = await electronApi.updaterGetStatus()
      }
      catch (error: any) {
        console.error('Failed to fetch updater status:', error)
      }
    }

    return {
      updaterStatus,
      updateAvailable,
      latestVersion,
      downloadProgress,
      downloadSpeed,
      updateDownloaded,
      updateError,
      hasCheckedForUpdate,
      initListener,
      fetchUpdaterStatus,
    }
  },
)
