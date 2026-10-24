import type { Component } from 'vue'
import { defineAsyncComponent } from 'vue'

export interface WidgetDef {
  id: string
  titleKey: string
  icon: string
  component: Component
  defaultSpan: 1 | 2
}

const load = (path: string) => defineAsyncComponent(() => import(`./${path}.vue`))

export const widgetRegistry: WidgetDef[] = [
  { id: 'summary', titleKey: 'stats.widgetSummary', icon: 'i-mdi:chart-box-outline', component: load('summary'), defaultSpan: 2 },
  { id: 'durationShare', titleKey: 'stats.widgetDurationShare', icon: 'i-mdi:chart-pie', component: load('durationShare'), defaultSpan: 1 },
  { id: 'timeTrend', titleKey: 'stats.widgetTimeTrend', icon: 'i-mdi:chart-bar', component: load('timeTrend'), defaultSpan: 2 },
  { id: 'launchFrequency', titleKey: 'stats.widgetLaunchFrequency', icon: 'i-mdi:rocket-launch-outline', component: load('launchFrequency'), defaultSpan: 1 },
  { id: 'recentSessions', titleKey: 'stats.widgetRecentSessions', icon: 'i-mdi:chart-line', component: load('recentSessions'), defaultSpan: 1 },
  { id: 'yearHeatmap', titleKey: 'stats.widgetYearHeatmap', icon: 'i-mdi:calendar-heat-map', component: load('yearHeatmap'), defaultSpan: 2 },
  { id: 'hourWeekHeatmap', titleKey: 'stats.widgetHourWeekHeatmap', icon: 'i-mdi:matrix', component: load('hourWeekHeatmap'), defaultSpan: 1 },
  { id: 'sessionGantt', titleKey: 'stats.widgetSessionGantt', icon: 'i-mdi:chart-gantt', component: load('sessionGantt'), defaultSpan: 2 },
  { id: 'lifecycle', titleKey: 'stats.widgetLifecycle', icon: 'i-mdi:chart-timeline-variant', component: load('lifecycle'), defaultSpan: 2 },
  { id: 'metaBreakdown', titleKey: 'stats.widgetMetaBreakdown', icon: 'i-mdi:tag-multiple-outline', component: load('metaBreakdown'), defaultSpan: 1 },
  { id: 'periodComparison', titleKey: 'stats.widgetPeriodComparison', icon: 'i-mdi:compare', component: load('periodComparison'), defaultSpan: 2 },
  { id: 'personalRecords', titleKey: 'stats.widgetPersonalRecords', icon: 'i-mdi:trophy-outline', component: load('personalRecords'), defaultSpan: 2 },
  { id: 'gameRanking', titleKey: 'stats.widgetGameRanking', icon: 'i-mdi:podium', component: load('gameRanking'), defaultSpan: 2 },
  { id: 'drillDetail', titleKey: 'stats.widgetDrillDetail', icon: 'i-mdi:table', component: load('drillDetail'), defaultSpan: 2 },
]

export const widgetById = new Map(widgetRegistry.map(w => [w.id, w]))
