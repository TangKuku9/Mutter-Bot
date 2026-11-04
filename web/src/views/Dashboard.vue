<template>
  <div>
    <el-row :gutter="16">
      <el-col :xs="12" :sm="6" v-for="c in cards" :key="c.label">
        <el-card shadow="never">
          <div class="stat-label">{{ c.label }}</div>
          <div class="stat-value" :style="{ color: c.color }">{{ c.value }}</div>
        </el-card>
      </el-col>
    </el-row>
    <p v-if="stats.legacyReplyCount" class="note">累计 Token 含 {{ stats.legacyReplyCount }} 条历史回复用量；实际模型请求数从本次升级后开始记录。</p>
    <p v-if="stats.unknownUsageCount" class="note">{{ stats.unknownUsageCount }} 次请求未返回完整用量，累计 Token 只包含已知部分。</p>

    <el-card shadow="never" style="margin-top: 16px">
      <template #header><b>机器人状态</b></template>
      <el-descriptions :column="2" border>
        <el-descriptions-item label="连接状态">
          <el-tag :type="status.isConnected ? 'success' : 'danger'" size="small">
            {{ status.isConnected ? '已连接' : '未连接' }}
          </el-tag>
        </el-descriptions-item>
        <el-descriptions-item label="运行时长">{{ status.uptime || '-' }}</el-descriptions-item>
        <el-descriptions-item label="Bot QQ">{{ status.botQQ || '-' }}</el-descriptions-item>
        <el-descriptions-item label="昵称">{{ status.botNickname || '-' }}</el-descriptions-item>
        <el-descriptions-item label="QQ 网关">{{ qq.appId || '-' }}</el-descriptions-item>
        <el-descriptions-item label="API 地址">{{ qq.apiBaseUrl || '-' }}</el-descriptions-item>
      </el-descriptions>
    </el-card>
    <RuntimePanel :runtime="runtime" :stale="runtimeStale" />
  </div>
</template>

<script setup>
import { ref, computed, onMounted } from 'vue'
import api from '../api'
import { botStatus } from '../botStatus'
import { useVisiblePolling } from '../useVisiblePolling'
import RuntimePanel from '../components/RuntimePanel.vue'

const status = computed(() => botStatus.data)
const qq = ref({})
const stats = ref({})
const runtime = ref({})
const runtimeStale = ref(false)

const load = async (signal) => {
  const results = await Promise.allSettled([
    api.get('/chat/stats', { signal, skipErrorMessage: true }),
    api.get('/runtime', { signal, skipErrorMessage: true })
  ])
  if (signal.aborted) return
  if (results[0].status === 'fulfilled') stats.value = results[0].value.data
  runtimeStale.value = results[1].status !== 'fulfilled'
  if (!runtimeStale.value) runtime.value = results[1].value.data
}

const cards = computed(() => [
  { label: '总消息数', value: stats.value.totalMessages ?? '-', color: '#409eff' },
  { label: '今日消息', value: stats.value.todayMessages ?? '-', color: '#67c23a' },
  { label: '实际模型请求', value: stats.value.aiCallCount ?? '-', color: '#e6a23c' },
  { label: '已知累计 Token', value: (stats.value.totalTokensUsed ?? 0).toLocaleString(), color: '#f56c6c' }
])

useVisiblePolling(load, 5000)
onMounted(async () => { try { qq.value = (await api.get('/config/qqbot')).data } catch { /* 统一提示 */ } })
</script>

<style scoped>
.stat-label { color: #909399; font-size: 13px; }
.note { color: var(--el-text-color-secondary); font-size: 12px; }
.stat-value { font-size: 26px; font-weight: 700; margin-top: 6px; }
</style>
