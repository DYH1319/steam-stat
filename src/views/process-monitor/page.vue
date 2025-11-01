<route lang="yaml">
meta:
  title: 进程监控
  icon: i-material-symbols:monitor-heart-outline
</route>

<script setup lang="ts">
import type { Window } from '@/types/global'
import { toast } from 'vue-sonner'

// 定义进程信息接口
interface ProcessInfo {
  name: string
  pid: number
  sessionName: string
  sessionNumber: string
  memUsage: string
}

interface ProcessInfoWithStatus extends ProcessInfo {
  isNew?: boolean
}

const loading = ref(false)
const processList = ref<ProcessInfoWithStatus[]>([])
const previousPids = ref<Set<number>>(new Set())
const autoRefresh = ref(false)
const refreshInterval = ref(5)
let timer: NodeJS.Timeout | null = null

const tableColumns = [
  { prop: 'name', label: '进程名称', minWidth: 200 },
  { prop: 'pid', label: '进程ID', width: 120 },
  { prop: 'sessionName', label: '会话名', width: 150 },
  { prop: 'sessionNumber', label: '会话号', width: 120 },
  { prop: 'memUsage', label: '内存使用', width: 150 },
]

async function fetchProcessList() {
  console.warn('[前端] 开始获取进程列表')
  const electronApi = (window as unknown as Window).electron
  console.warn('[前端] electronApi:', electronApi)
  console.warn('[前端] electronApi.getProcessList:', electronApi?.getProcessList)

  if (!electronApi?.getProcessList) {
    console.error('[前端] Electron API 不可用')
    toast.error('Electron API 不可用')
    return
  }

  loading.value = true
  try {
    console.warn('[前端] 调用 electronApi.getProcessList()')
    const processes = await electronApi.getProcessList() as ProcessInfo[]
    console.warn('[前端] 收到进程列表，数量:', processes.length)
    console.warn('[前端] 进程列表:', processes)

    // 标记新增进程
    const currentPids = new Set(processes.map((p: ProcessInfo) => p.pid))
    const newPids = [...currentPids].filter((pid: number) => !previousPids.value.has(pid))

    processList.value = processes.map((p: ProcessInfo) => ({
      ...p,
      isNew: newPids.includes(p.pid),
    }))

    console.warn('[前端] 处理后的进程列表数量:', processList.value.length)

    // 更新之前的 PID 集合
    previousPids.value = new Set(currentPids) as Set<number>

    if (newPids.length > 0 && previousPids.value.size > 0) {
      toast.success(`检测到 ${newPids.length} 个新进程`)
    }
  }
  catch (error) {
    console.error('[前端] 获取进程列表失败:', error)
    toast.error('获取进程列表失败')
  }
  finally {
    loading.value = false
  }
}

function startAutoRefresh() {
  if (timer) {
    clearInterval(timer)
  }
  if (autoRefresh.value) {
    timer = setInterval(() => {
      fetchProcessList()
    }, refreshInterval.value * 1000)
    toast.success(`已启动自动刷新，间隔 ${refreshInterval.value} 秒`)
  }
  else {
    toast.info('已停止自动刷新')
  }
}

function handleRefreshIntervalChange() {
  if (autoRefresh.value) {
    startAutoRefresh()
  }
}

// 行的类名
function tableRowClassName({ row }: { row: ProcessInfoWithStatus }) {
  return row.isNew ? 'new-process-row' : ''
}

watch(autoRefresh, startAutoRefresh)

onMounted(() => {
  fetchProcessList()
})

onUnmounted(() => {
  if (timer) {
    clearInterval(timer)
  }
})
</script>

<template>
  <div>
    <FaPageHeader title="进程监控" />
    <FaPageMain>
      <div>
        <!-- 工具栏 -->
        <div class="mb-5 flex items-center justify-between rounded-lg bg-[var(--g-container-bg)] p-4">
          <div class="flex items-center gap-4">
            <el-button
              type="primary"
              :loading="loading"
              icon="i-ep:refresh"
              @click="fetchProcessList"
            >
              刷新
            </el-button>
            <el-switch
              v-model="autoRefresh"
              active-text="自动刷新"
              inactive-text="手动刷新"
            />
            <div v-if="autoRefresh" class="flex items-center gap-2">
              <span>刷新间隔：</span>
              <el-input-number
                v-model="refreshInterval"
                :min="1"
                :max="60"
                :step="1"
                @change="handleRefreshIntervalChange"
              />
              <span>秒</span>
            </div>
          </div>
          <div class="flex items-center gap-3">
            <el-tag type="info">
              总进程数: {{ processList.length }}
            </el-tag>
            <el-tag type="success">
              新增进程: {{ processList.filter(p => p.isNew).length }}
            </el-tag>
          </div>
        </div>

        <!-- 进程表格 -->
        <el-table
          :data="processList"
          :row-class-name="tableRowClassName"
          stripe
          border
          height="calc(100vh - 280px)"
          style="width: 100%;"
        >
          <el-table-column
            v-for="column in tableColumns"
            :key="column.prop"
            :prop="column.prop"
            :label="column.label"
            :width="column.width"
            :min-width="column.minWidth"
          >
            <template #default="{ row }">
              <div class="flex items-center">
                {{ row[column.prop] }}
                <el-tag
                  v-if="column.prop === 'name' && row.isNew"
                  type="warning"
                  size="small"
                  class="ml-2"
                >
                  新增
                </el-tag>
              </div>
            </template>
          </el-table-column>
        </el-table>
      </div>
    </FaPageMain>
  </div>
</template>

<style scoped>
:deep(.new-process-row) {
  background-color: #fff3e0 !important;
}

:deep(.new-process-row:hover) {
  background-color: #ffe0b2 !important;
}

:deep(.el-table--dark .new-process-row) {
  background-color: #3e2723 !important;
}

:deep(.el-table--dark .new-process-row:hover) {
  background-color: #4e342e !important;
}
</style>
