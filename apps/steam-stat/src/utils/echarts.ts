import { BarChart, CustomChart, HeatmapChart, LineChart, PieChart, TreemapChart } from 'echarts/charts'
import {
  CalendarComponent,
  DatasetComponent,
  DataZoomComponent,
  GridComponent,
  LegendComponent,
  MarkLineComponent,
  TitleComponent,
  TooltipComponent,
  VisualMapComponent,
} from 'echarts/components'
import * as echarts from 'echarts/core'
import { CanvasRenderer } from 'echarts/renderers'

echarts.use([
  BarChart,
  LineChart,
  PieChart,
  HeatmapChart,
  TreemapChart,
  CustomChart,
  GridComponent,
  LegendComponent,
  TooltipComponent,
  TitleComponent,
  VisualMapComponent,
  CalendarComponent,
  DataZoomComponent,
  MarkLineComponent,
  DatasetComponent,
  CanvasRenderer,
])

export default echarts
