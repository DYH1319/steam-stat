<route lang="yaml">
meta:
  title: Steam 测试（新）
  icon: i-mdi:flask-outline
</route>

<script setup lang="ts">
import { ref } from 'vue'
import { toast } from 'vue-sonner'

const electronApi = (window as any).electron
const testResults = ref<Record<string, any>>({
  loginUser: null,
  steamStatus: null,
  runningApps: null,
  installedApps: null,
  libraryFolders: null,
})
const loading = ref<Record<string, boolean>>({
  loginUser: false,
  steamStatus: false,
  runningApps: false,
  installedApps: false,
  libraryFolders: false,
})
// 1. 登录用户
async function doLoginUser() {
  loading.value.loginUser = true
  try {
    testResults.value.loginUser = await electronApi.steamTestGetLoginUser()
    toast.success('获取登录用户信息成功')
  }
  catch (e: any) {
    toast.error(`获取失败: ${e?.message || e}`)
  }
  finally {
    loading.value.loginUser = false
  }
}
function clearLoginUser() {
  testResults.value.loginUser = null
}
// 2. Steam状态
async function doSteamStatus() {
  loading.value.steamStatus = true
  try {
    testResults.value.steamStatus = await electronApi.steamTestGetStatus()
    toast.success('获取Steam状态成功')
  }
  catch (e: any) {
    toast.error(`获取失败: ${e?.message || e}`)
  }
  finally {
    loading.value.steamStatus = false
  }
}
function clearSteamStatus() {
  testResults.value.steamStatus = null
}
// 3. 运行中的应用
async function doRunningApps() {
  loading.value.runningApps = true
  try {
    testResults.value.runningApps = await electronApi.steamTestGetRunningApps()
    toast.success('获取运行中应用成功')
  }
  catch (e: any) {
    toast.error(`获取失败: ${e?.message || e}`)
  }
  finally {
    loading.value.runningApps = false
  }
}
function clearRunningApps() {
  testResults.value.runningApps = null
}
// 4. 已安装的应用
async function doInstalledApps() {
  loading.value.installedApps = true
  try {
    testResults.value.installedApps = await electronApi.steamTestGetInstalledApps()
    toast.success('获取已安装应用成功')
  }
  catch (e: any) {
    toast.error(`获取失败: ${e?.message || e}`)
  }
  finally {
    loading.value.installedApps = false
  }
}
function clearInstalledApps() {
  testResults.value.installedApps = null
}
// 5. 游戏库目录
async function doLibraryFolders() {
  loading.value.libraryFolders = true
  try {
    testResults.value.libraryFolders = await electronApi.steamTestGetLibraryFolders()
    toast.success('获取游戏库目录成功')
  }
  catch (e: any) {
    toast.error(`获取失败: ${e?.message || e}`)
  }
  finally {
    loading.value.libraryFolders = false
  }
}
function clearLibraryFolders() {
  testResults.value.libraryFolders = null
}
</script>

<template>
  <div>
    <FaPageHeader title="Steam 测试（新）" />
    <FaPageMain>
      <div class="space-y-6">
        <!-- 1. 登录用户信息 -->
        <div class="rounded-lg bg-[var(--g-container-bg)] p-6">
          <div class="mb-4 flex items-center justify-between">
            <h3 class="text-lg font-semibold">
              1. 获取Steam本机登录用户信息
            </h3>
            <div class="flex gap-2">
              <el-button type="primary" :loading="loading.loginUser" @click="doLoginUser">
                执行测试
              </el-button>
              <el-button :disabled="!testResults.loginUser" @click="clearLoginUser">
                清除结果
              </el-button>
            </div>
          </div>
          <div v-if="testResults.loginUser" class="rounded bg-gray-100 p-4 dark:bg-gray-800">
            <!-- 主字段部分 -->
            <el-descriptions bordered :column="2" size="small" class="mb-4">
              <el-descriptions-item label="RunningAppID">
                {{ testResults.loginUser.RunningAppID }}
              </el-descriptions-item>
              <el-descriptions-item label="AutoLoginUser">
                {{ testResults.loginUser.AutoLoginUser }}
              </el-descriptions-item>
              <el-descriptions-item label="LastGameNameUsed">
                {{ testResults.loginUser.LastGameNameUsed }}
              </el-descriptions-item>
              <el-descriptions-item label="RememberPassword">
                <el-tag :type="Number(testResults.loginUser.RememberPassword) === 1 ? 'success' : 'info'">
                  {{ Number(testResults.loginUser.RememberPassword) === 1 ? '已记住' : '未记住' }}
                </el-tag>
              </el-descriptions-item>
              <el-descriptions-item label="ActiveUser">
                {{ testResults.loginUser.ActiveUser }}
              </el-descriptions-item>
              <el-descriptions-item label="ActiveUserSteamID">
                {{ testResults.loginUser.ActiveUserSteamID?.toString?.() || '' }}
              </el-descriptions-item>
              <el-descriptions-item label="ActiveUserSteam2ID">
                {{ testResults.loginUser.ActiveUserSteam2ID }}
              </el-descriptions-item>
              <el-descriptions-item label="ActiveUserSteam3ID">
                {{ testResults.loginUser.ActiveUserSteam3ID }}
              </el-descriptions-item>
              <el-descriptions-item label="ActiveUserSteam64ID">
                {{ testResults.loginUser.ActiveUserSteam64ID }}
              </el-descriptions-item>
            </el-descriptions>
            <!-- loginusers 部分 -->
            <template v-if="testResults.loginUser.loginusers">
              <el-tag size="large" type="success" class="mb-4">
                共 {{ Object.keys(testResults.loginUser.loginusers).length }} 个本机登录过的用户
              </el-tag>
              <el-table :data="Object.values(testResults.loginUser.loginusers)" stripe>
                <el-table-column prop="AccountName" label="账号" min-width="90" />
                <el-table-column prop="PersonaName" label="昵称" min-width="90" />
                <el-table-column prop="Steam64ID" label="Steam64ID" min-width="160" />
                <el-table-column prop="Steam2ID" label="Steam2ID" min-width="120" />
                <el-table-column prop="Steam3ID" label="Steam3ID" min-width="120" />
                <el-table-column prop="RememberPassword" label="记住密码" width="90">
                  <template #default="{ row }">
                    <el-tag v-if="row.RememberPassword === 1" size="small" type="success">
                      是
                    </el-tag>
                    <el-tag v-else size="small" type="info">
                      否
                    </el-tag>
                  </template>
                </el-table-column>
                <el-table-column prop="MostRecent" label="最近" width="80">
                  <template #default="{ row }">
                    <el-tag v-if="row.MostRecent === 1" size="small" type="success">
                      当前
                    </el-tag>
                  </template>
                </el-table-column>
                <el-table-column prop="Timestamp" label="最后登录">
                  <template #default="{ row }">
                    <span v-if="row.Timestamp">{{ new Date(row.Timestamp * 1000).toLocaleString() }}</span>
                  </template>
                </el-table-column>
              </el-table>
            </template>
            <template v-else>
              <el-empty description="无账号数据" />
            </template>
          </div>
        </div>
        <!-- Steam状态 -->
        <div class="rounded-lg bg-[var(--g-container-bg)] p-6">
          <div class="mb-4 flex items-center justify-between">
            <h3 class="text-lg font-semibold">
              2. 获取Steam状态
            </h3>
            <div class="flex gap-2">
              <el-button type="primary" :loading="loading.steamStatus" @click="doSteamStatus">
                执行测试
              </el-button>
              <el-button :disabled="!testResults.steamStatus" @click="clearSteamStatus">
                清除结果
              </el-button>
            </div>
          </div>
          <div v-if="testResults.steamStatus" class="rounded bg-gray-100 p-4 dark:bg-gray-800">
            <div class="mb-4 flex flex-wrap gap-2">
              <el-tag :type="testResults.steamStatus.SteamPath ? 'success' : 'info'">
                安装路径: {{ testResults.steamStatus.SteamPath || '无' }}
              </el-tag>
              <el-tag :type="testResults.steamStatus.SteamExe ? 'success' : 'info'">
                可执行文件: {{ testResults.steamStatus.SteamExe || '无' }}
              </el-tag>
              <el-tag :type="Number(testResults.steamStatus.pid) > 0 ? 'success' : 'info'">
                pid: {{ testResults.steamStatus.pid }}
              </el-tag>
              <el-tag :type="testResults.steamStatus.SteamClientDll ? 'success' : 'info'">
                SteamClientDll: {{ testResults.steamStatus.SteamClientDll }}
              </el-tag>
              <el-tag :type="testResults.steamStatus.SteamClientDll64 ? 'success' : 'info'">
                SteamClientDll64: {{ testResults.steamStatus.SteamClientDll64 }}
              </el-tag>
            </div>
            <el-descriptions bordered :column="2" size="small">
              <el-descriptions-item label="安装路径">
                {{ testResults.steamStatus.SteamPath }}
              </el-descriptions-item>
              <el-descriptions-item label="可执行文件">
                {{ testResults.steamStatus.SteamExe }}
              </el-descriptions-item>
              <el-descriptions-item label="pid">
                {{ testResults.steamStatus.pid }}
              </el-descriptions-item>
              <el-descriptions-item label="SteamClientDll">
                {{ testResults.steamStatus.SteamClientDll }}
              </el-descriptions-item>
              <el-descriptions-item label="SteamClientDll64">
                {{ testResults.steamStatus.SteamClientDll64 }}
              </el-descriptions-item>
            </el-descriptions>
          </div>
        </div>
        <!-- 运行中应用 -->
        <div class="rounded-lg bg-[var(--g-container-bg)] p-6">
          <div class="mb-4 flex items-center justify-between">
            <h3 class="text-lg font-semibold">
              3. 获取运行中的Steam应用
            </h3>
            <div class="flex gap-2">
              <el-button type="primary" :loading="loading.runningApps" @click="doRunningApps">
                执行测试
              </el-button>
              <el-button :disabled="!testResults.runningApps" @click="clearRunningApps">
                清除结果
              </el-button>
            </div>
          </div>
          <div v-if="testResults.runningApps" class="rounded bg-gray-100 p-4 dark:bg-gray-800">
            <el-tag size="large" class="mb-4" :type="testResults.runningApps.length > 0 ? 'success' : 'info'">
              {{ testResults.runningApps.length > 0 ? `当前有 ${testResults.runningApps.length} 个应用运行中` : '无' }}
            </el-tag>
            <el-table v-if="testResults.runningApps.length > 0" :data="testResults.runningApps" stripe>
              <el-table-column prop="name" label="应用名称" min-width="200" />
              <el-table-column prop="appId" label="AppID" width="100" />
            </el-table>
            <div v-else class="text-sm text-muted-foreground font-normal">
              当前没有运行中的应用
            </div>
          </div>
        </div>
        <!-- 已安装应用 -->
        <div class="rounded-lg bg-[var(--g-container-bg)] p-6">
          <div class="mb-4 flex items-center justify-between">
            <h3 class="text-lg font-semibold">
              4. 获取已安装的Steam应用
            </h3>
            <div class="flex gap-2">
              <el-button type="primary" :loading="loading.installedApps" @click="doInstalledApps">
                执行测试
              </el-button>
              <el-button :disabled="!testResults.installedApps" @click="clearInstalledApps">
                清除结果
              </el-button>
            </div>
          </div>
          <div v-if="testResults.installedApps" class="rounded bg-gray-100 p-4 dark:bg-gray-800">
            <el-tag size="large" class="mb-4" type="success">
              共 {{ testResults.installedApps.length }} 个已安装应用
            </el-tag>
            <el-table :data="testResults.installedApps" stripe max-height="400">
              <el-table-column prop="appId" label="AppID" width="100" sortable />
              <el-table-column prop="name" label="应用名称" min-width="160" sortable />
              <el-table-column prop="installDir" label="安装目录" min-width="120" sortable />
              <el-table-column prop="gameFolder" label="应用路径" min-width="150" sortable />
              <el-table-column prop="libraryFolder" label="库文件夹" min-width="120" sortable />
              <el-table-column prop="exePaths" label="exe文件" min-width="120" sortable="custom">
                <template #default="{ row }">
                  <el-tag size="small">
                    {{ row.exePaths?.length || 0 }}
                  </el-tag>
                  <template v-if="row.exePaths && row.exePaths.length">
                    <el-popover placement="bottom" trigger="click">
                      <template #reference>
                        <el-link type="primary" :underline="false" class="ml-1">
                          明细
                        </el-link>
                      </template>
                      <div style="max-width: 300px; white-space: pre-wrap;">
                        <div v-for="exe in row.exePaths" :key="exe">
                          {{ exe }}
                        </div>
                      </div>
                    </el-popover>
                  </template>
                </template>
              </el-table-column>
              <el-table-column prop="sizeOnDisk" label="占用空间(B)" min-width="120" sortable />
              <el-table-column prop="sizeOnDiskHuman" label="占用空间(可读)" min-width="120" sortable="custom">
                <template #default="{ row }">
                  {{ row.sizeOnDiskHuman ?? '-' }}
                </template>
              </el-table-column>
            </el-table>
          </div>
        </div>
        <!-- 游戏库目录 -->
        <div class="rounded-lg bg-[var(--g-container-bg)] p-6">
          <div class="mb-4 flex items-center justify-between">
            <h3 class="text-lg font-semibold">
              5. 获取Steam库目录
            </h3>
            <div class="flex gap-2">
              <el-button type="primary" :loading="loading.libraryFolders" @click="doLibraryFolders">
                执行测试
              </el-button>
              <el-button :disabled="!testResults.libraryFolders" @click="clearLibraryFolders">
                清除结果
              </el-button>
            </div>
          </div>
          <div v-if="testResults.libraryFolders && testResults.libraryFolders.length" class="flex flex-wrap gap-2 rounded bg-gray-100 p-4 dark:bg-gray-800">
            <el-tag v-for="(folder, idx) in testResults.libraryFolders" :key="folder" type="info">
              库{{ idx + 1 }}: {{ folder }}
            </el-tag>
          </div>
          <div v-else-if="testResults.libraryFolders" class="text-sm text-muted-foreground font-normal">
            暂无数据
          </div>
        </div>
      </div>
    </FaPageMain>
  </div>
</template>

<style scoped lang="scss">
pre {
  padding: 12px;
  overflow-x: auto;
  background: var(--el-bg-color);
  border-radius: 4px;
}
</style>
