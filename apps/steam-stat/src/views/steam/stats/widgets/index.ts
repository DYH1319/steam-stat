import type { Component } from 'vue'
import type { WidgetId } from '@/features/stats/widgets'
import DrillDetail from './DrillDetail.vue'
import DurationShare from './DurationShare.vue'
import GameRanking from './GameRanking.vue'
import HourWeekHeatmap from './HourWeekHeatmap.vue'
import LaunchFrequency from './LaunchFrequency.vue'
import Lifecycle from './Lifecycle.vue'
import MetaBreakdown from './MetaBreakdown.vue'
import OnThisDay from './OnThisDay.vue'
import PeriodComparison from './PeriodComparison.vue'
import PersonalRecords from './PersonalRecords.vue'
import RecentSessions from './RecentSessions.vue'
import SessionGantt from './SessionGantt.vue'
import SummaryCards from './SummaryCards.vue'
import TimeTrend from './TimeTrend.vue'
import YearHeatmap from './YearHeatmap.vue'

export const widgetComponents: Record<WidgetId, Component> = {
  summary: SummaryCards,
  durationShare: DurationShare,
  timeTrend: TimeTrend,
  launchFrequency: LaunchFrequency,
  yearHeatmap: YearHeatmap,
  hourWeekHeatmap: HourWeekHeatmap,
  sessionGantt: SessionGantt,
  lifecycle: Lifecycle,
  recentSessions: RecentSessions,
  personalRecords: PersonalRecords,
  periodComparison: PeriodComparison,
  metaBreakdown: MetaBreakdown,
  gameRanking: GameRanking,
  drillDetail: DrillDetail,
  onThisDay: OnThisDay,
}
