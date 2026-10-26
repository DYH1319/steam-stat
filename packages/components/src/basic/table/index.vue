<script lang="ts">
import type {
  Cell,
  CellContext,
  Column,
  ColumnDef,
  ColumnPinningState,
  ColumnVisibilityState,
  DisplayColumnDef,
  ExpandedState,
  Header,
  HeaderContext,
  Row,
  RowData,
  RowSelectionState,
  SortingState,
  TableOptions,
  Table as TanStackTable,
  Updater,
} from '@tanstack/vue-table'
import type { CSSProperties, HTMLAttributes } from 'vue'
import {
  columnOrderingFeature,
  columnPinningFeature,
  columnSizingFeature,
  columnVisibilityFeature,
  createExpandedRowModel,
  FlexRender,
  rowExpandingFeature,
  rowSelectionFeature,
  rowSortingFeature,
  tableFeatures,
  useTable,
} from '@tanstack/vue-table'
import { computed, ref, watch } from 'vue'
import { cn } from '#utils'
import Button from '../../basic/button/index.vue'
import { Checkbox } from '../../basic/checkbox/checkbox'
import Dropdown from '../../basic/dropdown/index.vue'
import Icon from '../../basic/icon/index.vue'
import { RadioGroup, RadioGroupItem } from '../../basic/radio-group/radio-group'
import {
  Table,
  TableBody,
  TableCaption,
  TableCell,
  TableEmpty,
  TableHead,
  TableHeader,
  TableRow,
} from './table'

const SELECTION_COLUMN_ID = '__fa_table_selection__'
const TABLE_HEADER_ROW_HEIGHT = 40
const faTableFeatures = tableFeatures({
  columnOrderingFeature,
  columnSizingFeature,
  columnPinningFeature,
  columnVisibilityFeature,
  rowExpandingFeature,
  expandedRowModel: createExpandedRowModel(),
  rowSelectionFeature,
  rowSortingFeature,
})

type FaTableFeatures = typeof faTableFeatures

type ColumnSlotName = `cell-${string}` | `header-${string}` | `footer-${string}`
type TableSlots<TData extends RowData> = {
  caption?: () => any
  empty?: (props: TableEmptySlotProps<TData>) => any
  toolbar?: (props: TableToolbarSlotProps<TData>) => any
} & {
  [key: `cell-${string}`]: ((props: TableCellSlotProps<TData, any>) => any) | undefined
} & {
  [key: `header-${string}`]: ((props: TableHeaderSlotProps<TData, any>) => any) | undefined
} & {
  [key: `footer-${string}`]: ((props: TableFooterSlotProps<TData, any>) => any) | undefined
}
type ColumnClass<TContext> = HTMLAttributes['class'] | ((context: TContext) => HTMLAttributes['class'])
type RowClass<TData extends RowData> = HTMLAttributes['class'] | ((row: Row<FaTableFeatures, TData>) => HTMLAttributes['class'])
type RowKey<TData extends RowData> = keyof TData | string | ((row: TData, index: number) => string | number)
type TablePinnedPosition = 'start' | 'end'

export type TableAlign = 'left' | 'center' | 'right'
export type TableColumnFixed = 'left' | 'right'
export type TableSelectionDisabled<TData extends RowData = RowData> = (row: TData, index: number) => boolean
export type TableSortingState = SortingState
export type TableExpandedState = ExpandedState

interface TableColumnOptions<TData extends RowData = RowData, TValue = unknown> {
  align?: TableAlign
  cellClass?: ColumnClass<CellContext<FaTableFeatures, TData, TValue>>
  class?: HTMLAttributes['class']
  fixed?: boolean | TableColumnFixed
  headerClass?: ColumnClass<HeaderContext<FaTableFeatures, TData, TValue>>
  label?: string
  maxWidth?: number | string
  minWidth?: number | string
  title?: string
  width?: number | string
}

export type TableSelectionColumn<TData extends RowData = RowData>
  = Partial<DisplayColumnDef<FaTableFeatures, TData, unknown>>
    & TableColumnOptions<TData, unknown>
    & {
      type: 'selection'
      disabled?: TableSelectionDisabled<TData>
      columns?: never
    }

export type TableColumn<TData extends RowData = RowData, TValue = unknown>
  = | (ColumnDef<FaTableFeatures, TData, TValue> & TableColumnOptions<TData, TValue> & {
    type?: never
    columns?: TableColumn<TData, any>[]
  })
  | TableSelectionColumn<TData>

export interface TableCellSlotProps<TData extends RowData = RowData, TValue = unknown> {
  cell: Cell<FaTableFeatures, TData, TValue>
  column: Column<FaTableFeatures, TData, TValue>
  index: number
  row: Row<FaTableFeatures, TData>
  table: TanStackTable<FaTableFeatures, TData>
  value: TValue
}

export interface TableHeaderSlotProps<TData extends RowData = RowData, TValue = unknown> {
  column: Column<FaTableFeatures, TData, TValue>
  header: Header<FaTableFeatures, TData, TValue>
  table: TanStackTable<FaTableFeatures, TData>
}

export interface TableFooterSlotProps<TData extends RowData = RowData, TValue = unknown> {
  column: Column<FaTableFeatures, TData, TValue>
  header: Header<FaTableFeatures, TData, TValue>
  table: TanStackTable<FaTableFeatures, TData>
}

export interface TableEmptySlotProps<TData extends RowData = RowData> {
  table: TanStackTable<FaTableFeatures, TData>
}

export interface TableToolbarSlotProps<TData extends RowData = RowData> {
  table: TanStackTable<FaTableFeatures, TData>
}

export interface TableProps<TData extends RowData = RowData> {
  border?: boolean
  bodyClass?: HTMLAttributes['class']
  caption?: string
  class?: HTMLAttributes['class']
  columnVisibility?: boolean
  columns: TableColumn<TData, any>[]
  data: TData[]
  defaultExpanded?: ExpandedState
  defaultSorting?: SortingState
  emptyText?: string
  enableMultiSort?: boolean
  enableSortingRemoval?: boolean
  expanded?: ExpandedState
  getRowId?: TableOptions<FaTableFeatures, TData>['getRowId']
  getSubRows?: TableOptions<FaTableFeatures, TData>['getSubRows']
  headerClass?: HTMLAttributes['class']
  headerRowClass?: HTMLAttributes['class']
  indentSize?: number
  manualExpanding?: boolean
  multiple?: boolean
  rowClass?: RowClass<TData>
  rowKey?: RowKey<TData>
  selectable?: boolean
  selectionColumnClass?: HTMLAttributes['class']
  sortable?: boolean
  sorting?: SortingState
  sortDescFirst?: boolean
  stripe?: boolean
  tableClass?: HTMLAttributes['class']
  tableRootClass?: HTMLAttributes['class']
  tree?: boolean
}
</script>

<script setup lang="ts" generic="TData extends RowData = RowData">
defineOptions({
  name: 'BuiltInTable',
})

const props = withDefaults(defineProps<TableProps<TData>>(), {
  border: false,
  caption: undefined,
  columnVisibility: false,
  defaultExpanded: undefined,
  enableMultiSort: true,
  enableSortingRemoval: true,
  expanded: undefined,
  emptyText: '暂无数据',
  indentSize: 20,
  manualExpanding: false,
  multiple: false,
  selectable: false,
  sortable: false,
  sortDescFirst: true,
  stripe: false,
  tree: false,
})

const emit = defineEmits<{
  'expandedChange': [expanded: ExpandedState, table: TanStackTable<FaTableFeatures, TData>]
  'rowClick': [row: TData, index: number, event: MouseEvent]
  'selectionChange': [rows: TData[], rowSelection: RowSelectionState]
  'sortingChange': [sorting: SortingState, table: TanStackTable<FaTableFeatures, TData>]
  'update:expanded': [expanded: ExpandedState]
}>()

const slots = defineSlots<TableSlots<TData>>()

const rowSelection = ref<RowSelectionState>({})
const columnVisibilityState = ref<ColumnVisibilityState>({})
const sortingState = ref<SortingState>(props.sorting ?? props.defaultSorting ?? [])
const expandedState = ref<ExpandedState>(props.expanded ?? props.defaultExpanded ?? {})
const isSortingControlled = computed(() => props.sorting !== undefined)
const isExpandedControlled = computed(() => props.expanded !== undefined)

function isSelectionColumnDef(column: TableColumn<TData, any>): column is TableSelectionColumn<TData> {
  return column.type === 'selection'
}

function normalizeFixed(value?: boolean | TableColumnFixed): TablePinnedPosition | undefined {
  if (value === true) {
    return 'start'
  }

  if (value === 'left') {
    return 'start'
  }

  if (value === 'right') {
    return 'end'
  }

  return undefined
}

function parseNumericWidth(width?: number | string) {
  if (typeof width === 'number') {
    return width
  }

  if (typeof width === 'string') {
    const matched = width.trim().match(/^(\d+(?:\.\d+)?)px$/)
    return matched ? Number(matched[1]) : undefined
  }

  return undefined
}

function getColumnId(column: TableColumn<TData, any>): string | undefined {
  if (isSelectionColumnDef(column)) {
    return column.id ? String(column.id) : SELECTION_COLUMN_ID
  }

  if (column.id) {
    return String(column.id)
  }

  if ('accessorKey' in column && column.accessorKey) {
    return String(column.accessorKey).replaceAll('.', '_')
  }

  if (typeof column.header === 'string') {
    return column.header
  }

  return undefined
}

function normalizeColumn(column: TableColumn<TData, any>): TableColumn<TData, any> {
  const normalized = { ...column }

  if (isSelectionColumnDef(normalized)) {
    normalized.id ??= SELECTION_COLUMN_ID
    normalized.header ??= ''
    normalized.cell ??= ''
    normalized.align ??= 'center'
    normalized.enableHiding ??= false
    normalized.enableSorting ??= false
    normalized.size ??= parseNumericWidth(normalized.width) ?? 40
    normalized.minSize ??= parseNumericWidth(normalized.minWidth)
    normalized.maxSize ??= parseNumericWidth(normalized.maxWidth)
    normalized.width ??= normalized.size

    return normalized
  }

  normalized.header ??= normalized.label ?? normalized.title
  normalized.enableSorting ??= false
  normalized.size ??= parseNumericWidth(normalized.width)
  normalized.minSize ??= parseNumericWidth(normalized.minWidth)
  normalized.maxSize ??= parseNumericWidth(normalized.maxWidth)

  if (normalized.columns?.length) {
    normalized.columns = normalized.columns.map(item => normalizeColumn(item))
  }

  return normalized
}

const normalizedColumns = computed<TableColumn<TData, any>[]>(() => props.columns.map(column => normalizeColumn(column)))

const resolvedColumns = computed<ColumnDef<FaTableFeatures, TData, any>[]>(() => normalizedColumns.value as ColumnDef<FaTableFeatures, TData, any>[])

function findSelectionColumn(columns: TableColumn<TData, any>[]): TableSelectionColumn<TData> | undefined {
  for (const column of columns) {
    if (isSelectionColumnDef(column)) {
      return column
    }

    if (column.columns?.length) {
      const selectionColumn = findSelectionColumn(column.columns)
      if (selectionColumn) {
        return selectionColumn
      }
    }
  }
}

const selectionColumnDef = computed(() => findSelectionColumn(normalizedColumns.value))

function getColumnPinningState(columns: TableColumn<TData, any>[]) {
  const start = new Set<string>()
  const end = new Set<string>()

  function collect(items: TableColumn<TData, any>[], inheritedFixed?: TablePinnedPosition) {
    items.forEach((column) => {
      const fixed = normalizeFixed(column.fixed) ?? inheritedFixed

      if (column.columns?.length) {
        collect(column.columns, fixed)
        return
      }

      const columnId = getColumnId(column)
      if (!columnId || !fixed) {
        return
      }

      if (fixed === 'start') {
        start.add(columnId)
        end.delete(columnId)
      }
      else {
        end.add(columnId)
        start.delete(columnId)
      }
    })
  }

  collect(columns)

  return {
    start: [...start],
    end: [...end],
  } satisfies ColumnPinningState
}

const columnPinning = computed<ColumnPinningState>(() => getColumnPinningState(normalizedColumns.value))

function resolveRowId(row: TData, index: number, parent?: Row<FaTableFeatures, TData>): string {
  if (props.getRowId) {
    return props.getRowId(row, index, parent)
  }

  if (typeof props.rowKey === 'function') {
    return String(props.rowKey(row, index))
  }

  if (props.rowKey) {
    const value = (row as Record<string, unknown>)[String(props.rowKey)]
    if (value != null) {
      return String(value)
    }
  }

  return parent ? `${parent.id}.${index}` : String(index)
}

function resolveSubRows(row: TData, index: number): readonly TData[] | undefined {
  if (!props.tree) {
    return undefined
  }

  if (props.getSubRows) {
    return props.getSubRows(row, index)
  }

  const children = (row as Record<string, unknown>).children
  return Array.isArray(children) ? children as TData[] : undefined
}

function isRowSelectionDisabled(row: Row<FaTableFeatures, TData>) {
  return selectionColumnDef.value?.disabled?.(row.original, row.index) ?? false
}

function syncSingleRowSelection() {
  if (props.multiple) {
    return
  }

  const selectedIds = Object.keys(rowSelection.value).filter(id => rowSelection.value[id])
  if (selectedIds.length <= 1) {
    return
  }

  const selectedId = selectedIds.at(-1)
  rowSelection.value = selectedId ? { [selectedId]: true } : {}
}

function handleRowSelectionChange(updaterOrValue: Updater<RowSelectionState>) {
  rowSelection.value = typeof updaterOrValue === 'function'
    ? updaterOrValue(rowSelection.value)
    : updaterOrValue
  syncSingleRowSelection()
}

function handleColumnVisibilityChange(updaterOrValue: Updater<ColumnVisibilityState>) {
  columnVisibilityState.value = typeof updaterOrValue === 'function'
    ? updaterOrValue(columnVisibilityState.value)
    : updaterOrValue
}

function handleSortingChange(updaterOrValue: Updater<SortingState>) {
  const nextSorting = typeof updaterOrValue === 'function'
    ? updaterOrValue(sortingState.value)
    : updaterOrValue

  if (!isSortingControlled.value) {
    sortingState.value = nextSorting
  }

  emit('sortingChange', nextSorting, table)
}

function handleExpandedChange(updaterOrValue: Updater<ExpandedState>) {
  const nextExpanded = typeof updaterOrValue === 'function'
    ? updaterOrValue(expandedState.value)
    : updaterOrValue

  if (!isExpandedControlled.value) {
    expandedState.value = nextExpanded
  }

  emit('update:expanded', nextExpanded)
  emit('expandedChange', nextExpanded, table)
}

const tableState = computed(() => ({
  columnVisibility: columnVisibilityState.value,
  columnPinning: columnPinning.value,
  rowSelection: rowSelection.value,
  expanded: expandedState.value,
  sorting: sortingState.value,
}))

const table = useTable<FaTableFeatures, TData>({
  features: faTableFeatures,
  data: computed(() => props.data),
  get enableMultiSort() {
    return props.enableMultiSort
  },
  get enableMultiRowSelection() {
    return props.multiple
  },
  get enableSorting() {
    return props.sortable
  },
  get enableSortingRemoval() {
    return props.enableSortingRemoval
  },
  enableRowSelection: row => props.selectable && Boolean(selectionColumnDef.value) && !isRowSelectionDisabled(row),
  get columns() {
    return resolvedColumns.value
  },
  getRowId: resolveRowId,
  getSubRows: resolveSubRows,
  get manualExpanding() {
    return props.manualExpanding
  },
  manualSorting: true,
  onColumnVisibilityChange: handleColumnVisibilityChange,
  onRowSelectionChange: handleRowSelectionChange,
  onExpandedChange: handleExpandedChange,
  onSortingChange: handleSortingChange,
  get sortDescFirst() {
    return props.sortDescFirst
  },
  state: tableState,
})

const selectedRows = computed(() => table.getSelectedRowModel().rows.map(row => row.original))
const tableRows = computed(() => {
  void tableState.value
  return table.getRowModel().rows
})
const visibleColumnCount = computed(() => {
  void tableState.value.columnVisibility
  return Math.max(table.getVisibleLeafColumns().length, 1)
})
const isSingleSelectionMode = computed(() => props.selectable && !props.multiple && Boolean(selectionColumnDef.value))
const singleSelectedRowId = computed(() => Object.keys(rowSelection.value).find(id => rowSelection.value[id]))
const tableRootComponent = computed(() => isSingleSelectionMode.value ? RadioGroup : 'div')
const tableRootModelValue = computed(() => isSingleSelectionMode.value ? singleSelectedRowId.value : undefined)
const stripeInteractionClass = computed(() => props.stripe ? '[&:hover>td]:bg-muted/50 [&[data-state=selected]>td]:bg-muted' : undefined)
const hasToolbar = computed(() => props.columnVisibility || Boolean(slots.toolbar))
const columnVisibilityColumns = computed(() => {
  void resolvedColumns.value
  void tableState.value.columnVisibility

  return table.getAllLeafColumns().filter(column => column.getCanHide())
})
const columnVisibilityMenuItems = computed(() => [
  columnVisibilityColumns.value.length
    ? columnVisibilityColumns.value.map(column => ({
        type: 'checkbox' as const,
        label: getColumnVisibilityLabel(column),
        checked: column.getIsVisible(),
        handle: (checked?: boolean) => column.toggleVisibility(checked),
      }))
    : [],
])

watch(
  () => props.multiple,
  () => {
    syncSingleRowSelection()
  },
  { immediate: true },
)

watch(
  () => props.sorting,
  (sorting) => {
    if (sorting !== undefined) {
      sortingState.value = sorting
    }
  },
  { deep: true },
)

watch(
  () => props.expanded,
  (expanded) => {
    if (expanded !== undefined) {
      expandedState.value = expanded
    }
  },
  { deep: true },
)

watch(
  [rowSelection, () => props.data],
  () => {
    emit('selectionChange', selectedRows.value, rowSelection.value)
  },
  { deep: true },
)

function isSelectionColumn(column: Column<FaTableFeatures, TData, unknown>) {
  return isSelectionColumnDef(column.columnDef as TableColumn<TData, unknown>)
}

function getColumnSlotName(prefix: 'cell' | 'header' | 'footer', columnId: string): ColumnSlotName {
  return `${prefix}-${columnId}`
}

function getHeaderSlotName(header: Header<FaTableFeatures, TData, unknown>) {
  return getColumnSlotName('header', header.column.id)
}

function getCellSlotName(cell: Cell<FaTableFeatures, TData, unknown>) {
  return getColumnSlotName('cell', cell.column.id)
}

function hasColumnSlot(name: ColumnSlotName) {
  return Boolean(slots[name])
}

function getColumnVisibilityLabel(column: Column<FaTableFeatures, TData, unknown>) {
  const columnDef = column.columnDef as TableColumn<TData, unknown>

  if (columnDef.label) {
    return columnDef.label
  }

  if (columnDef.title) {
    return columnDef.title
  }

  if (typeof columnDef.header === 'string' && columnDef.header) {
    return columnDef.header
  }

  return column.id
}

function getHeaderSlotProps(header: Header<FaTableFeatures, TData, unknown>): any {
  return {
    column: header.column,
    header,
    table,
  }
}

function getHeaderAriaSort(header: Header<FaTableFeatures, TData, unknown>) {
  if (!props.sortable || !header.column.getCanSort()) {
    return undefined
  }

  const sorted = header.column.getIsSorted()

  if (sorted === 'asc') {
    return 'ascending'
  }

  if (sorted === 'desc') {
    return 'descending'
  }

  return undefined
}

function getCellSlotProps(cell: Cell<FaTableFeatures, TData, unknown>, row: Row<FaTableFeatures, TData>): any {
  return {
    cell,
    column: cell.column,
    index: row.index,
    row,
    table,
    value: cell.getValue(),
  }
}

function shouldRenderTreeCell(cell: Cell<FaTableFeatures, TData, unknown>, row: Row<FaTableFeatures, TData>) {
  if (!props.tree || isSelectionColumn(cell.column)) {
    return false
  }

  return row.getVisibleCells().find(item => !isSelectionColumn(item.column))?.id === cell.id
}

function getTreeCellIndentStyle(row: Row<FaTableFeatures, TData>): CSSProperties {
  return {
    paddingLeft: `${row.depth * props.indentSize}px`,
  }
}

function getTreeToggleIconName(row: Row<FaTableFeatures, TData>) {
  return row.getIsExpanded() ? 'i-lucide:chevron-down' : 'i-lucide:chevron-right'
}

function handleToggleRowExpanded(row: Row<FaTableFeatures, TData>) {
  row.toggleExpanded()
}

function getAlignClass(align?: TableAlign) {
  if (align === 'center') {
    return 'text-center'
  }

  if (align === 'right') {
    return 'text-right'
  }

  return undefined
}

function resolveSize(size?: number | string) {
  if (size == null || size === '') {
    return undefined
  }

  return typeof size === 'number' ? `${size}px` : size
}

function getColumnStyle(column: Column<FaTableFeatures, TData, unknown>): CSSProperties | undefined {
  const columnDef = column.columnDef as TableColumn<TData, unknown>
  const pin = column.getIsPinned()
  const width = resolveSize(columnDef.width ?? (pin ? column.getSize() : columnDef.size && columnDef.size !== 150 ? columnDef.size : undefined))
  const minSize = columnDef.minSize && columnDef.minSize !== 20 ? columnDef.minSize : undefined
  const maxSize = columnDef.maxSize && columnDef.maxSize !== Number.MAX_SAFE_INTEGER ? columnDef.maxSize : undefined
  const minWidth = resolveSize(columnDef.minWidth ?? minSize)
  const maxWidth = resolveSize(columnDef.maxWidth ?? maxSize)
  const style: CSSProperties = {}

  if (width) {
    style.width = width
    style.minWidth = minWidth ?? width
  }
  else if (minWidth) {
    style.minWidth = minWidth
  }

  if (maxWidth) {
    style.maxWidth = maxWidth
  }

  if (pin === 'start') {
    style.insetInlineStart = `${column.getStart('start')}px`
    style.position = 'sticky'
  }
  else if (pin === 'end') {
    style.position = 'sticky'
    style.insetInlineEnd = `${column.getAfter('end')}px`
  }

  if (!Object.keys(style).length) {
    return undefined
  }

  return style
}

function resolveColumnClass<TContext>(
  columnClass: ColumnClass<TContext> | undefined,
  context: TContext,
) {
  return typeof columnClass === 'function' ? columnClass(context) : columnClass
}

function getHeaderClass(header: Header<FaTableFeatures, TData, unknown>) {
  const columnDef = header.column.columnDef as TableColumn<TData, unknown>
  return cn(
    'sticky z-2 bg-muted text-muted-foreground after:pointer-events-none after:absolute after:inset-x-0 after:bottom-0 after:h-px after:bg-border after:content-empty',
    getAlignClass(columnDef.align),
    getHeaderBorderClass(header),
    getPinnedColumnClass(header.column, 'header'),
    header.column.getCanSort() && 'cursor-pointer select-none',
    getSortedColumnClass(header.column, 'header'),
    columnDef.class,
    resolveColumnClass(columnDef.headerClass, header.getContext()),
    isSelectionColumn(header.column) && cn('px-3', props.selectionColumnClass),
  )
}

function getHeaderStyle(header: Header<FaTableFeatures, TData, unknown>, headerGroupIndex: number): CSSProperties {
  return {
    ...getColumnStyle(header.column),
    top: `${headerGroupIndex * TABLE_HEADER_ROW_HEIGHT}px`,
  }
}

function getHeaderContentClass(header: Header<FaTableFeatures, TData, unknown>) {
  const columnDef = header.column.columnDef as TableColumn<TData, unknown>

  return cn(
    'inline-flex w-full items-center gap-1.5',
    columnDef.align === 'center' && 'justify-center',
    columnDef.align === 'right' && 'justify-end',
  )
}

function getCellClass(cell: Cell<FaTableFeatures, TData, unknown>) {
  const columnDef = cell.column.columnDef as TableColumn<TData, unknown>
  return cn(
    getAlignClass(columnDef.align),
    getCellBorderClass(cell),
    getPinnedColumnClass(cell.column, 'cell'),
    getSortedColumnClass(cell.column, 'cell'),
    columnDef.class,
    resolveColumnClass(columnDef.cellClass, cell.getContext()),
    isSelectionColumn(cell.column) && cn('px-3', props.selectionColumnClass),
  )
}

function getSortedColumnClass(column: Column<FaTableFeatures, TData, unknown>, area: 'cell' | 'header') {
  if (!props.sortable || !column.getIsSorted()) {
    return undefined
  }

  return area === 'header'
    ? '!bg-primary/20 text-foreground'
    : '!bg-primary/10'
}

function getPinnedColumnClass(column: Column<FaTableFeatures, TData, unknown>, area: 'cell' | 'header') {
  const pin = column.getIsPinned()

  if (!pin) {
    return undefined
  }

  return cn(
    'sticky',
    area === 'header' ? 'z-3' : 'z-1',
    area === 'cell' && 'bg-background transition-colors',
    pin === 'start' && column.getIsLastColumn('start') && 'shadow-[4px_0_6px_-4px_rgb(0_0_0/0.18)]',
    pin === 'end' && column.getIsFirstColumn('end') && 'shadow-[-4px_0_6px_-4px_rgb(0_0_0/0.18)]',
  )
}

function isLastVisibleLeafColumn(column: Column<FaTableFeatures, TData, unknown>) {
  return table.getVisibleLeafColumns().at(-1)?.id === column.id
}

function getNextVisibleLeafColumn(column: Column<FaTableFeatures, TData, unknown>) {
  const columns = table.getVisibleLeafColumns()
  const columnIndex = columns.findIndex(item => item.id === column.id)
  return columnIndex === -1 ? undefined : columns[columnIndex + 1]
}

function getVisiblePinnedColumns(pin: TablePinnedPosition) {
  return table.getPinnedVisibleLeafColumns(pin)
}

function isFirstVisiblePinnedColumn(column: Column<FaTableFeatures, TData, unknown>, pin: TablePinnedPosition) {
  return getVisiblePinnedColumns(pin).at(0)?.id === column.id
}

function isLastVisiblePinnedColumn(column: Column<FaTableFeatures, TData, unknown>, pin: TablePinnedPosition) {
  return getVisiblePinnedColumns(pin).at(-1)?.id === column.id
}

function getHeaderBorderClass(header: Header<FaTableFeatures, TData, unknown>) {
  if (!props.border) {
    return undefined
  }

  const leafColumns = header.column.getLeafColumns()
  const lastLeafColumn = leafColumns.at(-1) ?? header.column
  const pin = lastLeafColumn.getIsPinned()

  if (pin === 'start') {
    return cn(
      !isFirstVisiblePinnedColumn(lastLeafColumn, 'start')
      && 'before:pointer-events-none before:absolute before:inset-y-0 before:left-0 before:z-1 before:w-px before:bg-border before:content-empty',
      isLastVisiblePinnedColumn(lastLeafColumn, 'start') && !isLastVisibleLeafColumn(lastLeafColumn)
      && 'bg-[linear-gradient(to_left,oklch(var(--border))_1px,transparent_1px)]',
    )
  }

  if (pin === 'end') {
    return 'before:pointer-events-none before:absolute before:inset-y-0 before:left-0 before:z-1 before:w-px before:bg-border before:content-empty'
  }

  if (getNextVisibleLeafColumn(lastLeafColumn)?.getIsPinned() === 'end') {
    return undefined
  }

  return isLastVisibleLeafColumn(lastLeafColumn) ? undefined : 'border-r'
}

function getCellBorderClass(cell: Cell<FaTableFeatures, TData, unknown>) {
  if (!props.border) {
    return undefined
  }

  const pin = cell.column.getIsPinned()

  if (pin === 'start') {
    return cn(
      !isFirstVisiblePinnedColumn(cell.column, 'start')
      && 'before:pointer-events-none before:absolute before:inset-y-0 before:left-0 before:z-1 before:w-px before:bg-border before:content-empty',
      isLastVisiblePinnedColumn(cell.column, 'start') && !isLastVisibleLeafColumn(cell.column)
      && 'after:pointer-events-none after:absolute after:inset-y-0 after:right-0 after:z-1 after:w-px after:bg-border after:content-empty',
    )
  }

  if (pin === 'end') {
    return 'after:pointer-events-none after:absolute after:inset-y-0 after:left-0 after:z-1 after:w-px after:bg-border after:content-empty'
  }

  if (getNextVisibleLeafColumn(cell.column)?.getIsPinned() === 'end') {
    return undefined
  }

  return isLastVisibleLeafColumn(cell.column) ? undefined : 'border-r'
}

function getRowClass(row: Row<FaTableFeatures, TData>, rowIndex: number) {
  return cn(
    'bg-background',
    '[&:hover>td[data-pinned]]:bg-muted [&[data-state=selected]>td[data-pinned]]:bg-muted',
    props.stripe && rowIndex % 2 === 1 && '[&>td]:bg-muted/50',
    props.stripe && rowIndex % 2 === 1 && '[&>td[data-pinned]]:bg-muted',
    stripeInteractionClass.value,
    typeof props.rowClass === 'function' ? props.rowClass(row) : props.rowClass,
  )
}

function getHeaderCheckboxValue() {
  if (table.getIsAllRowsSelected()) {
    return true
  }

  if (table.getIsSomeRowsSelected() && !table.getIsAllRowsSelected()) {
    return 'indeterminate'
  }

  return false
}

function shouldRenderSelectionHeader(header: Header<FaTableFeatures, TData, unknown>) {
  return props.selectable && props.multiple && isSelectionColumn(header.column) && !header.column.columnDef.header
}

function getSortIconName(header: Header<FaTableFeatures, TData, unknown>) {
  const sorted = header.column.getIsSorted()

  if (sorted === 'desc') {
    return 'i-lucide:arrow-down'
  }

  if (sorted === 'asc') {
    return 'i-lucide:arrow-up'
  }

  return 'i-lucide:arrow-up-down'
}

function getSortIconClass(header: Header<FaTableFeatures, TData, unknown>) {
  return cn(
    'shrink-0 size-3.5',
    header.column.getIsSorted() ? 'text-primary' : 'text-muted-foreground/80',
  )
}

function shouldRenderSortIndex(header: Header<FaTableFeatures, TData, unknown>) {
  return props.enableMultiSort && header.column.getSortIndex() > -1 && sortingState.value.length > 1
}

function getSortIndexLabel(header: Header<FaTableFeatures, TData, unknown>) {
  return header.column.getSortIndex() + 1
}

function handleHeaderClick(header: Header<FaTableFeatures, TData, unknown>, event: MouseEvent) {
  if (!props.sortable || !header.column.getCanSort()) {
    return
  }

  header.column.getToggleSortingHandler()?.(event)
}

function handleToggleAllRows(value: boolean | 'indeterminate') {
  table.toggleAllRowsSelected(value === true)
}

function handleToggleRow(row: Row<FaTableFeatures, TData>, value: boolean | 'indeterminate') {
  row.toggleSelected(value === true)
}

function handleSingleSelectionChange(value: unknown) {
  if (value == null) {
    return
  }

  rowSelection.value = {
    [String(value)]: true,
  }
}

function handleRowClick(row: Row<FaTableFeatures, TData>, event: MouseEvent) {
  emit('rowClick', row.original, row.index, event)
}

defineExpose({
  table,
})
</script>

<template>
  <div :class="cn('flex flex-col gap-2', props.class)">
    <div v-if="hasToolbar" class="flex gap-2 items-center">
      <div class="flex flex-1 gap-2 min-w-0 items-center">
        <slot name="toolbar" :table="table" />
      </div>
      <Dropdown v-if="props.columnVisibility" :items="columnVisibilityMenuItems" align="end">
        <Button
          variant="outline"
          size="icon"
          :disabled="!columnVisibilityColumns.length"
        >
          <Icon name="i-lucide:columns-3" />
        </Button>
      </Dropdown>
    </div>
    <component
      :is="tableRootComponent"
      :class="cn('size-full', props.border && 'border rounded-lg overflow-hidden', props.tableRootClass)"
      :model-value="tableRootModelValue"
      @update:model-value="handleSingleSelectionChange"
    >
      <Table :class="cn(tableClass, !tableRows.length && 'h-full')">
        <slot name="caption">
          <TableCaption v-if="caption">
            {{ caption }}
          </TableCaption>
        </slot>

        <TableHeader :class="headerClass">
          <TableRow
            v-for="(headerGroup, headerGroupIndex) in table.getHeaderGroups()"
            :key="headerGroup.id"
            :class="cn('border-b-0', headerRowClass)"
          >
            <TableHead
              v-for="header in headerGroup.headers"
              :key="header.id"
              :colspan="header.colSpan"
              :class="getHeaderClass(header)"
              :style="getHeaderStyle(header, headerGroupIndex)"
              :aria-sort="getHeaderAriaSort(header)"
              :data-sortable="props.sortable && header.column.getCanSort() ? '' : undefined"
              @click="handleHeaderClick(header, $event)"
            >
              <template v-if="!header.isPlaceholder">
                <div :class="getHeaderContentClass(header)">
                  <slot
                    v-if="hasColumnSlot(getHeaderSlotName(header))"
                    :name="getHeaderSlotName(header)"
                    v-bind="getHeaderSlotProps(header)"
                  />
                  <Checkbox
                    v-else-if="shouldRenderSelectionHeader(header)"
                    :model-value="getHeaderCheckboxValue()"
                    class="translate-y-[2px]"
                    @click.stop
                    @update:model-value="handleToggleAllRows"
                  />
                  <FlexRender
                    v-else
                    :header="header"
                  />
                  <template v-if="props.sortable && header.column.getCanSort()">
                    <Icon :name="getSortIconName(header)" :class="getSortIconClass(header)" />
                    <span
                      v-if="shouldRenderSortIndex(header)"
                      class="text-[10px] text-muted-foreground leading-none font-medium rounded-full bg-muted inline-flex size-4 items-center justify-center"
                    >
                      {{ getSortIndexLabel(header) }}
                    </span>
                  </template>
                </div>
              </template>
            </TableHead>
          </TableRow>
        </TableHeader>

        <TableBody :class="bodyClass">
          <template v-if="tableRows.length">
            <TableRow
              v-for="(row, rowIndex) in tableRows"
              :key="row.id"
              :class="getRowClass(row, rowIndex)"
              :data-state="row.getIsSelected() ? 'selected' : undefined"
              @click="handleRowClick(row, $event)"
            >
              <TableCell
                v-for="cell in row.getVisibleCells()"
                :key="cell.id"
                :class="getCellClass(cell)"
                :style="getColumnStyle(cell.column)"
                :data-pinned="cell.column.getIsPinned() || undefined"
              >
                <div
                  v-if="shouldRenderTreeCell(cell, row)"
                  class="flex gap-1.5 min-w-0 items-center"
                  :style="getTreeCellIndentStyle(row)"
                >
                  <Button
                    v-if="row.getCanExpand()"
                    type="button"
                    variant="ghost"
                    size="icon"
                    class="shrink-0 size-6"
                    @click.stop="handleToggleRowExpanded(row)"
                  >
                    <Icon :name="getTreeToggleIconName(row)" class="size-3.5" />
                  </Button>
                  <span v-else class="shrink-0 size-6" aria-hidden="true" />
                  <div class="flex-1 min-w-0">
                    <slot
                      v-if="hasColumnSlot(getCellSlotName(cell))"
                      :name="getCellSlotName(cell)"
                      v-bind="getCellSlotProps(cell, row)"
                    />
                    <template v-else-if="isSelectionColumn(cell.column) && selectable">
                      <Checkbox
                        v-if="multiple"
                        :model-value="row.getIsSelected()"
                        :disabled="!row.getCanSelect()"
                        class="translate-y-[2px]"
                        @click.stop
                        @update:model-value="value => handleToggleRow(row, value)"
                      />
                      <RadioGroupItem
                        v-else
                        :value="row.id"
                        :disabled="!row.getCanSelect()"
                        class="translate-y-[2px]"
                        @click.stop
                      />
                    </template>
                    <FlexRender
                      v-else
                      :cell="cell"
                    />
                  </div>
                </div>
                <template v-else>
                  <slot
                    v-if="hasColumnSlot(getCellSlotName(cell))"
                    :name="getCellSlotName(cell)"
                    v-bind="getCellSlotProps(cell, row)"
                  />
                  <template v-else-if="isSelectionColumn(cell.column) && selectable">
                    <Checkbox
                      v-if="multiple"
                      :model-value="row.getIsSelected()"
                      :disabled="!row.getCanSelect()"
                      class="translate-y-[2px]"
                      @click.stop
                      @update:model-value="value => handleToggleRow(row, value)"
                    />
                    <RadioGroupItem
                      v-else
                      :value="row.id"
                      :disabled="!row.getCanSelect()"
                      class="translate-y-[2px]"
                      @click.stop
                    />
                  </template>
                  <FlexRender
                    v-else
                    :cell="cell"
                  />
                </template>
              </TableCell>
            </TableRow>
          </template>
          <TableEmpty v-else :colspan="visibleColumnCount">
            <slot name="empty" :table="table">
              <span class="text-muted-foreground">{{ emptyText }}</span>
            </slot>
          </TableEmpty>
        </TableBody>
      </Table>
    </component>
  </div>
</template>
