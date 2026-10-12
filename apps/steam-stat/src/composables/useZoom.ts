import { ref } from 'vue'

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
  const electronApi = (window as Window).electron

  // 应用并持久化缩放（由主进程实际执行 setZoomFactor 并写入设置）
  async function applyZoom(factor: number) {
    const target = clamp(factor)
    try {
      await electronApi.settingUpdate({ zoomFactor: target })
      zoomFactor.value = target
    }
    catch {
      zoomFactor.value = target
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
      const settings = await electronApi.settingGet()
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
