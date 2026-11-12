<template>
  <el-card shadow="never" class="logs-card">
    <div class="toolbar">
      <el-select v-model="levelFilter" placeholder="全部级别" aria-label="日志级别" clearable class="level-filter">
        <el-option v-for="l in levels" :key="l" :label="l" :value="l" />
      </el-select>
      <el-input v-model="keyword" placeholder="关键词过滤" aria-label="关键词过滤" clearable class="keyword-filter" />
      <div class="log-actions">
        <el-switch v-model="autoScroll" active-text="自动滚动" />
        <el-button @click="exportLogs">导出</el-button>
        <el-button @click="clearView">清空视图</el-button>
      </div>
      <el-tag size="small" :type="connOk ? 'success' : 'danger'" class="stream-status">
        {{ connOk ? '实时流已连接' : '实时流未连接' }}
      </el-tag>
    </div>

    <div class="log-box" ref="boxRef" role="region" aria-label="实时日志流" tabindex="0">
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
watch([filtered, autoScroll], () => {
  if (autoScroll.value) nextTick(() => { boxRef.value?.scrollTo(0, boxRef.value.scrollHeight) })
})
</script>

<style scoped>
.logs-card { display: flex; flex: 1; flex-direction: column; min-height: min-content; container: logs / inline-size; }
.logs-card :deep(.el-card__body) { display: flex; flex: 1; flex-direction: column; }
.toolbar { display: flex; flex: none; flex-wrap: wrap; align-items: center; gap: 12px; margin-bottom: 12px; }
.level-filter { width: 140px; max-width: 100%; }
.keyword-filter { flex: 1 1 220px; max-width: 360px; }
.log-actions { display: flex; flex-wrap: wrap; align-items: center; gap: 12px; }
.log-actions .el-button + .el-button { margin-left: 0; }
.stream-status { margin-left: auto; }
.log-box { flex: 1 0 160px; min-height: 160px; overflow: auto; scrollbar-gutter: stable; overscroll-behavior: contain; background: var(--panel-raised); border-radius: 8px; padding: 12px 16px; font-family: ui-monospace, Consolas, Menlo, monospace; font-size: 13px; }
.log-line { display: grid; grid-template-columns: 8ch minmax(0, 1fr); align-items: baseline; gap: 3px 12px; padding: 7px 0; line-height: 1.75; color: var(--panel-text-regular); }
.log-line + .log-line { border-top: 1px solid var(--panel-border-soft); }
.ts { color: var(--panel-text-muted); white-space: nowrap; font-variant-numeric: tabular-nums; }
.lv { font-weight: 600; white-space: nowrap; }
.cat { grid-column: 1 / -1; min-width: 0; white-space: normal; overflow-wrap: anywhere; color: var(--panel-accent-text); }
.msg { grid-column: 1 / -1; min-width: 0; white-space: pre-wrap; overflow-wrap: anywhere; }
.lv-information .lv { color: var(--el-color-success); }
.lv-warning .lv, .lv-warning .msg { color: var(--el-color-warning); }
.lv-error .lv, .lv-error .msg, .lv-critical .lv, .lv-critical .msg { color: var(--el-color-danger); }
.lv-debug .lv, .lv-trace .lv { color: var(--panel-text-muted); }
.empty { color: var(--panel-text-muted); text-align: center; padding: 40px 0; }
@container logs (max-width: 540px) {
  .keyword-filter { flex-basis: 100%; max-width: none; }
  .log-box { padding: 8px 12px; }
  .log-line { column-gap: 10px; }
}
</style>
