import { ref } from 'vue'
import { getAppSettings, updateAppSettings } from '@/utils/appSettings'

const ZOOM_MIN = 0.5
const ZOOM_MAX = 2.5
const ZOOM_STEP = 0.1
const ZOOM_DEFAULT = 1

// 模块级单例，保证整个应用共享同一份缩放状态
const zoomFactor = ref(ZOOM_DEFAULT)
let initialized = false

function clamp(value: number) {
  const rounded = Math.round(value * 100) / 100
  return Math.min(ZOOM_MAX, Math.max(ZOOM_MIN, rounded))
}

export default function useZoom() {
  // 应用并持久化缩放（由主进程实际执行 setZoomFactor 并写入设置）
  async function applyZoom(factor: number) {
    const target = clamp(factor)
    // 先更新界面缩放值，避免快速连续缩放（Ctrl+滚轮）基于同一个旧值重复计算
    zoomFactor.value = target
    try {
      await updateAppSettings({ zoomFactor: target })
    }
    catch {
      // 持久化失败时保留当前界面缩放
    }
    return zoomFactor.value
  }

  function zoomIn() {
    return applyZoom(zoomFactor.value + ZOOM_STEP)
  }

  function zoomOut() {
    return applyZoom(zoomFactor.value - ZOOM_STEP)
  }

  function zoomReset() {
    return applyZoom(ZOOM_DEFAULT)
  }

  // 从主进程读取已持久化的缩放值（仅同步 UI 显示，主进程已在窗口创建/加载时应用）
  async function initZoom() {
    if (initialized) {
      return zoomFactor.value
    }
    initialized = true
    try {
      const settings = await getAppSettings()
      const factor = settings.zoomFactor
      zoomFactor.value = clamp(typeof factor === 'number' ? factor : ZOOM_DEFAULT)
    }
    catch {
      zoomFactor.value = ZOOM_DEFAULT
    }
    return zoomFactor.value
  }

  return {
    zoomFactor,
    ZOOM_MIN,
    ZOOM_MAX,
    ZOOM_STEP,
    ZOOM_DEFAULT,
    applyZoom,
    zoomIn,
    zoomOut,
    zoomReset,
    initZoom,
  }
}
