<template>
  <div>
    <el-row :gutter="16">
      <el-col :span="6" v-for="c in cards" :key="c.label">
        <el-card shadow="never">
          <div class="stat-label">{{ c.label }}</div>
          <div class="stat-value" :style="{ color: c.color }">{{ c.value }}</div>
        </el-card>
      </el-col>
    </el-row>

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
  </div>
</template>

<script setup>
import { ref, computed, onMounted, onUnmounted } from 'vue'
import api from '../api'

const status = ref({})
const qq = ref({})
const stats = ref({})
let timer = null

const load = async () => {
  const [s, q, st] = await Promise.all([
    api.get('/status'), api.get('/config/qqbot'), api.get('/chat/stats')
  ])
  status.value = s.data
  qq.value = q.data
  stats.value = st.data
}

const cards = computed(() => [
  { label: '总消息数', value: stats.value.totalMessages ?? '-', color: '#409eff' },
  { label: '今日消息', value: stats.value.todayMessages ?? '-', color: '#67c23a' },
  { label: 'AI 调用次数', value: stats.value.aiCallCount ?? '-', color: '#e6a23c' },
  { label: '累计 Token', value: (stats.value.totalTokensUsed ?? 0).toLocaleString(), color: '#f56c6c' }
])

onMounted(() => { load().catch(() => {}); timer = setInterval(() => load().catch(() => {}), 15000) })
onUnmounted(() => clearInterval(timer))
</script>

<style scoped>
.stat-label { color: #909399; font-size: 13px; }
.stat-value { font-size: 26px; font-weight: 700; margin-top: 6px; }
</style>
