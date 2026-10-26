/** 触发浏览器下载（Electron 中会弹出保存对话框） */
export function downloadBlob(fileName: string, blob: Blob) {
  const url = URL.createObjectURL(blob)
  const anchor = document.createElement('a')
  anchor.href = url
  anchor.download = fileName
  document.body.appendChild(anchor)
  anchor.click()
  anchor.remove()
  setTimeout(() => URL.revokeObjectURL(url), 1000)
}

export function downloadDataUrl(fileName: string, dataUrl: string) {
  const anchor = document.createElement('a')
  anchor.href = dataUrl
  anchor.download = fileName
  document.body.appendChild(anchor)
  anchor.click()
  anchor.remove()
}

function csvCell(value: unknown): string {
  const text = value == null ? '' : String(value)
  return /[",\r\n]/.test(text) ? `"${text.replace(/"/g, '""')}"` : text
}

/** 导出 CSV（带 BOM，Excel 可直接识别 UTF-8） */
export function downloadCsv(fileName: string, rows: unknown[][]) {
  const content = rows.map(row => row.map(csvCell).join(',')).join('\r\n')
  downloadBlob(fileName, new Blob([`\uFEFF${content}`], { type: 'text/csv;charset=utf-8' }))
}

export function safeFileName(name: string): string {
  return name.replace(/[\\/:*?"<>|]+/g, '_').trim() || 'export'
}
