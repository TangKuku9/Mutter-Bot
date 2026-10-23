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
        <ParamHelpButton :label="helpLabel('enabled')" @click="openHelp('enabled')" />
        <el-button v-if="dirty" size="small" type="warning" plain @click="save">保存后生效</el-button>
      </div>

      <el-alert v-if="status.deliveryHint" :type="status.fullModeArrived > 0 ? 'success' : 'warning'"
        :closable="false" show-icon style="margin-bottom: 12px">
        <template #title>{{ status.deliveryHint }}</template>
      </el-alert>

      <el-alert v-if="form.webSearchEnabled && status.aiWebSearchEnabled === false" type="warning"
        :closable="false" show-icon style="margin-bottom: 12px">
        <template #title>
          择机联网核实已打开，但「AI 设置」里没有开启联网搜索，改写阶段不会真的检索。
        </template>
      </el-alert>
      <el-alert v-else-if="form.webSearchEnabled && status.aiWebSearchConfigured === false" type="warning"
        :closable="false" show-icon style="margin-bottom: 12px">
        <template #title>
          联网搜索已开启，但还没有可用的搜索通道：请回「AI 设置」完成探测并选用，否则改写会退化成普通请求。
        </template>
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
        <el-descriptions-item label="今日带记忆">{{ status.todayMemoryInjections ?? '-' }} 次</el-descriptions-item>
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
      <template #header>
        <div class="card-head">
          <b>参数</b>
          <el-button size="small" @click="openHelp('enabled')">参数说明</el-button>
        </div>
      </template>
      <el-form :model="form" label-width="160px" style="max-width: 760px">
        <el-divider content-position="left">时机</el-divider>
        <el-form-item label="攒批窗口（秒）">
          <el-input-number v-model="form.windowSeconds" :min="15" :max="600" :step="5" @change="markDirty" />
          <ParamHelpButton :label="helpLabel('windowSeconds')" @click="openHelp('windowSeconds')" />
        </el-form-item>
        <el-form-item label="窗口最多条数">
          <el-input-number v-model="form.windowMaxMessages" :min="2" :max="50" @change="markDirty" />
          <ParamHelpButton :label="helpLabel('windowMaxMessages')" @click="openHelp('windowMaxMessages')" />
        </el-form-item>
        <el-form-item label="发言倾向">
          <el-radio-group v-model="form.liveliness" @change="markDirty">
            <el-radio-button :value="0">克制</el-radio-button>
            <el-radio-button :value="1">正常</el-radio-button>
            <el-radio-button :value="2">主动</el-radio-button>
          </el-radio-group>
          <ParamHelpButton :label="helpLabel('liveliness')" @click="openHelp('liveliness')" />
        </el-form-item>
        <el-form-item label="说与不说">
          <ParamHelpButton :label="helpLabel('decision')" @click="openHelp('decision')" />
        </el-form-item>
        <el-form-item label="附图张数">
          <el-input-number v-model="form.maxAttachedImages" :min="0" :max="ceilings.attachedImages" @change="markDirty" />
          <ParamHelpButton :label="helpLabel('maxAttachedImages')" @click="openHelp('maxAttachedImages')" />
        </el-form-item>
        <el-form-item label="联网核实">
          <el-switch v-model="form.webSearchEnabled" @change="markDirty" />
          <ParamHelpButton :label="helpLabel('webSearchEnabled')" @click="openHelp('webSearchEnabled')" />
        </el-form-item>

        <el-divider content-position="left">判定规模（与「AI 设置」里的上下文/Token 无关）</el-divider>
        <el-form-item label="背景历史条数">
          <el-input-number v-model="form.historyMaxMessages" :min="0" :max="50" @change="markDirty" />
          <ParamHelpButton :label="helpLabel('historyMaxMessages')" @click="openHelp('historyMaxMessages')" />
        </el-form-item>
        <el-form-item label="判定输出上限">
          <el-input-number v-model="form.evalMaxTokens" :min="64" :max="8192" :step="64" @change="markDirty" />
          <ParamHelpButton :label="helpLabel('evalMaxTokens')" @click="openHelp('evalMaxTokens')" />
        </el-form-item>
        <el-form-item label="群记忆注入">
          <el-switch v-model="form.memoryInjectionEnabled" @change="markDirty" />
          <ParamHelpButton :label="helpLabel('memoryInjectionEnabled')" @click="openHelp('memoryInjectionEnabled')" />
        </el-form-item>

        <el-divider content-position="left">节流</el-divider>
        <el-form-item label="同群间隔（分钟）">
          <el-input-number v-model="form.cooldownMinutes" :min="1" :max="1440" @change="markDirty" />
          <ParamHelpButton :label="helpLabel('cooldownMinutes')" @click="openHelp('cooldownMinutes')" />
        </el-form-item>
        <el-form-item label="单群每日上限">
          <el-input-number v-model="form.maxPerGroupPerDay" :min="1" :max="200" @change="markDirty" />
          <ParamHelpButton :label="helpLabel('maxPerGroupPerDay')" @click="openHelp('maxPerGroupPerDay')" />
        </el-form-item>
        <el-form-item label="全部群每日上限">
          <el-input-number v-model="form.maxGlobalPerDay" :min="1" :max="500" @change="markDirty" />
          <ParamHelpButton :label="helpLabel('maxGlobalPerDay')" @click="openHelp('maxGlobalPerDay')" />
        </el-form-item>
        <el-form-item label="静默时段（北京时间）">
          <el-input-number v-model="form.quietStartHour" :min="0" :max="23" controls-position="right"
            style="width: 110px" @change="markDirty" />
          <span style="margin: 0 8px">至</span>
          <el-input-number v-model="form.quietEndHour" :min="0" :max="23" controls-position="right" style="width: 110px"
            @change="markDirty" />
          <ParamHelpButton :label="helpLabel('quietHours')" @click="openHelp('quietHours')" />
        </el-form-item>

        <el-divider content-position="left">通道</el-divider>
        <el-form-item label="允许真主动消息">
          <el-switch v-model="form.allowActiveChannel" @change="markDirty" />
          <ParamHelpButton :label="helpLabel('allowActiveChannel')" @click="openHelp('allowActiveChannel')" />
        </el-form-item>
        <el-form-item label="主动：单群每分钟">
          <el-input-number v-model="form.activePerGroupPerMinute" :min="1" :max="ceilings.groupPerMinute"
            @change="markDirty" />
          <ParamHelpButton :label="helpLabel('activePerGroupPerMinute')" @click="openHelp('activePerGroupPerMinute')" />
        </el-form-item>
        <el-form-item label="主动：单群每日">
          <el-input-number v-model="form.activePerGroupPerDay" :min="1" :max="ceilings.groupPerDay" @change="markDirty" />
          <ParamHelpButton :label="helpLabel('activePerGroupPerDay')" @click="openHelp('activePerGroupPerDay')" />
        </el-form-item>
        <el-form-item label="主动：账号每分钟">
          <el-input-number v-model="form.activePerAccountPerMinute" :min="1" :max="ceilings.accountPerMinute"
            @change="markDirty" />
          <ParamHelpButton :label="helpLabel('activePerAccountPerMinute')" @click="openHelp('activePerAccountPerMinute')" />
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
import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr'
import api from '../api'
import { useParamHelp } from '../paramHelp'
import ParamHelpButton from '../components/ParamHelpButton.vue'
import ParamHelpDrawer from '../components/ParamHelpDrawer.vue'

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
  failed_api_error: ['发送失败', 'danger']
}

const outcomeOptions = Object.entries(outcomeMap).map(([value, v]) => ({ value, label: v[0] }))

const form = ref(emptyForm())
const status = ref({})
const ceilings = ref({ groupPerMinute: 20, groupPerDay: 1000, accountPerMinute: 60, attachedImages: 10 })
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

const HELP_ORDER = [
  'enabled',
  'windowSeconds', 'windowMaxMessages', 'liveliness', 'decision', 'maxAttachedImages', 'webSearchEnabled',
  'historyMaxMessages', 'evalMaxTokens', 'memoryInjectionEnabled',
  'cooldownMinutes', 'maxPerGroupPerDay', 'maxGlobalPerDay', 'quietHours',
  'allowActiveChannel', 'activePerGroupPerMinute', 'activePerGroupPerDay', 'activePerAccountPerMinute'
]

const HELP = {
  enabled: {
    title: '启用择机发言',
    value: (f) => (f.enabled ? '已启用' : '已关闭'),
    lines: [
      '总熔断点。关掉后不再采集消息、不再调用模型、不再主动发言。',
      '每个群还要在「群管理」里单独勾选「AI 择机发言」，两处都打开才生效。'
    ]
  },
  windowSeconds: {
    title: '攒批窗口（秒）',
    value: (f) => `${f.windowSeconds} 秒`,
    lines: [
      '同一个群的消息攒满这个时长，才交给模型评估一次。',
      '窗口里的消息按时间顺序一起送过去，不是逐条判断。',
      '调大更省调用，但接话也变慢。'
    ]
  },
  windowMaxMessages: {
    title: '窗口最多条数',
    value: (f) => `${f.windowMaxMessages} 条`,
    lines: [
      '一个窗口最多留多少条消息，超出就丢掉最早的。',
      '攒够这个条数会提前评估，不必等窗口时间走满。'
    ]
  },
  liveliness: {
    title: '发言倾向',
    value: (f) => ({ 0: '克制', 1: '正常', 2: '主动' }[f.liveliness ?? 1] || '正常'),
    lines: [
      '克制：拿不准就不说。',
      '正常：有话说就说。',
      '主动：能接就接，宁可多一句。',
      '改的是判断标准本身，不是数值阈值，也不影响下面的节流与上限。'
    ]
  },
  decision: {
    title: '说与不说',
    value: () => '由模型判断',
    lines: [
      '由模型自己判断要不要开口，没有可调的置信度阈值。',
      '群友原话要不要写进聊天历史，在「群管理 → 围观范围」按群设置，这里不控制。'
    ]
  },
  maxAttachedImages: {
    title: '附图张数',
    value: (f) => (f.maxAttachedImages > 0 ? `${f.maxAttachedImages} 张` : '不看图'),
    lines: [
      '一次评估最多带几张图，取窗口内最近的。',
      '0 = 不看图，纯文字判断。',
      '表情包和动图不会附给模型（GIF 会把整次调用打成 500），实际附上的可能少于这个数。'
    ]
  },
  webSearchEnabled: {
    title: '联网核实',
    value: (f) => (f.webSearchEnabled ? '开启' : '关闭'),
    lines: [
      '开启后，判定认为这句话要说时效性事实（新闻、赛事、天气、价格、日期）时，会先联网核实，再把草稿润色成最终一句。',
      '只在模型主动给出检索词时才联网；闲聊、玩梗、情绪接话不会多打一次调用。',
      '需要同时在「AI 设置」里开启联网搜索并选用可用通道；那边没开时，这里打开也不会真的检索。',
      '默认关闭：升级后不会静默产生检索费用，也不会改变现有群的说话风格。',
      '联网改写失败或超时会直接发判定阶段的草稿；核实后觉得不该说则记为「联网后放弃」。'
    ]
  },
  historyMaxMessages: {
    title: '背景历史条数',
    value: (f) => `${f.historyMaxMessages} 条`,
    lines: [
      '判定时额外参考几条更早的群聊，用来认人设、跟话题走向。',
      '这段只作背景，不参与「有没有新话可说」的判断。'
    ]
  },
  evalMaxTokens: {
    title: '判定输出上限',
    value: (f) => `${f.evalMaxTokens}`,
    lines: [
      '判定与联网改写这两步共用的模型输出上限，与「AI 设置」里的「最大 Tokens」互不影响。',
      '太低会因为思考被截断而白丢一次评估，建议 5000 以上。',
      '「AI 设置」里的「上下文条数」「最大 Tokens」只作用于 @ 回复与私聊。'
    ]
  },
  memoryInjectionEnabled: {
    title: '群记忆注入',
    value: (f) => (f.memoryInjectionEnabled ? '开启' : '关闭'),
    lines: [
      '打开后，把「群管理」里各群维护的摘要一并交给模型作背景参考。',
      '关掉只是暂停注入，摘要原样保留。要连内容一起删，去「群管理」清空。',
      '单个群可以在「群管理」里单独覆盖这一项。',
      '留痕会记下每次判定带没带记忆、带了多少字。'
    ]
  },
  cooldownMinutes: {
    title: '同群间隔（分钟）',
    value: (f) => `${f.cooldownMinutes} 分钟`,
    lines: [
      '同一个群两次主动发言之间至少隔这么久。',
      '间隔没到就直接跳过，不占当日额度。'
    ]
  },
  maxPerGroupPerDay: {
    title: '单群每日上限',
    value: (f) => `${f.maxPerGroupPerDay} 条`,
    lines: ['单个群一天最多主动说几条，按北京时间跨自然日重置。']
  },
  maxGlobalPerDay: {
    title: '全部群每日上限',
    value: (f) => `${f.maxGlobalPerDay} 条`,
    lines: ['所有群加起来一天最多主动说几条。']
  },
  quietHours: {
    title: '静默时段（北京时间）',
    value: (f) => (f.quietStartHour === f.quietEndHour ? '不静默' : `${f.quietStartHour}:00 - ${f.quietEndHour}:00`),
    lines: [
      '这段时间内不主动发言。跨零点按「起始 → 次日结束」算。',
      '两个值相同表示不静默。',
      '用北京时间，与服务器时区无关。'
    ]
  },
  allowActiveChannel: {
    title: '允许真主动消息',
    value: (f) => (f.allowActiveChannel ? '允许' : '不允许'),
    lines: [
      '被动槽位指收到消息后的 5 分钟，可以先回一条带 msg_id 的消息。',
      '打开后，槽位过期还能改发 msg_id 为空的主动消息；关掉就只能放弃这次发言。'
    ]
  },
  activePerGroupPerMinute: {
    title: '主动：单群每分钟',
    value: (f) => `${f.activePerGroupPerMinute} 条/分钟`,
    lines: [`平台上限 ${ceilings.value.groupPerMinute} 条/分钟，超出会被拒收，这里最多只能填到上限。`]
  },
  activePerGroupPerDay: {
    title: '主动：单群每日',
    value: (f) => `${f.activePerGroupPerDay} 条/日`,
    lines: [
      `平台上限 ${ceilings.value.groupPerDay} 条/日。`,
      '与「单群每日上限」是两套限制，先到哪个算哪个。'
    ]
  },
  activePerAccountPerMinute: {
    title: '主动：账号每分钟',
    value: (f) => `${f.activePerAccountPerMinute} 条/分钟`,
    lines: [
      `平台上限 ${ceilings.value.accountPerMinute} 条/分钟（已认证账号）。`,
      '按整个账号算，不区分群。'
    ]
  }
}

const {
  visible: helpVisible, index: helpIndex, total: helpTotal, title: helpTitle,
  lines: helpLines, current: helpCurrent, label: helpLabel, open: openHelp, step: stepHelp
} = useParamHelp(HELP, HELP_ORDER, () => form.value)

const pendingText = computed(() => {
  const w = status.value.pendingWindows || []
  return w.length
    ? w.map((x) => `${x.name || `${x.groupId.slice(0, 6)}…`}:${x.pending}`).join('  ')
    : '无'
})

function emptyForm() {
  return {
    enabled: false, windowSeconds: 60, windowMaxMessages: 12, cooldownMinutes: 45,
    maxPerGroupPerDay: 3, maxGlobalPerDay: 12, quietStartHour: 23, quietEndHour: 8,
    liveliness: 1, allowActiveChannel: true,
    maxAttachedImages: 3, historyMaxMessages: 8, evalMaxTokens: 512, webSearchEnabled: false,
    activePerGroupPerMinute: 3, activePerGroupPerDay: 20, activePerAccountPerMinute: 20,
    memoryInjectionEnabled: true,
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
      accountPerMinute: data.ceilingAccountPerMinute,
      attachedImages: data.ceilingAttachedImages
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

// 后端每写一行决策留痕（说与不说都算）就推一次 ProactiveEvaluated，
// 这里节流刷新：不翻页时留痕与今日统计自动跟上，翻页查看不被打断
let conn = null
let refreshTimer = null
const refreshSoon = () => {
  if (refreshTimer) return
  refreshTimer = setTimeout(async () => {
    refreshTimer = null
    await loadStatus()
    if (page.value === 1) await loadLogs()
  }, 1500)
}

const connectHub = async () => {
  conn = new HubConnectionBuilder()
    .withUrl('/hub/botstatus')
    .configureLogging(LogLevel.None)
    .withAutomaticReconnect()
    .build()
  conn.on('ProactiveEvaluated', refreshSoon)
  try { await conn.start() } catch { /* 面板离线时退回手动刷新 */ }
}

const disconnectHub = async () => {
  if (refreshTimer) { clearTimeout(refreshTimer); refreshTimer = null }
  if (conn) { await conn.stop().catch(() => {}); conn = null }
}

onMounted(async () => {
  await loadAll()
  await connectHub()
})

onUnmounted(disconnectHub)
</script>

<style scoped>
.card-head { display: flex; justify-content: space-between; align-items: center; }
.master { display: flex; align-items: center; gap: 12px; }
.hint { color: #909399; font-size: 12px; margin-left: 10px; }
</style>
