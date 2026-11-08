<template>
  <el-card shadow="never" style="margin-top: 16px">
    <template #header><b>正在处理什么</b></template>
    <el-alert v-if="stale" title="运行状态暂未更新，下面保留上一次数据。" type="warning" :closable="false" />
    <el-descriptions :column="3" border style="margin-top: 10px">
      <el-descriptions-item label="等待处理">{{ queue.waiting ?? 0 }} 条</el-descriptions-item>
      <el-descriptions-item label="正在处理">{{ queue.running ?? 0 }} / {{ queue.parallelism ?? 3 }}</el-descriptions-item>
      <el-descriptions-item label="队列容量">{{ queue.capacity ?? 256 }} 条</el-descriptions-item>
      <el-descriptions-item label="成功调用平均耗时">{{ seconds(runtime.averageMs) }}</el-descriptions-item>
      <el-descriptions-item label="95% 成功调用耗时不超过">{{ seconds(runtime.p95Ms) }}</el-descriptions-item>
      <el-descriptions-item label="近期失败 / 取消">{{ runtime.failureCount ?? 0 }} 次</el-descriptions-item>
      <el-descriptions-item label="上游 AI 状态">
        <el-tag size="small" :type="upstream.degraded ? 'warning' : 'success'">{{ upstream.degraded ? '降级' : '正常' }}</el-tag>
        <span style="margin-left: 6px">{{ upstream.reason || '正常' }}</span>
      </el-descriptions-item>
    </el-descriptions>
    <p class="note">耗时取最近 {{ runtime.sampleCount ?? 0 }} 次已完成模型请求中的成功样本，不含排队与图片准备。队列计数自本次启动累计：繁忙拒绝 {{ queue.rejected ?? 0 }}，排队超时 {{ queue.expired ?? 0 }}，重复 @ 合并 {{ queue.coalesced ?? 0 }}。</p>
    <p class="note">上游 AI 滚动窗口（最近 {{ (upstream.windowSuccess ?? 0) + (upstream.windowFailure ?? 0) }} 次调用）：成功 {{ upstream.windowSuccess ?? 0 }} / 失败 {{ upstream.windowFailure ?? 0 }}，延迟 P50 {{ seconds(upstream.p50Ms) }} / P95 {{ seconds(upstream.p95Ms) }}。上游连续失败时机器人降级：暂停主动发言评估、缩短排队等待并加快放弃挂起的请求。</p>
    <p class="note">图片缓存 {{ megabytes(runtime.imageCache?.bytes) }} / {{ megabytes(runtime.imageCache?.byteLimit) }} MB，{{ runtime.imageCache?.entries ?? 0 }} 项；正在下载 {{ runtime.imageCache?.downloads ?? 0 }} 张。</p>

    <el-table :data="tasks" size="small" max-height="340" empty-text="暂无会话任务">
      <el-table-column label="进入队列" width="172"><template #default="{ row }">{{ formatTime(row.enqueuedAt) }}</template></el-table-column>
      <el-table-column label="会话" min-width="130" show-overflow-tooltip><template #default="{ row }">{{ conversationName(row.conversationId) }}</template></el-table-column>
      <el-table-column prop="description" label="发起方" min-width="100" show-overflow-tooltip />
      <el-table-column label="类型" width="100"><template #default="{ row }">{{ row.background ? '主动评估' : '用户请求' }}</template></el-table-column>
      <el-table-column label="状态" width="142"><template #default="{ row }"><el-tag size="small" :type="runtimeType(row.outcome || row.state)">{{ runtimeLabel(row.outcome || row.state) }}</el-tag></template></el-table-column>
      <el-table-column label="操作" width="105"><template #default="{ row }"><el-button link type="primary" @click="showDetail(row.id)">查看过程</el-button></template></el-table-column>
    </el-table>
    <p class="note">展示当前任务和本次启动最近 100 条任务中的前 30 条。下面的模型请求记录已保存，重启后仍可查看。</p>
    <el-table :data="runtime.calls || []" size="small" max-height="340" empty-text="暂无模型调用记录">
      <el-table-column label="模型请求时间" width="172"><template #default="{ row }">{{ formatTime(row.startedAt) }}</template></el-table-column>
      <el-table-column prop="model" label="模型" min-width="140" show-overflow-tooltip />
      <el-table-column label="用途" width="100"><template #default="{ row }">{{ row.purpose === 'proactive' ? '主动发言' : row.purpose === 'reply' ? '聊天回复' : row.purpose === 'probe' ? '手动探测' : '其他调用' }}</template></el-table-column>
      <el-table-column label="耗时" width="90"><template #default="{ row }">{{ seconds(row.elapsedMs) }}</template></el-table-column>
      <el-table-column label="结果" width="150"><template #default="{ row }"><el-tag size="small" :type="runtimeType(row.state)">{{ runtimeLabel(row.state) }}</el-tag></template></el-table-column>
      <el-table-column label="操作" width="105"><template #default="{ row }"><el-button link type="primary" @click="showDetail(row.operationId)">查看过程</el-button></template></el-table-column>
    </el-table>
  </el-card>

  <el-dialog v-model="detailVisible" title="处理经过" width="min(900px, 94vw)" @closed="detailRequest.cancel()">
    <div v-loading="detailLoading">
      <el-timeline v-if="detail.task?.steps?.length">
        <el-timeline-item v-for="(step, index) in detail.task.steps" :key="index" :timestamp="formatTime(step.at)">
          {{ runtimeLabel(step.stage) }}<span v-if="step.detail"> · {{ step.detail }}</span>
        </el-timeline-item>
      </el-timeline>
      <p v-else class="note">任务步骤只保留在本次启动的近期记录中；已保存的模型请求和发送结果列在下面。</p>
      <el-table :data="detail.calls || []" size="small" empty-text="尚无已完成的模型请求">
        <el-table-column label="阶段" width="130"><template #default="{ row }">{{ runtimeLabel(row.stage) }}</template></el-table-column>
        <el-table-column prop="model" label="模型" show-overflow-tooltip />
        <el-table-column label="耗时" width="90"><template #default="{ row }">{{ seconds(row.elapsedMs) }}</template></el-table-column>
        <el-table-column label="输入 / 输出 Token" width="150"><template #default="{ row }">{{ row.promptTokens ?? '未知' }} / {{ row.completionTokens ?? '未知' }}</template></el-table-column>
        <el-table-column label="结果" min-width="140"><template #default="{ row }">{{ runtimeLabel(row.state) }}<span v-if="row.httpStatus">（{{ row.httpStatus }}）</span></template></el-table-column>
      </el-table>
      <el-card v-for="delivery in detail.deliveries || []" :key="delivery.id" shadow="never" style="margin-top: 12px">
        <el-tag :type="runtimeType(delivery.state)">{{ runtimeLabel(delivery.state) }}</el-tag>
        <p class="delivery-content">{{ delivery.content }}</p>
        <p v-if="delivery.detail" class="note">{{ delivery.detail }}</p>
      </el-card>
    </div>
    <template #footer><el-button @click="loadDetail">刷新过程</el-button><el-button @click="detailVisible = false">关闭</el-button></template>
  </el-dialog>
</template>

<script setup>
import { ref, computed, onUnmounted } from 'vue'
import api from '../api'
import { LatestRequest } from '../latestRequest'
import { useVisiblePolling } from '../useVisiblePolling'
import { runtimeLabel, runtimeType, formatTime } from '../runtimeLabels'

const props = defineProps({ runtime: { type: Object, default: () => ({}) }, stale: Boolean })
const queue = computed(() => props.runtime.queue || {})
const upstream = computed(() => props.runtime.upstream || {})
const tasks = computed(() => [...(queue.value.tasks || [])].sort((a, b) => {
  const active = (t) => t.state === 'running' ? 2 : t.state === 'queued' ? 1 : 0
  return active(b) - active(a) || new Date(b.enqueuedAt) - new Date(a.enqueuedAt)
}).slice(0, 30))
const detail = ref({})
const detailVisible = ref(false)
const detailLoading = ref(false)
const operationId = ref('')
const detailRequest = new LatestRequest()
const seconds = (value) => `${((value || 0) / 1000).toFixed(1)} 秒`
const megabytes = (value) => ((value || 0) / 1048576).toFixed(1)
const conversationName = (id) => id?.startsWith('g:')
  ? props.runtime.groupNames?.[id.slice(2)] || `群 ${id.slice(2, 10)}…` : '私聊'

const loadDetail = async () => {
  if (!detailVisible.value || !operationId.value) return
  detailLoading.value = true
  const id = operationId.value
  try {
    await detailRequest.run((signal) => api.get(`/runtime/operations/${encodeURIComponent(id)}`, {
      signal, skipErrorMessage: true
    }), ({ data }) => { detail.value = data })
  } catch { /* 统一错误提示 */ }
  finally { if (!detailRequest.pending) detailLoading.value = false }
}
const showDetail = (id) => { operationId.value = id; detail.value = {}; detailVisible.value = true; loadDetail() }
useVisiblePolling(loadDetail, 5000)
onUnmounted(() => detailRequest.cancel())
</script>

<style scoped>
.note { color: var(--el-text-color-secondary); font-size: 12px; line-height: 1.7; }
.delivery-content { white-space: pre-wrap; overflow-wrap: anywhere; }
</style>
