<template>
  <el-card shadow="never" class="proactive-overview">
    <div class="overview-heading">
      <div class="overview-intro">
        <div class="overview-title"><h2>择机发言</h2><el-tag :type="runtime.type" size="small">{{ runtime.label }}</el-tag></div>
        <p>观察群聊，在合适的时机自然接话。</p>
      </div>
      <div class="overview-actions">
        <label class="overview-enable"><span>启用</span><el-switch :model-value="enabled" :disabled="!config || saving" aria-label="启用择机发言" @change="changeEnabled" /></label>
        <ParamHelpButton label="择机发言开关说明" @click="$emit('help')" />
        <el-button size="small" :loading="refreshing" :disabled="saving" @click="$emit('refresh')">刷新</el-button>
      </div>
    </div>

    <div class="overview-runtime" :class="`runtime-${runtime.type}`" role="status">
      <span class="runtime-dot" aria-hidden="true"></span><span>{{ runtime.text }}</span>
      <el-button v-if="runtime.inspect" link type="primary" size="small" @click="diagnosticsOpen = true">检查接收</el-button>
    </div>
    <div v-if="dirty" class="overview-draft" role="status">
      <span>配置有未保存的修改，保存后生效</span>
      <el-button type="primary" size="small" :loading="saving" :disabled="!config || refreshing" @click="$emit('save')">保存配置</el-button>
    </div>

    <dl class="overview-metrics" aria-label="择机发言概览">
      <div><dt>今日接话</dt><dd><strong>{{ number(status.todaySent) }}</strong><span>次</span></dd><p>已成功发送</p></div>
      <div><dt>今日决策</dt><dd><strong>{{ number(status.todayEvaluations) }}</strong><span>次</span></dd><p>包含决定不发言</p></div>
      <div><dt>参与群聊</dt><dd><strong>{{ number(status.participatingGroups) }}</strong><span>个</span></dd><p>群响应与择机均已开启</p></div>
    </dl>

    <div class="overview-context">
      <div class="pending-context">
        <el-icon aria-hidden="true"><Clock /></el-icon>
        <span v-if="!ready">待评估窗口尚未获取</span>
        <span v-else-if="!pendingWindows.length">暂无待评估窗口</span>
        <details v-else class="pending-details">
          <summary>待评估 <b>{{ pendingWindows.length }} 个群 · {{ number(pendingCount) }} 条消息</b></summary>
          <ul><li v-for="window in pendingWindows" :key="window.groupId"><span>{{ window.name || window.groupId }}</span><span>{{ number(window.pending) }} 条消息</span></li></ul>
        </details>
      </div>
      <div><el-icon aria-hidden="true"><Moon /></el-icon><span><template v-if="quietHoursText(config) !== '不静默'">静默 </template><b>{{ quietHoursText(config) }}</b><span v-if="quietHoursText(config) !== '不静默'" class="context-note">（北京时间）</span></span></div>
      <div><el-icon aria-hidden="true"><Collection /></el-icon><span>今日参考群记忆 <b>{{ number(status.todayMemoryInjections) }} 次</b></span></div>
    </div>

    <details :open="diagnosticsOpen" class="overview-details" @toggle="diagnosticsOpen = $event.target.open">
      <summary><span><el-icon aria-hidden="true"><Connection /></el-icon>接收诊断</span><span class="details-scope">启动以来<el-icon aria-hidden="true"><ArrowRight /></el-icon></span></summary>
      <div class="diagnostic-body">
        <dl class="received-metrics"><div><dt>全量群消息</dt><dd>{{ number(status.fullModeArrived) }} 条</dd></div><div><dt>@ 消息</dt><dd>{{ number(status.atModeArrived) }} 条</dd></div></dl>
        <p class="diagnostic-result" :class="{ 'receive-warning': ready && status.enabled && status.fullModeArrived === 0 && status.atModeArrived > 0 }">{{ receiveText }}</p>
        <p v-if="status.enabled && status.fullModeArrived === 0 && status.atModeArrived > 0" class="diagnostic-hint">检查开放平台的 GROUP_MESSAGE_CREATE 事件订阅，以及群设置中的“可获取的群聊消息范围”。</p>
        <p class="diagnostic-note">接收计数自本次进程启动累计；今日接话、决策和群记忆次数按北京时间自然日统计。</p>
      </div>
    </details>

    <details v-if="status.quotas?.length" class="overview-details quota-details">
      <summary><span>主动消息用量</span><span class="details-scope">{{ status.quotas.length }} 个群<el-icon aria-hidden="true"><ArrowRight /></el-icon></span></summary>
      <el-table :data="status.quotas" size="small" class="quota-table">
        <el-table-column label="群" min-width="140"><template #default="{ row }">{{ row.name || row.groupId }}</template></el-table-column>
        <el-table-column prop="usedToday" label="今日主动" width="100" />
        <el-table-column prop="quota" label="每日上限" width="100" />
        <el-table-column prop="sentLastMinute" label="本分钟" width="90" />
        <el-table-column prop="minuteQuota" label="分钟上限" width="100" />
        <el-table-column label="接收状态" width="110"><template #default="{ row }"><el-tag size="small" :type="row.rejected ? 'danger' : 'success'">{{ row.rejected ? '已被关闭' : '正常' }}</el-tag></template></el-table-column>
      </el-table>
    </details>
  </el-card>
</template>

<script setup>
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { ArrowRight, Clock, Collection, Connection, Moon } from '@element-plus/icons-vue'
import ParamHelpButton from './ParamHelpButton.vue'
import { proactiveRuntime, quietHoursText } from '../proactiveOverview'

const props = defineProps({
  status: { type: Object, required: true },
  config: { type: Object, default: null },
  enabled: Boolean,
  dirty: Boolean,
  saving: Boolean,
  refreshing: Boolean,
  stale: Boolean
})
const emit = defineEmits(['update:enabled', 'change', 'help', 'save', 'refresh'])
const now = ref(Date.now())
const diagnosticsOpen = ref(false)
const ready = computed(() => typeof props.status.enabled === 'boolean')
const runtime = computed(() => props.stale
  ? { type: 'info', label: '状态待更新', text: ready.value ? '暂时无法刷新，以下保留上次获取的数据。' : '暂时无法获取运行数据，请稍后刷新。' }
  : proactiveRuntime(props.status, props.config, now.value))
const pendingWindows = computed(() => ready.value ? (props.status.pendingWindows || []).filter(window => window.pending > 0) : [])
const pendingCount = computed(() => pendingWindows.value.reduce((total, window) => total + window.pending, 0))
const receiveText = computed(() => {
  if (!ready.value) return '接收数据尚未获取。'
  if (!props.status.enabled) return '开关已关闭，当前不提示接收异常。'
  if (props.status.fullModeArrived > 0) return '全量群消息已到达。累计计数不代表当前连接状态。'
  return props.status.atModeArrived > 0 ? '只收到 @ 消息，平台尚未下发全量群消息。' : '本次启动后尚未收到群消息，可在群内发送消息后刷新查看。'
})
const number = value => Number.isFinite(value) ? value.toLocaleString('zh-CN') : '—'
const changeEnabled = value => { emit('update:enabled', value); emit('change') }
let clockTimer
onMounted(() => { clockTimer = setInterval(() => { now.value = Date.now() }, 15_000) })
onUnmounted(() => clearInterval(clockTimer))
</script>

<style scoped>
.overview-heading, .overview-title, .overview-actions, .overview-enable { display: flex; align-items: center; }
.overview-heading { justify-content: space-between; flex-wrap: wrap; gap: 16px; }
.overview-intro { flex: 1; min-width: 180px; }
.overview-title { gap: 10px; flex-wrap: wrap; }
.overview-title h2 { margin: 0; font-size: 16px; line-height: 1.5; font-weight: 600; color: var(--panel-text); }
.overview-intro p { margin: 5px 0 0; font-size: 13px; color: var(--panel-text-muted); }
.overview-actions { gap: 8px; flex-wrap: wrap; }
.overview-actions :deep(.q) { margin-left: 0; }
.overview-enable { gap: 9px; font-size: 13px; color: var(--panel-text-regular); }
.overview-runtime { display: flex; align-items: baseline; flex-wrap: wrap; gap: 8px; margin-top: 20px; color: var(--panel-text-muted); font-size: 13px; }
.runtime-dot { width: 6px; height: 6px; border-radius: 50%; flex: none; align-self: flex-start; margin-top: 8px; background: currentColor; }
.runtime-success .runtime-dot { color: var(--el-color-success); }
.runtime-warning { color: var(--el-color-warning); }
.overview-draft { display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: 10px; margin-top: 12px; padding: 10px 12px; background: var(--el-color-warning-light-9); color: var(--el-color-warning); border-radius: 8px; font-size: 13px; }
.overview-metrics { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 24px; margin: 26px 0 22px; }
.overview-metrics dt { color: var(--panel-text-muted); font-size: 13px; }
.overview-metrics dd { display: flex; align-items: baseline; flex-wrap: wrap; gap: 5px; margin: 5px 0; color: var(--panel-text-muted); font-size: 13px; }
.overview-metrics strong { color: var(--panel-text); font-size: 30px; line-height: 1.4; font-weight: 600; letter-spacing: -.5px; font-variant-numeric: tabular-nums; overflow-wrap: anywhere; }
.overview-metrics p { margin: 0; color: var(--panel-text-muted); font-size: 12px; }
.overview-context { display: flex; align-items: flex-start; flex-wrap: wrap; gap: 12px 24px; padding-top: 17px; border-top: 1px solid var(--panel-border-soft); }
.overview-context > div { display: flex; align-items: flex-start; gap: 7px; color: var(--panel-text-muted); font-size: 12px; min-width: 0; max-width: 100%; }
.overview-context .el-icon { margin-top: 3px; flex: none; font-size: 15px; }
.overview-context b { color: var(--panel-text); font-weight: 500; font-variant-numeric: tabular-nums; }
.context-note { color: var(--panel-text-muted); }
.pending-details { min-width: 0; }
summary { cursor: pointer; }
.pending-details ul { list-style: none; padding: 9px 0 0; margin: 0; }
.pending-details li { display: flex; justify-content: space-between; gap: 20px; padding: 5px 0; }
.pending-details li > span:first-child { overflow-wrap: anywhere; }
.pending-details li > span:last-child { flex: none; }
.overview-details { margin-top: 20px; border-top: 1px solid var(--panel-border-soft); }
.overview-details > summary { list-style: none; display: flex; align-items: center; justify-content: space-between; gap: 12px; padding-top: 17px; color: var(--panel-text-regular); font-size: 13px; }
.overview-details > summary::-webkit-details-marker { display: none; }
.overview-details > summary > span, .details-scope { display: flex; align-items: center; gap: 7px; }
.details-scope { color: var(--panel-text-muted); font-size: 12px; }
.overview-details[open] > summary .details-scope .el-icon { transform: rotate(90deg); }
.diagnostic-body { padding-top: 16px; }
.received-metrics { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 20px; margin: 0; }
.received-metrics dt { color: var(--panel-text-muted); font-size: 12px; }
.received-metrics dd { margin: 4px 0 0; color: var(--panel-text); font-size: 14px; font-variant-numeric: tabular-nums; overflow-wrap: anywhere; }
.diagnostic-result { margin: 14px 0 0; color: var(--panel-text-regular); font-size: 13px; }
.receive-warning { color: var(--el-color-warning); }
.diagnostic-hint, .diagnostic-note { margin: 9px 0 0; color: var(--panel-text-muted); font-size: 12px; overflow-wrap: anywhere; }
.quota-table { margin-top: 16px; }
html.compact .overview-metrics { margin-top: 20px; margin-bottom: 18px; }
html.compact .overview-runtime { margin-top: 16px; }
html.compact .overview-context { padding-top: 14px; }
html.compact .overview-details { margin-top: 16px; }
@media (max-width: 600px) {
  .overview-metrics { gap: 10px; margin-top: 22px; }
  .overview-metrics strong { font-size: 27px; }
  .overview-metrics p { display: none; }
  .overview-context { flex-direction: column; gap: 11px; }
}
@media (pointer: coarse) { summary, .overview-enable { min-height: 44px; } }
</style>
