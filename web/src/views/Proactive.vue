<template>
  <div>
    <ProactiveOverview v-model:enabled="form.enabled" :config="savedConfig" :status="status"
      :dirty="dirty" :saving="saving" :refreshing="refreshing" :stale="statusStale"
      @change="markDirty" @save="save" @refresh="loadAll" @help="openHelp('enabled')" />

    <el-alert v-if="form.webSearchEnabled && status.aiWebSearchEnabled === false" type="warning"
      :closable="false" show-icon style="margin-top: 16px">
      <template #title>
        择机联网核实已打开，但「AI 设置」里没有开启联网搜索，改写阶段不会真的检索。
      </template>
    </el-alert>
    <el-alert v-else-if="form.webSearchEnabled && status.aiWebSearchConfigured === false" type="warning"
      :closable="false" show-icon style="margin-top: 16px">
      <template #title>
        联网搜索已开启，但还没有可用的搜索通道：请回「AI 设置」完成探测并选用，否则改写会退化成普通请求。
      </template>
    </el-alert>

    <ProactiveParameters ref="parametersPanel" v-model:global-config="form" :saved-global="savedConfig"
      :saving-global="saving" :refreshing="refreshing" :dirty-global="dirty"
      @change-global="markDirty" @save-global="save" @reset-global="resetConfig"
      @dirty-groups="groupDirty = $event" @groups-changed="loadStatus().catch(() => {})" />

    <el-card shadow="never" style="margin-top: 16px">
      <template #header>
        <div class="card-head">
          <b>决策留痕</b>
          <div>
            <el-select v-model="filterOutcome" size="small" clearable placeholder="全部结论" style="width: 150px"
              @change="reloadLogs">
              <el-option v-for="o in outcomeOptions" :key="o.value" :label="o.label" :value="o.value" />
            </el-select>
            <el-button size="small" style="margin-left: 8px" @click="reloadLogs">刷新</el-button>
          </div>
        </div>
      </template>
      <el-table :data="logs" size="small" v-loading="logsLoading">
        <el-table-column label="时间" width="160">
          <template #default="{ row }">{{ fmt(row.evaluatedAt) }}</template>
        </el-table-column>
        <el-table-column label="群" width="120" show-overflow-tooltip>
          <template #default="{ row }">{{ row.groupName || row.groupId }}</template>
        </el-table-column>
        <el-table-column prop="messageCount" label="条数" width="60" />
        <el-table-column label="附图" width="60">
          <template #default="{ row }">{{ row.imageCount ? row.imageCount + ' 张' : '-' }}</template>
        </el-table-column>
        <el-table-column label="记忆" width="90">
          <template #default="{ row }">
            <el-tooltip v-if="row.memoryCount" :content="`本次判定注入了 ${row.memoryCount} 条群记忆`" placement="top">
              <span>{{ row.memoryPromptChars }} 字</span>
            </el-tooltip>
            <span v-else>-</span>
          </template>
        </el-table-column>
        <el-table-column label="联网" width="100">
          <template #default="{ row }">
            <el-tooltip v-if="row.searchQuery" :content="`检索词：${row.searchQuery}`" placement="top">
              <el-tag size="small" :type="row.searchUsed ? 'primary' : 'info'">
                {{ row.searchUsed ? '已改写' : '未启用' }}
              </el-tag>
            </el-tooltip>
            <span v-else>-</span>
          </template>
        </el-table-column>
        <el-table-column label="结论" width="120">
          <template #default="{ row }">
            <el-tag size="small" :type="outcomeType(row.outcome)">{{ outcomeLabel(row.outcome) }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column label="说了什么 / 为什么没说" min-width="240" show-overflow-tooltip>
          <template #default="{ row }">{{ row.line || row.reason || row.detail || row.topic || '-' }}</template>
        </el-table-column>
        <el-table-column prop="model" label="模型" width="130" show-overflow-tooltip />
        <el-table-column label="tokens" width="90">
          <template #default="{ row }">
            <el-tooltip :content="`判定 ${row.promptTokens + row.completionTokens} / 改写 ${(row.speechPromptTokens || 0) + (row.speechCompletionTokens || 0)}`"
              placement="top">
              <span>{{ row.promptTokens + row.completionTokens + (row.speechPromptTokens || 0) + (row.speechCompletionTokens || 0) }}</span>
            </el-tooltip>
          </template>
        </el-table-column>
      </el-table>
      <el-pagination layout="prev, pager, next, total" :total="logTotal" :page-size="pageSize"
        :current-page="page" style="margin-top: 10px" @current-change="onPage" />
    </el-card>

    <ParamHelpDrawer v-model="helpVisible" :title="helpTitle" :lines="helpLines" :current="helpCurrent"
      :index="helpIndex" :total="helpTotal" @step="stepHelp" />
  </div>
</template>

<script setup>
import { ref, computed, onMounted, onUnmounted } from 'vue'
import { ElMessage } from 'element-plus'
import { subscribeRealtime } from '../realtime'
import { LatestRequest } from '../latestRequest'
import { useUnsavedChanges } from '../useUnsavedChanges'
import api from '../api'
import { useParamHelp } from '../paramHelp'
import ParamHelpDrawer from '../components/ParamHelpDrawer.vue'
import ProactiveOverview from '../components/ProactiveOverview.vue'
import ProactiveParameters from '../components/ProactiveParameters.vue'
import { defaultProactiveConfig, invalidParameterField } from '../proactiveParameters'

const outcomeMap = {
  sent_passive: ['已插话（被动）', 'success'],
  sent_active: ['已插话（主动）', 'success'],
  skipped_quiet: ['静默时段', 'info'],
  skipped_cooldown: ['间隔未到', 'info'],
  skipped_group_cap: ['单群到量', 'warning'],
  skipped_global_cap: ['全局到量', 'warning'],
  skipped_ai_declined: ['AI 没话可说', 'info'],
  skipped_after_search: ['联网后放弃', 'info'],
  skipped_ai_unavailable: ['AI 不可用', 'warning'],
  failed_ai_call: ['AI 调用失败', 'danger'],
  skipped_ai_truncated: ['AI 输出被截断', 'warning'],
  skipped_ai_unparsable: ['AI 输出无法解析', 'warning'],
  skipped_ai_busy: ['评估忙', 'info'],
  skipped_disabled: ['已关闭', 'info'],
  failed_rejected: ['被接收方拒收', 'danger'],
  failed_rate_limited: ['触顶频控', 'danger'],
  failed_no_channel: ['无可用的发送通道', 'danger'],
  delivery_unknown: ['平台结果未确认', 'warning'],
  failed_api_error: ['发送失败', 'danger']
}

const outcomeOptions = Object.entries(outcomeMap).map(([value, v]) => ({ value, label: v[0] }))

const form = ref(emptyForm())
const savedConfig = ref(null)
const status = ref({})
const statusStale = ref(false)
const refreshing = ref(false)
const logs = ref([])
const logTotal = ref(0)
const page = ref(1)
const pageSize = 30
const filterOutcome = ref('')
const saving = ref(false)
const logsLoading = ref(false)
const dirty = ref(false), groupDirty = ref(false), parametersPanel = ref(null)
const anyDirty = computed(() => dirty.value || groupDirty.value)

const HELP_ORDER = ['enabled']
const HELP = {
  enabled: {
    title: '启用择机发言', value: f => f.enabled ? '已启用' : '已关闭',
    lines: ['全局总开关。关闭后不再采集消息、调用模型或择机发言。', '各群还需开启参与；群级参数可在下方选择群聊后设置。']
  }
}

const {
  visible: helpVisible, index: helpIndex, total: helpTotal, title: helpTitle,
  lines: helpLines, current: helpCurrent, label: helpLabel, open: openHelp, step: stepHelp
} = useParamHelp(HELP, HELP_ORDER, () => form.value)

function emptyForm() { return defaultProactiveConfig() }

const fmt = (s) => (s ? new Date(s + (s.endsWith('Z') ? '' : 'Z')).toLocaleString('zh-CN', { hour12: false }) : '-')
const outcomeLabel = (o) => outcomeMap[o]?.[0] || o
const outcomeType = (o) => outcomeMap[o]?.[1] || 'info'
const markDirty = () => { dirty.value = true }
useUnsavedChanges(anyDirty)
const logRequest = new LatestRequest()
const statusRequest = new LatestRequest()

const loadConfig = async () => {
  if (dirty.value) return
  const { data } = await api.get('/proactive/config')
  // 刷新期间也可能开始编辑，返回的配置不能覆盖本地草稿。
  savedConfig.value = { ...emptyForm(), ...data }
  if (dirty.value) return
  form.value = { ...emptyForm(), ...data }
  dirty.value = false
}

const loadStatus = async () => {
  try {
    await statusRequest.run((signal) => api.get('/proactive/status', { signal }), ({ data }) => {
      status.value = data
      statusStale.value = false
    })
  } catch (error) {
    if (!disposed) statusStale.value = true
    throw error
  }
}

const loadLogs = async () => {
  logsLoading.value = true
  try {
    const params = { page: page.value, pageSize }
    if (filterOutcome.value) params.outcome = filterOutcome.value
    await logRequest.run((signal) => api.get('/proactive/logs', { params, signal }), ({ data }) => {
      logs.value = data.items
      logTotal.value = data.totalCount
    })
  } finally {
    if (!logRequest.pending) logsLoading.value = false
  }
}

const reloadLogs = () => { page.value = 1; loadLogs() }
const onPage = (p) => { page.value = p; loadLogs() }

const resetConfig = () => { if (savedConfig.value) { form.value = { ...savedConfig.value }; dirty.value = false } }

let saveController = null
const save = async () => {
  if (saving.value || refreshing.value || !savedConfig.value) return
  const invalid = invalidParameterField(form.value, true)
  if (invalid) { ElMessage.warning(`请填写有效的${invalid}后再保存`); return }
  saving.value = true
  saveController = new AbortController()
  try {
    const { data } = await api.put('/proactive/config', form.value, { signal: saveController.signal })
    if (disposed) return // 已离开页面：不写状态、不弹提示
    ElMessage.success(data.message)
    form.value = { ...emptyForm(), ...data.config }
    savedConfig.value = { ...form.value }
    dirty.value = false
    await loadStatus()
  } catch { /* API 层提示失败；未成功保存时保留草稿。 */ }
  finally {
    saving.value = false
    saveController = null
  }
}

const loadAll = async () => {
  if (refreshing.value) return
  refreshing.value = true
  try {
    // 等所有刷新请求结束再开放保存；某个请求失败也不提前解除保护。
    await Promise.allSettled([loadConfig(), loadStatus(), loadLogs(), parametersPanel.value?.refresh()])
  }
  finally { refreshing.value = false }
}

// 后端每写一行决策留痕（说与不说都算）就推一次 ProactiveEvaluated，
// 这里节流刷新：不翻页时留痕与今日统计自动跟上，翻页查看不被打断
let unsubscribe = null
let disposed = false
let refreshTimer = null
const refreshSoon = () => {
  if (disposed || refreshTimer) return
  refreshTimer = setTimeout(async () => {
    refreshTimer = null
    try {
      await loadStatus()
      if (!disposed && page.value === 1) await loadLogs()
    } catch { /* 统一错误提示 */ }
  }, 1500)
}

const connectHub = () => {
  if (disposed) return
  unsubscribe = subscribeRealtime({ ProactiveEvaluated: refreshSoon, Resync: refreshSoon })
}
const disconnectHub = () => {
  disposed = true
  if (refreshTimer) { clearTimeout(refreshTimer); refreshTimer = null }
  saveController?.abort() // 卸载时取消在途的全局保存，返回后也不再写状态
  unsubscribe?.()
  logRequest.cancel()
  statusRequest.cancel()
}

onMounted(async () => {
  connectHub()
  await loadAll().catch(() => {})
})

onUnmounted(disconnectHub)
</script>

<style scoped>
.card-head { display: flex; justify-content: space-between; align-items: center; }
.hint { color: var(--el-text-color-secondary); font-size: 12px; margin-left: 10px; }
</style>
