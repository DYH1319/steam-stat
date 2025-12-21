import { toast } from 'vue-sonner'
import i18n from '@/i18n'

/**
 * 复制文本到剪贴板
 * @param text 要复制的文本内容
 * @param showToast 是否显示提示消息，默认为 true
 * @param toastDuration 提示消息持续时间（毫秒），默认为 1000
 */
export async function copyToClipboard(text: string, showToast = true, toastDuration = 1000) {
  try {
    await navigator.clipboard.writeText(text)
    if (showToast) {
      toast.success(i18n.global.t('user.copySuccess'), {
        duration: toastDuration,
      })
    }
  }
  catch (e: any) {
    if (showToast) {
      toast.error(`${i18n.global.t('user.copyFailed')}: ${e?.message || e}`)
    }
    throw e
  }
}
