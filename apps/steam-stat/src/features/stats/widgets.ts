/** 统计小组件标识（仪表盘与各标签页共用） */
export type WidgetId
  = | 'summary'
    | 'durationShare'
    | 'timeTrend'
    | 'launchFrequency'
    | 'yearHeatmap'
    | 'hourWeekHeatmap'
    | 'sessionGantt'
    | 'lifecycle'
    | 'recentSessions'
    | 'personalRecords'
    | 'periodComparison'
    | 'metaBreakdown'
    | 'gameRanking'
    | 'drillDetail'
    | 'onThisDay'

export interface WidgetMeta {
  id: WidgetId
  icon: string
  defaultSpan: 1 | 2
}

export const WIDGETS: WidgetMeta[] = [
  { id: 'summary', icon: 'i-mdi:view-dashboard-outline', defaultSpan: 2 },
  { id: 'durationShare', icon: 'i-mdi:chart-donut', defaultSpan: 1 },
  { id: 'timeTrend', icon: 'i-mdi:chart-bar', defaultSpan: 1 },
  { id: 'launchFrequency', icon: 'i-mdi:rocket-launch-outline', defaultSpan: 1 },
  { id: 'recentSessions', icon: 'i-mdi:chart-line', defaultSpan: 1 },
  { id: 'yearHeatmap', icon: 'i-mdi:calendar-month-outline', defaultSpan: 2 },
  { id: 'hourWeekHeatmap', icon: 'i-mdi:clock-outline', defaultSpan: 1 },
  { id: 'sessionGantt', icon: 'i-mdi:chart-timeline', defaultSpan: 2 },
  { id: 'lifecycle', icon: 'i-mdi:chart-bell-curve-cumulative', defaultSpan: 1 },
  { id: 'personalRecords', icon: 'i-mdi:trophy-outline', defaultSpan: 1 },
  { id: 'periodComparison', icon: 'i-mdi:compare-horizontal', defaultSpan: 1 },
  { id: 'metaBreakdown', icon: 'i-mdi:tag-multiple-outline', defaultSpan: 1 },
  { id: 'gameRanking', icon: 'i-mdi:format-list-numbered', defaultSpan: 2 },
  { id: 'drillDetail', icon: 'i-mdi:table-search', defaultSpan: 2 },
  { id: 'onThisDay', icon: 'i-mdi:calendar-today', defaultSpan: 1 },
]

export const DEFAULT_LAYOUT_WIDGETS: { id: WidgetId, span: 1 | 2 }[] = [
  { id: 'summary', span: 2 },
  { id: 'periodComparison', span: 1 },
  { id: 'durationShare', span: 1 },
  { id: 'timeTrend', span: 2 },
  { id: 'yearHeatmap', span: 2 },
  { id: 'hourWeekHeatmap', span: 1 },
  { id: 'personalRecords', span: 1 },
  { id: 'drillDetail', span: 2 },
]
