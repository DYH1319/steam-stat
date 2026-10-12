/**
 * 深层 Partial 类型，递归地将对象的所有嵌套属性变为可选
 */
export type DeepPartial<T> = T extends object ? {
  [P in keyof T]?: DeepPartial<T[P]>
} : T
