<template>
  <div class="dashboard">
    <el-alert v-if="statsStale" :title="statsLoaded ? '消息统计暂未更新，下面保留上一次数据。' : '消息统计暂时无法读取，稍后会自动重试。'" type="warning" show-icon :closable="false" class="dashboard-notice" />
    <section class="stat-grid" aria-label="消息与用量概览" :aria-busy="!statsLoaded && !statsStale">
      <el-card v-for="card in cards" :key="card.label" shadow="never" class="stat-card">
        <div class="stat-label">{{ card.label }}<el-icon aria-hidden="true"><component :is="card.icon" /></el-icon></div>
        <div class="stat-value">{{ card.value }}</div>
        <div class="stat-context">{{ card.context }}</div>
      </el-card>
    </section>
    <div v-if="stats.legacyReplyCount || stats.unknownUsageCount" class="stats-footnote">
      <p v-if="stats.legacyReplyCount">累计 Token 含 {{ stats.legacyReplyCount }} 条历史回复用量；实际模型请求数从用量记账升级后开始记录。</p>
      <p v-if="stats.unknownUsageCount">{{ stats.unknownUsageCount }} 次请求未返回完整用量，累计 Token 只包含已知部分。</p>
    </div>

    <RuntimePanel :runtime="runtime" :stale="runtimeStale" :loading="!runtimeLoaded && !runtimeStale" :loaded="runtimeLoaded">
      <template #health>
        <el-card shadow="never" class="service-health">
          <template #header><div class="card-head"><b>服务状态</b><el-icon aria-hidden="true"><Connection /></el-icon></div></template>
          <dl class="service-status-list">
            <div><dt>QQ 网关</dt><dd><el-tag :type="botStatus.stale ? 'info' : status.isConnected ? 'success' : 'danger'" size="small" effect="light">{{ botStatus.stale ? '状态待更新' : status.isConnected ? '已连接' : '未连接' }}</el-tag></dd></div>
            <div><dt>上游 AI</dt><dd><el-tag :type="!runtimeLoaded || runtimeStale ? 'info' : runtime.upstream?.degraded ? 'warning' : 'success'" size="small" effect="light">{{ !runtimeLoaded ? '未获取' : runtimeStale ? '状态待更新' : runtime.upstream?.degraded ? '已降级' : '正常' }}</el-tag></dd></div>
            <div><dt>运行时长</dt><dd>{{ status.uptime || '—' }}</dd></div>
            <div><dt>成功调用 P95</dt><dd>{{ runtimeLoaded && runtime.sampleCount > (runtime.failureCount ?? 0) ? seconds(runtime.p95Ms) : '暂无成功样本' }}</dd></div>
            <div><dt>机器人昵称</dt><dd>{{ status.botNickname || '—' }}</dd></div>
          </dl>
          <p v-if="runtimeLoaded && runtime.upstream?.degraded" class="health-warning">{{ runtime.upstream.reason || '上游 AI 服务已降级，请查看运行诊断。' }}</p>
          <p class="health-updated">{{ runtimeUpdatedAt ? `运行数据获取于 ${runtimeUpdatedAt}` : '正在等待运行数据' }}</p>
          <el-collapse class="connection-details">
            <el-collapse-item title="连接信息" name="connection">
              <dl class="service-status-list">
                <div><dt>Bot QQ</dt><dd>{{ status.botQQ || '—' }}</dd></div>
                <div><dt>App ID</dt><dd>{{ qq.appId || '—' }}</dd></div>
                <div><dt>API 地址</dt><dd>{{ qq.apiBaseUrl || '—' }}</dd></div>
              </dl>
              <p v-if="qqUnavailable" class="health-updated">连接配置暂时无法读取。</p>
            </el-collapse-item>
          </el-collapse>
        </el-card>
      </template>
    </RuntimePanel>
  </div>
</template>

<script setup>
import { ref, computed, onMounted, onUnmounted } from 'vue'
import { ChatDotRound, ChatLineRound, Cpu, Coin, Connection } from '@element-plus/icons-vue'
import api from '../api'
import { botStatus } from '../botStatus'
import { useVisiblePolling } from '../useVisiblePolling'
import RuntimePanel from '../components/RuntimePanel.vue'

const status = computed(() => botStatus.data)
const qq = ref({})
const qqUnavailable = ref(false)
const qqRequest = new AbortController()
const stats = ref({})
const statsLoaded = ref(false)
const statsStale = ref(false)
const runtime = ref({})
const runtimeLoaded = ref(false)
const runtimeStale = ref(false)
const runtimeUpdatedAt = ref('')
const seconds = (value) => Number.isFinite(value) ? `${(value / 1000).toFixed(1)} 秒` : '—'
const number = (value) => Number.isFinite(value) ? value.toLocaleString('zh-CN') : '—'

const load = async (signal) => {
  const results = await Promise.allSettled([
    api.get('/chat/stats', { signal, skipErrorMessage: true }),
    api.get('/runtime', { signal, skipErrorMessage: true })
  ])
  if (signal.aborted) return
  statsStale.value = results[0].status !== 'fulfilled'
  if (!statsStale.value) { stats.value = results[0].value.data; statsLoaded.value = true }
  runtimeStale.value = results[1].status !== 'fulfilled'
  if (!runtimeStale.value) {
    runtime.value = results[1].value.data
    runtimeLoaded.value = true
    runtimeUpdatedAt.value = new Date().toLocaleTimeString('zh-CN', { hour12: false })
  }
}

const cards = computed(() => [
  { label: '累计消息', value: number(stats.value.totalMessages), context: '已记录的全部聊天消息', icon: ChatDotRound },
  { label: '今日消息', value: number(stats.value.todayMessages), context: '北京时间 00:00 至今', icon: ChatLineRound },
  { label: '累计模型请求', value: number(stats.value.aiCallCount), context: '含回复、主动评估与探测', icon: Cpu },
  { label: '已知累计 Token', value: number(stats.value.totalTokensUsed), context: '仅统计已返回的用量', icon: Coin }
])

useVisiblePolling(load, 5000)
onMounted(async () => {
  try { qq.value = (await api.get('/config/qqbot', { signal: qqRequest.signal, skipErrorMessage: true })).data }
  catch { if (!qqRequest.signal.aborted) qqUnavailable.value = true }
})
onUnmounted(() => qqRequest.abort())
</script>

<style scoped>
.dashboard-notice { margin-bottom: 16px; }
.stat-grid { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: 16px; margin-bottom: 22px; }
.stat-label { display: flex; align-items: center; justify-content: space-between; gap: 8px; color: var(--panel-text-muted); font-size: 13px; }
.stat-label .el-icon { color: var(--panel-text-muted); font-size: 17px; }
.stat-value { margin: 12px 0 7px; color: var(--panel-text); font-size: clamp(24px, 2.2vw, 32px); line-height: 1.4; font-weight: 650; letter-spacing: -.6px; font-variant-numeric: tabular-nums; overflow-wrap: anywhere; }
.stat-context { color: var(--panel-text-muted); font-size: 12px; }
.stats-footnote { margin: -6px 0 20px; font-size: 12px; line-height: 1.7; color: var(--panel-text-muted); }
.stats-footnote p { margin: 4px 0; }
.service-health { height: 100%; }
.service-status-list { margin: 0; }
.service-status-list > div { display: flex; align-items: baseline; justify-content: space-between; gap: 16px; padding: 13px 0; border-bottom: 1px solid var(--panel-border-soft); }
.service-status-list > div:first-child { padding-top: 0; }
.service-status-list dt { flex: none; color: var(--panel-text-muted); font-size: 13px; }
.service-status-list dd { margin: 0; text-align: right; font-size: 13px; overflow-wrap: anywhere; min-width: 0; }
.health-updated { margin: 16px 0 0; font-size: 12px; color: var(--panel-text-muted); }
.health-warning { color: var(--el-color-warning); font-size: 13px; line-height: 1.7; }
.connection-details { margin-top: 12px; border-bottom: 0; }
.connection-details :deep(.el-collapse-item__header) { font-size: 13px; border-bottom: 0; color: var(--panel-text-muted); background: transparent; }
.connection-details :deep(.el-collapse-item__wrap) { border-bottom: 0; background: transparent; }
.connection-details :deep(.el-collapse-item__content) { padding-bottom: 0; }
@media (max-width: 1200px) { .stat-grid { grid-template-columns: repeat(2, minmax(0, 1fr)); } }
@media (max-width: 600px) {
  .stat-grid { gap: 12px; margin-bottom: 16px; }
  .stat-label { font-size: 12px; }
  .stat-label .el-icon { display: none; }
  .stat-value { font-size: 25px; }
}
</style>
