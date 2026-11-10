<template>
  <div class="runtime-panel">
    <el-alert v-if="stale" :title="loaded ? '运行状态暂未更新，下面保留上一次数据。' : '运行数据暂时无法读取，稍后会自动重试。'" type="warning" show-icon :closable="false" class="runtime-notice" />
    <div class="runtime-overview">
      <el-card shadow="never" class="runtime-activity">
        <template #header><div class="card-head"><b>当前与近期任务</b><span class="runtime-refresh-note">每 5 秒更新</span></div></template>
        <div class="queue-summary" aria-label="当前处理队列">
          <div><span>正在处理</span><strong>{{ loaded ? queue.running ?? 0 : '—' }}<small v-if="loaded"> / {{ queue.parallelism ?? 3 }}</small></strong></div>
          <div><span>等待处理</span><strong>{{ loaded ? queue.waiting ?? 0 : '—' }}<small> 条</small></strong></div>
          <div><span>队列容量</span><strong>{{ loaded ? queue.capacity ?? 256 : '—' }}<small> 条</small></strong></div>
        </div>
        <el-tabs v-model="activityTab" class="runtime-tabs" aria-label="任务与模型请求">
          <el-tab-pane label="会话任务" name="tasks">
            <el-table :data="tasks" v-loading="loading" max-height="420" :empty-text="emptyText('暂无会话任务')">
              <el-table-column label="会话 / 发起方" min-width="150" show-overflow-tooltip>
                <template #default="{ row }"><div class="task-conversation">{{ conversationName(row.conversationId) }}</div><div class="task-description">{{ row.description || '—' }}</div></template>
              </el-table-column>
              <el-table-column label="类型" width="100"><template #default="{ row }">{{ row.background ? '主动评估' : '用户请求' }}</template></el-table-column>
              <el-table-column label="状态" min-width="148"><template #default="{ row }"><el-tag size="small" :type="runtimeType(row.outcome || row.state)">{{ runtimeLabel(row.outcome || row.state) }}</el-tag></template></el-table-column>
              <el-table-column label="进入队列" width="166"><template #default="{ row }"><span class="runtime-time">{{ formatTime(row.enqueuedAt) }}</span></template></el-table-column>
              <el-table-column label="操作" width="100" fixed="right"><template #default="{ row }"><el-button link type="primary" @click="showDetail(row.id)">查看过程</el-button></template></el-table-column>
            </el-table>
            <p class="note activity-note">优先展示当前任务，以及本次启动最近 100 条任务中的前 30 条。</p>
          </el-tab-pane>
          <el-tab-pane label="模型请求" name="calls">
            <el-table :data="runtime.calls || []" v-loading="loading" max-height="420" :empty-text="emptyText('暂无模型调用记录')">
              <el-table-column prop="model" label="模型" min-width="140" show-overflow-tooltip />
              <el-table-column label="用途" width="100"><template #default="{ row }">{{ row.purpose === 'proactive' ? '主动发言' : row.purpose === 'reply' ? '聊天回复' : row.purpose === 'probe' ? '手动探测' : '其他调用' }}</template></el-table-column>
              <el-table-column label="耗时" width="90"><template #default="{ row }">{{ seconds(row.elapsedMs) }}</template></el-table-column>
              <el-table-column label="结果" min-width="150"><template #default="{ row }"><el-tag size="small" :type="runtimeType(row.state)">{{ runtimeLabel(row.state) }}</el-tag></template></el-table-column>
              <el-table-column label="请求时间" width="166"><template #default="{ row }"><span class="runtime-time">{{ formatTime(row.startedAt) }}</span></template></el-table-column>
              <el-table-column label="操作" width="100" fixed="right"><template #default="{ row }"><el-button link type="primary" @click="showDetail(row.operationId)">查看过程</el-button></template></el-table-column>
            </el-table>
            <p class="note activity-note">展示近期模型请求。已保存的调用与发送结果可在重启后继续查看。</p>
          </el-tab-pane>
        </el-tabs>
      </el-card>
      <aside v-if="$slots.health" class="runtime-health" aria-label="服务状态"><slot name="health" /></aside>
    </div>

    <el-card shadow="never" class="runtime-diagnostics">
      <el-collapse>
        <el-collapse-item name="diagnostics">
          <template #title><div class="diagnostics-heading"><el-icon aria-hidden="true"><Monitor /></el-icon><span>运行诊断</span><span class="diagnostics-subtitle">耗时、队列与缓存</span></div></template>
          <div v-if="!loaded" class="note">{{ emptyText('暂无运行数据') }}</div>
          <template v-else>
            <dl class="diagnostic-grid">
              <div><dt>成功调用平均耗时</dt><dd>{{ runtime.sampleCount > (runtime.failureCount ?? 0) ? seconds(runtime.averageMs) : '暂无成功样本' }}</dd></div>
              <div><dt>成功调用 P95</dt><dd>{{ runtime.sampleCount > (runtime.failureCount ?? 0) ? seconds(runtime.p95Ms) : '暂无成功样本' }}</dd></div>
              <div><dt>近期失败 / 取消</dt><dd>{{ runtime.failureCount ?? 0 }} 次</dd></div>
              <div><dt>繁忙拒绝 / 排队超时</dt><dd>{{ queue.rejected ?? 0 }} / {{ queue.expired ?? 0 }}</dd></div>
              <div><dt>重复 @ 合并</dt><dd>{{ queue.coalesced ?? 0 }} 次</dd></div>
              <div><dt>图片缓存</dt><dd>{{ megabytes(runtime.imageCache?.bytes) }} / {{ megabytes(runtime.imageCache?.byteLimit) }} MB</dd></div>
            </dl>
            <p class="note">耗时取最近 {{ runtime.sampleCount ?? 0 }} 次已完成模型请求中的成功样本，不含排队、图片准备与发送。队列计数自本次启动累计。</p>
            <p class="note">上游 AI：{{ upstream.degraded ? '已降级' : '正常' }}{{ upstream.reason ? ` · ${upstream.reason}` : '' }}。滚动窗口最近 {{ (upstream.windowSuccess ?? 0) + (upstream.windowFailure ?? 0) }} 次调用，成功 {{ upstream.windowSuccess ?? 0 }} / 失败 {{ upstream.windowFailure ?? 0 }}；P50 {{ seconds(upstream.p50Ms) }} / P95 {{ seconds(upstream.p95Ms) }}。</p>
            <p v-if="upstream.degraded" class="note">上游连续失败时暂停主动发言评估、缩短排队等待，并加快放弃挂起的请求。</p>
            <p class="note">图片缓存 {{ runtime.imageCache?.entries ?? 0 }} 项；正在下载 {{ runtime.imageCache?.downloads ?? 0 }} 张。</p>
          </template>
        </el-collapse-item>
      </el-collapse>
    </el-card>
  </div>

  <el-dialog v-model="detailVisible" title="处理经过" width="min(900px, 94vw)" @closed="detailRequest.cancel()">
    <div v-loading="detailLoading">
      <el-alert v-if="detailError" :title="detailError" type="warning" show-icon :closable="false" />
      <el-timeline v-if="detail.task?.steps?.length" class="operation-timeline">
        <el-timeline-item v-for="(step, index) in detail.task.steps" :key="index" :timestamp="formatTime(step.at)">{{ runtimeLabel(step.stage) }}<span v-if="step.detail"> · {{ step.detail }}</span></el-timeline-item>
      </el-timeline>
      <p v-else-if="!detailLoading && !detailError" class="note">任务步骤只保留在本次启动的近期记录中；已保存的模型请求和发送结果列在下面。</p>
      <el-table :data="detail.calls || []" empty-text="尚无已完成的模型请求">
        <el-table-column label="阶段" width="130"><template #default="{ row }">{{ runtimeLabel(row.stage) }}</template></el-table-column>
        <el-table-column prop="model" label="模型" show-overflow-tooltip />
        <el-table-column label="耗时" width="90"><template #default="{ row }">{{ seconds(row.elapsedMs) }}</template></el-table-column>
        <el-table-column label="输入 / 输出 Token" width="150"><template #default="{ row }">{{ row.promptTokens ?? '未知' }} / {{ row.completionTokens ?? '未知' }}</template></el-table-column>
        <el-table-column label="结果" min-width="140"><template #default="{ row }">{{ runtimeLabel(row.state) }}<span v-if="row.httpStatus">（{{ row.httpStatus }}）</span></template></el-table-column>
      </el-table>
      <el-card v-for="delivery in detail.deliveries || []" :key="delivery.id" shadow="never" class="delivery-card"><el-tag :type="runtimeType(delivery.state)">{{ runtimeLabel(delivery.state) }}</el-tag><p class="delivery-content">{{ delivery.content }}</p><p v-if="delivery.detail" class="note">{{ delivery.detail }}</p></el-card>
    </div>
    <template #footer><el-button @click="loadDetail">刷新过程</el-button><el-button @click="detailVisible = false">关闭</el-button></template>
  </el-dialog>
</template>

<script setup>
import { ref, computed, onUnmounted } from 'vue'
import { Monitor } from '@element-plus/icons-vue'
import api from '../api'
import { LatestRequest } from '../latestRequest'
import { useVisiblePolling } from '../useVisiblePolling'
import { runtimeLabel, runtimeType, formatTime } from '../runtimeLabels'

const props = defineProps({ runtime: { type: Object, default: () => ({}) }, stale: Boolean, loading: Boolean, loaded: Boolean })
const queue = computed(() => props.runtime.queue || {})
const upstream = computed(() => props.runtime.upstream || {})
const tasks = computed(() => [...(queue.value.tasks || [])].sort((a, b) => {
  const active = (t) => t.state === 'running' ? 2 : t.state === 'queued' ? 1 : 0
  return active(b) - active(a) || new Date(b.enqueuedAt) - new Date(a.enqueuedAt)
}).slice(0, 30))
const activityTab = ref('tasks')
const detail = ref({})
const detailVisible = ref(false)
const detailLoading = ref(false)
const detailError = ref('')
const operationId = ref('')
const detailRequest = new LatestRequest()
const seconds = (value) => Number.isFinite(value) ? `${(value / 1000).toFixed(1)} 秒` : '—'
const megabytes = (value) => ((value || 0) / 1048576).toFixed(1)
const emptyText = (empty) => props.loaded ? empty : props.stale ? '数据暂不可用' : '正在读取数据…'
const conversationName = (id) => id?.startsWith('g:')
  ? props.runtime.groupNames?.[id.slice(2)] || `群 ${id.slice(2, 10)}…` : '私聊'

const loadDetail = async () => {
  if (!detailVisible.value || !operationId.value) return
  detailLoading.value = true
  const id = operationId.value
  try {
    await detailRequest.run((signal) => api.get(`/runtime/operations/${encodeURIComponent(id)}`, { signal, skipErrorMessage: true }), ({ data }) => { detail.value = data; detailError.value = '' })
  } catch { detailError.value = '处理过程暂时无法读取，请刷新重试。' }
  finally { if (!detailRequest.pending) detailLoading.value = false }
}
const showDetail = (id) => { operationId.value = id; detail.value = {}; detailError.value = ''; detailVisible.value = true; loadDetail() }
useVisiblePolling(loadDetail, 5000)
onUnmounted(() => detailRequest.cancel())
</script>

<style scoped>
.runtime-notice { margin-bottom: 16px; }
.runtime-overview { display: grid; grid-template-columns: minmax(0, 1fr) 300px; gap: 20px; align-items: stretch; }
.runtime-activity, .runtime-health { min-width: 0; }
.runtime-refresh-note { color: var(--panel-text-muted); font-size: 12px; font-weight: 400; }
.queue-summary { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 16px; padding-bottom: 20px; border-bottom: 1px solid var(--panel-border-soft); }
.queue-summary > div { display: flex; flex-direction: column; gap: 7px; }
.queue-summary > div + div { padding-left: 20px; border-left: 1px solid var(--panel-border-soft); }
.queue-summary > div > span { color: var(--panel-text-muted); font-size: 13px; }
.queue-summary strong { color: var(--panel-text); font-size: 23px; font-weight: 600; line-height: 1.4; font-variant-numeric: tabular-nums; }
.queue-summary small { color: var(--panel-text-muted); font-size: 13px; font-weight: 400; }
.runtime-tabs { margin-top: 8px; }
.runtime-tabs :deep(.el-tabs__header) { margin-bottom: 12px; }
.runtime-tabs :deep(.el-tabs__nav-wrap::after) { height: 1px; }
.task-conversation { color: var(--panel-text); font-weight: 500; }
.task-description { color: var(--panel-text-muted); font-size: 12px; }
.runtime-time { font-size: 12px; color: var(--panel-text-muted); font-variant-numeric: tabular-nums; }
.note { color: var(--panel-text-muted); font-size: 12px; line-height: 1.75; }
.activity-note { margin: 14px 0 0; }
.runtime-diagnostics { margin-top: 20px; }
.runtime-diagnostics :deep(.el-card__body) { padding-top: 0; padding-bottom: 0; }
.runtime-diagnostics :deep(.el-collapse), .runtime-diagnostics :deep(.el-collapse-item__header), .runtime-diagnostics :deep(.el-collapse-item__wrap) { border: 0; background: transparent; }
.runtime-diagnostics :deep(.el-collapse-item__header) { min-height: 62px; height: auto; }
.runtime-diagnostics :deep(.el-collapse-item__content) { padding-bottom: 18px; }
.diagnostics-heading { display: flex; flex-wrap: wrap; align-items: center; gap: 9px; padding: 13px 0; font-size: 14px; color: var(--panel-text-regular); }
.diagnostics-heading > .el-icon { color: var(--panel-accent-text); font-size: 17px; }
.diagnostics-subtitle { color: var(--panel-text-muted); font-size: 12px; font-weight: 400; }
.diagnostic-grid { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 18px; margin: 0 0 20px; }
.diagnostic-grid dt { font-size: 13px; color: var(--panel-text-muted); }
.diagnostic-grid dd { margin: 6px 0 0; font-size: 15px; color: var(--panel-text); font-variant-numeric: tabular-nums; }
.delivery-card { margin-top: 14px; }
.delivery-content { white-space: pre-wrap; overflow-wrap: anywhere; }
.operation-timeline { padding: 0 0 0 4px; margin-top: 18px; }
@media (max-width: 1200px) {
  .runtime-overview { grid-template-columns: minmax(0, 1fr); }
}
@media (max-width: 600px) {
  .queue-summary { gap: 9px; }
  .queue-summary > div + div { padding-left: 10px; }
  .queue-summary > div > span, .queue-summary small { font-size: 12px; }
  .queue-summary strong { font-size: 21px; }
  .diagnostic-grid { grid-template-columns: repeat(2, minmax(0, 1fr)); }
  .diagnostics-subtitle { display: none; }
}
</style>
