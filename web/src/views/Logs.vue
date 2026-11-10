<template>
  <el-card shadow="never" class="logs-card">
    <div class="toolbar">
      <el-select v-model="levelFilter" placeholder="全部级别" clearable style="width: 140px">
        <el-option v-for="l in levels" :key="l" :label="l" :value="l" />
      </el-select>
      <el-input v-model="keyword" placeholder="关键词过滤" clearable style="width: 220px" />
      <el-switch v-model="autoScroll" active-text="自动滚动" style="margin-left: 8px" />
      <el-button @click="exportLogs">导出</el-button>
      <el-button @click="clearView">清空视图</el-button>
      <el-tag size="small" :type="connOk ? 'success' : 'danger'" style="margin-left: auto">
        {{ connOk ? '实时流已连接' : '实时流未连接' }}
      </el-tag>
    </div>

    <div class="log-box" ref="boxRef">
      <div v-for="(l, i) in filtered" :key="i" class="log-line" :class="'lv-' + (l.level || '').toLowerCase()">
        <span class="ts">{{ fmt(l.timestamp) }}</span>
        <span class="lv">{{ l.level }}</span>
        <span class="cat">[{{ l.category }}]</span>
        <span class="msg">{{ l.message }}{{ l.exception ? '\n' + l.exception : '' }}</span>
      </div>
      <div v-if="filtered.length === 0" class="empty">暂无日志</div>
    </div>
  </el-card>
</template>

<script setup>
import { ref, computed, onMounted, onUnmounted, nextTick, watch } from 'vue'
import { subscribeRealtime, realtimeState } from '../realtime'

const levels = ['Trace', 'Debug', 'Information', 'Warning', 'Error', 'Critical']
const logs = ref([])
const levelFilter = ref('')
const keyword = ref('')
const autoScroll = ref(true)
const connOk = computed(() => realtimeState.connected)
const boxRef = ref(null)
let unsubscribe = null
let flushTimer = null
let pending = []
const MAX = 800
const clearView = () => { pending = []; logs.value = [] }

const filtered = computed(() => logs.value.filter((l) => {
  if (levelFilter.value && l.level !== levelFilter.value) return false
  if (keyword.value && !(`${l.message} ${l.category}`).toLowerCase().includes(keyword.value.toLowerCase())) return false
  return true
}))

const fmt = (s) => s ? new Date(s).toLocaleTimeString('zh-CN', { hour12: false }) : ''

const push = (entries) => {
  pending.push(...entries)
  if (pending.length > MAX) pending = pending.slice(-MAX)
  if (flushTimer) return
  flushTimer = setTimeout(() => {
    flushTimer = null
    const seen = new Set()
    logs.value = [...logs.value, ...pending].filter((entry) => {
      const key = `${entry.id ?? ''}:${entry.timestamp}:${entry.category}:${entry.message}`
      if (seen.has(key)) return false
      seen.add(key)
      return true
    }).slice(-MAX)
    pending = []
  }, 80)
}

const exportLogs = () => {
  const text = filtered.value
    .map((l) => `${l.timestamp} [${l.level}] [${l.category}] ${l.message}${l.exception ? '\n' + l.exception : ''}`)
    .join('\n')
  const blob = new Blob([text], { type: 'text/plain;charset=utf-8' })
  const a = document.createElement('a')
  a.href = URL.createObjectURL(blob)
  a.download = `mutter-logs-${new Date().toISOString().slice(0, 19).replace(/[:T]/g, '-')}.txt`
  a.click()
  URL.revokeObjectURL(a.href)
}

onMounted(() => {
  unsubscribe = subscribeRealtime({ ReceiveLogs: push, ReceiveLog: (entry) => push([entry]) }, { logs: true })
})
onUnmounted(() => {
  unsubscribe?.()
  clearTimeout(flushTimer)
  pending = []
})
watch(filtered, () => {
  if (autoScroll.value) nextTick(() => { boxRef.value?.scrollTo(0, boxRef.value.scrollHeight) })
})
</script>

<style scoped>
.toolbar { display: flex; align-items: center; gap: 10px; margin-bottom: 12px; }
.log-box { height: min(640px, calc(100dvh - 340px)); min-height: 320px; overflow-y: auto; background: var(--panel-raised); border-radius: 8px; padding: 16px; font-family: ui-monospace, Consolas, Menlo, monospace; font-size: 13px; }
.log-line { white-space: pre-wrap; overflow-wrap: anywhere; line-height: 1.75; color: var(--panel-text-regular); }
.ts { color: var(--panel-text-muted); margin-right: 8px; }
.lv { display: inline-block; width: 82px; font-weight: 600; }
.cat { color: var(--panel-accent-text); margin-right: 8px; }
.lv-information .lv { color: var(--el-color-success); }
.lv-warning .lv, .lv-warning .msg { color: var(--el-color-warning); }
.lv-error .lv, .lv-error .msg, .lv-critical .lv, .lv-critical .msg { color: var(--el-color-danger); }
.lv-debug .lv, .lv-trace .lv { color: var(--panel-text-muted); }
.empty { color: var(--panel-text-muted); text-align: center; padding: 40px 0; }
</style>
