<template>
  <div>
    <el-card shadow="never">
      <template #header>
        <div class="card-head">
          <b>择机主动发言</b>
          <div>
            <el-tag :type="status.enabled ? 'success' : 'info'" size="small" style="margin-right: 8px">
              {{ status.enabled ? '运行中' : '已关闭' }}
            </el-tag>
            <el-button size="small" @click="loadAll">刷新</el-button>
          </div>
        </div>
      </template>

      <el-alert type="info" :closable="false" show-icon style="margin-bottom: 14px">
        <template #title>
          机器人在没被 @ 时围观群聊，攒满一个窗口后由 AI 判断要不要自然接一句。
          总开关是唯一熔断点：关掉后不再采集消息、不再调用 AI、不再主动发言。
        </template>
      </el-alert>

      <div class="master">
        <el-switch v-model="form.enabled" active-text="启用择机发言" size="large" @change="markDirty" />
        <span class="hint">各群还需在「群管理」里单独勾选参与，双重开关</span>
        <el-button v-if="dirty" size="small" type="warning" plain @click="save">保存后生效</el-button>
      </div>

      <el-alert v-if="status.deliveryHint" :type="status.fullModeArrived > 0 ? 'success' : 'warning'"
        :closable="false" show-icon style="margin-bottom: 12px">
        <template #title>{{ status.deliveryHint }}</template>
      </el-alert>

      <el-descriptions :column="4" border size="small">
        <el-descriptions-item label="启动以来收到">
          全量 {{ status.fullModeArrived ?? 0 }} 条 / @ {{ status.atModeArrived ?? 0 }} 条
        </el-descriptions-item>
        <el-descriptions-item label="围观落库">{{ observeCount }} 条</el-descriptions-item>
        <el-descriptions-item label="Bot 插话">{{ status.todaySent ?? '-' }} 条（今日）</el-descriptions-item>
        <el-descriptions-item label="静默时段">{{ quietText }}</el-descriptions-item>
      </el-descriptions>

      <el-descriptions :column="4" border size="small" style="margin-top: 10px">
        <el-descriptions-item label="今日评估">{{ status.todayEvaluations ?? '-' }}</el-descriptions-item>
        <el-descriptions-item label="今日已发言">{{ status.todaySent ?? '-' }}</el-descriptions-item>
        <el-descriptions-item label="参与群数">{{ status.participatingGroups ?? '-' }}</el-descriptions-item>
        <el-descriptions-item label="待评估窗口">{{ pendingText }}</el-descriptions-item>
      </el-descriptions>

      <el-table v-if="status.quotas && status.quotas.length" :data="status.quotas" size="small" style="margin-top: 12px">
        <el-table-column label="群" min-width="140">
          <template #default="{ row }">{{ row.name || row.groupId }}</template>
        </el-table-column>
        <el-table-column prop="usedToday" label="今日主动" width="100" />
        <el-table-column prop="quota" label="每日上限" width="100" />
        <el-table-column prop="sentLastMinute" label="本分钟" width="90" />
        <el-table-column prop="minuteQuota" label="分钟上限" width="100" />
        <el-table-column label="接收状态" width="110">
          <template #default="{ row }">
            <el-tag size="small" :type="row.rejected ? 'danger' : 'success'">
              {{ row.rejected ? '已被关闭' : '正常' }}
            </el-tag>
          </template>
        </el-table-column>
      </el-table>
    </el-card>

    <el-card shadow="never" style="margin-top: 16px">
      <template #header><b>参数</b></template>
      <el-form :model="form" label-width="160px" style="max-width: 760px">
        <el-divider content-position="left">时机</el-divider>
        <el-form-item label="攒批窗口（秒）">
          <el-input-number v-model="form.windowSeconds" :min="15" :max="600" :step="5" @change="markDirty" />
          <span class="hint">同群连续闲聊攒够这么久才问一次 AI</span>
        </el-form-item>
        <el-form-item label="窗口最多条数">
          <el-input-number v-model="form.windowMaxMessages" :min="2" :max="50" @change="markDirty" />
        </el-form-item>
        <el-form-item label="发言倾向">
          <el-radio-group v-model="form.liveliness" @change="markDirty">
            <el-radio-button :value="0">克制</el-radio-button>
            <el-radio-button :value="1">正常</el-radio-button>
            <el-radio-button :value="2">主动</el-radio-button>
          </el-radio-group>
          <span class="hint">{{ tendencyHint }}</span>
          <div class="subline">
            它改的是 AI 的判断标准本身，不是数值阈值。嫌它闷就切「主动」：只要有能补一句的东西就说，
            别人答过了也可以补充他没提到的那点。
          </div>
        </el-form-item>
        <el-form-item label="说与不说">
          <span class="hint">由 AI 自己判断（should_speak），没有可调的置信度阈值</span>
          <div class="subline">窗口里的图片与表情包会随文字一起交给 AI 读，最多附带最近 3 张</div>
          <div class="subline">群友原话要不要写进聊天历史，在「群管理 → 围观范围」按群设置，这里不控制</div>
        </el-form-item>

        <el-divider content-position="left">节流</el-divider>
        <el-form-item label="同群间隔（分钟）">
          <el-input-number v-model="form.cooldownMinutes" :min="1" :max="1440" @change="markDirty" />
        </el-form-item>
        <el-form-item label="单群每日上限">
          <el-input-number v-model="form.maxPerGroupPerDay" :min="1" :max="200" @change="markDirty" />
        </el-form-item>
        <el-form-item label="全部群每日上限">
          <el-input-number v-model="form.maxGlobalPerDay" :min="1" :max="500" @change="markDirty" />
        </el-form-item>
        <el-form-item label="静默时段（北京时间）">
          <el-input-number v-model="form.quietStartHour" :min="0" :max="23" controls-position="right"
            style="width: 110px" @change="markDirty" />
          <span style="margin: 0 8px">至</span>
          <el-input-number v-model="form.quietEndHour" :min="0" :max="23" controls-position="right" style="width: 110px"
            @change="markDirty" />
          <span class="hint">两值相等表示不静默</span>
        </el-form-item>

        <el-divider content-position="left">通道</el-divider>
        <el-form-item label="允许真主动消息">
          <el-switch v-model="form.allowActiveChannel" @change="markDirty" />
          <span class="hint">被动槽位（收到消息后 5 分钟内）过期后，是否改发 msg_id 为空的主动消息</span>
        </el-form-item>
        <el-form-item label="主动：单群每分钟">
          <el-input-number v-model="form.activePerGroupPerMinute" :min="1" :max="ceilings.groupPerMinute"
            @change="markDirty" />
          <span class="hint">官方上限 {{ ceilings.groupPerMinute }} 条/分钟</span>
        </el-form-item>
        <el-form-item label="主动：单群每日">
          <el-input-number v-model="form.activePerGroupPerDay" :min="1" :max="ceilings.groupPerDay" @change="markDirty" />
          <span class="hint">官方上限 {{ ceilings.groupPerDay }} 条/日</span>
        </el-form-item>
        <el-form-item label="主动：账号每分钟">
          <el-input-number v-model="form.activePerAccountPerMinute" :min="1" :max="ceilings.accountPerMinute"
            @change="markDirty" />
          <span class="hint">官方上限 {{ ceilings.accountPerMinute }} 条/分钟（已认证）</span>
        </el-form-item>

        <el-form-item>
          <el-button type="primary" :loading="saving" @click="save">保存配置</el-button>
          <span v-if="form.updatedAt" class="hint">上次更新 {{ fmt(form.updatedAt) }}</span>
        </el-form-item>
      </el-form>
    </el-card>

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
          <template #default="{ row }">{{ row.promptTokens + row.completionTokens }}</template>
        </el-table-column>
      </el-table>
      <el-pagination layout="prev, pager, next, total" :total="logTotal" :page-size="pageSize"
        :current-page="page" style="margin-top: 10px" @current-change="onPage" />
    </el-card>
  </div>
</template>

<script setup>
import { ref, computed, onMounted } from 'vue'
import { ElMessage } from 'element-plus'
import api from '../api'

const outcomeMap = {
  sent_passive: ['已插话（被动）', 'success'],
  sent_active: ['已插话（主动）', 'success'],
  skipped_quiet: ['静默时段', 'info'],
  skipped_cooldown: ['间隔未到', 'info'],
  skipped_group_cap: ['单群到量', 'warning'],
  skipped_global_cap: ['全局到量', 'warning'],
  skipped_ai_declined: ['AI 没话可说', 'info'],
  skipped_ai_unavailable: ['AI 不可用', 'warning'],
  skipped_ai_busy: ['评估忙', 'info'],
  skipped_disabled: ['已关闭', 'info'],
  failed_rejected: ['被接收方拒收', 'danger'],
  failed_rate_limited: ['触顶频控', 'danger'],
  failed_no_channel: ['无可用的发送通道', 'danger'],
  failed_api_error: ['发送失败', 'danger']
}

const outcomeOptions = Object.entries(outcomeMap).map(([value, v]) => ({ value, label: v[0] }))

const form = ref(emptyForm())
const status = ref({})
const ceilings = ref({ groupPerMinute: 20, groupPerDay: 1000, accountPerMinute: 60 })
const logs = ref([])
const logTotal = ref(0)
const page = ref(1)
const pageSize = 30
const filterOutcome = ref('')
const saving = ref(false)
const logsLoading = ref(false)
const dirty = ref(false)

const observeCount = computed(() => {
  const rows = status.value.todayByOutcome || []
  return rows.reduce((n, r) => n + r.count, 0)
})

const quietText = computed(() => {
  const a = form.value.quietStartHour, b = form.value.quietEndHour
  return a === b ? '不静默' : `${a}:00 - ${b}:00`
})

const tendencyHint = computed(() => ({
  0: '拿不准就不说，多数时候沉默',
  1: '有话说就说，不强行刷存在感',
  2: '像群里的熟人，有机会就接一句'
}[form.value.liveliness ?? 1]))

const pendingText = computed(() => {
  const w = status.value.pendingWindows || []
  return w.length ? w.map((x) => `${x.groupId.slice(0, 6)}…:${x.pending}`).join('  ') : '无'
})

function emptyForm() {
  return {
    enabled: false, windowSeconds: 60, windowMaxMessages: 12, cooldownMinutes: 45,
    maxPerGroupPerDay: 3, maxGlobalPerDay: 12, quietStartHour: 23, quietEndHour: 8,
    liveliness: 1, allowActiveChannel: true,
    activePerGroupPerMinute: 3, activePerGroupPerDay: 20, activePerAccountPerMinute: 20,
    updatedAt: null
  }
}

const fmt = (s) => (s ? new Date(s + (s.endsWith('Z') ? '' : 'Z')).toLocaleString('zh-CN', { hour12: false }) : '-')
const outcomeLabel = (o) => outcomeMap[o]?.[0] || o
const outcomeType = (o) => outcomeMap[o]?.[1] || 'info'
const markDirty = () => { dirty.value = true }

const loadConfig = async () => {
  const { data } = await api.get('/proactive/config')
  form.value = { ...emptyForm(), ...data }
  if (data.ceilingGroupPerMinute) {
    ceilings.value = {
      groupPerMinute: data.ceilingGroupPerMinute,
      groupPerDay: data.ceilingGroupPerDay,
      accountPerMinute: data.ceilingAccountPerMinute
    }
  }
  dirty.value = false
}

const loadStatus = async () => {
  const { data } = await api.get('/proactive/status')
  status.value = data
}

const loadLogs = async () => {
  logsLoading.value = true
  try {
    const params = { page: page.value, pageSize }
    if (filterOutcome.value) params.outcome = filterOutcome.value
    const { data } = await api.get('/proactive/logs', { params })
    logs.value = data.items
    logTotal.value = data.totalCount
  } finally {
    logsLoading.value = false
  }
}

const reloadLogs = () => { page.value = 1; loadLogs() }
const onPage = (p) => { page.value = p; loadLogs() }

const save = async () => {
  saving.value = true
  try {
    const { data } = await api.put('/proactive/config', form.value)
    ElMessage.success(data.message)
    form.value = { ...emptyForm(), ...data.config }
    dirty.value = false
    await loadStatus()
  } finally {
    saving.value = false
  }
}

const loadAll = async () => {
  await Promise.all([loadConfig(), loadStatus(), loadLogs()])
}

onMounted(loadAll)
</script>

<style scoped>
.card-head { display: flex; justify-content: space-between; align-items: center; }
.master { display: flex; align-items: center; gap: 12px; }
.hint { color: #909399; font-size: 12px; margin-left: 10px; }
.subline { color: #909399; font-size: 12px; width: 100%; }
</style>
