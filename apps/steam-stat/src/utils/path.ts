/**
 * 统一 Windows 路径显示格式：盘符大写、分隔符为反斜杠。
 * 例如 "d:/program files/steam" → "D:\program files\steam"
 * 分号拼接的多路径（如 appmanifest 扫描列表）逐段规范化。
 */
export function normalizePath(path: string): string {
  return path
    .split(';')
    .map(segment => segment
      .trim()
      .replaceAll('/', '\\')
      .replace(/^[a-z]:/i, drive => drive.toUpperCase()))
    .join('; ')
}
