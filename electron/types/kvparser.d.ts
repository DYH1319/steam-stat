declare module 'kvparser' {
  /**
   * Parses KeyValues format content (e.g., VDF files)
   * @param content - The string content to parse
   * @returns Parsed object with dynamic structure based on the VDF content
   */
  export function parse(content: string): Record<string, any>

  /**
   * Parses KeyValues format content with specific type
   * @param content - The string content to parse
   * @returns Typed parsed object
   */
  export function parse<T = Record<string, any>>(content: string): T
}
