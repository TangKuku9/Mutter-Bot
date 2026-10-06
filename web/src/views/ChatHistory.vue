<template>
  <el-card shadow="never">
    <div class="toolbar">
      <el-select v-model="groupId" placeholder="全部群" clearable filterable style="width: 240px" @change="reload">
        <el-option v-for="g in groupOptions" :key="g.value" :label="g.label" :value="g.value" />
      </el-select>

      <el-radio-group v-model="source" size="small" @change="reload">
        <el-radio-button value="">全部</el-radio-button>
        <el-radio-button value="observe">围观（未@）</el-radio-button>
        <el-radio-button value="at">被 @</el-radio-button>
        <el-radio-button value="bot">Bot 应答</el-radio-button>
        <el-radio-button value="proactive">Bot 插话</el-radio-button>
      </el-radio-group>

      <el-switch v-model="live" active-text="实时" style="margin-left: 4px" @change="toggleLive" />
      <el-tag size="small" :type="connOk ? 'success' : 'info'">{{ connOk ? '流已连' : '流未连' }}</el-tag>
      <span class="count">共 {{ total }} 条</span>
      <el-button size="small" @click="reload">刷新</el-button>
    </div>

    <el-alert v-if="source === 'observe' && total === 0" type="warning" :closable="false" show-icon
      style="margin-top: 10px"
      title="围观一条消息都没记录到">
      先在「群管理」把该群的围观范围设为「围观并记录全部消息」，再确认开放平台已订阅
      GROUP_MESSAGE_CREATE、且群设置里“可获取的群聊消息范围”是全部消息。「择机发言」页的事件计数能直接看出平台有没有下发。
    </el-alert>

    <el-table :data="items" v-loading="loading" stripe style="margin-top: 12px" :row-class-name="rowClass">
      <el-table-column label="时间" width="170">
        <template #default="{ row }">{{ fmt(row.createdAt) }}</template>
      </el-table-column>
      <el-table-column v-if="!groupId" label="群" width="150" show-overflow-tooltip>
        <template #default="{ row }">{{ row.groupName || shortId(row.groupId) }}</template>
      </el-table-column>
      <el-table-column label="来源" width="110">
        <template #default="{ row }">
          <el-tag size="small" :type="kindOf(row).type">{{ kindOf(row).label }}</el-tag>
        </template>
      </el-table-column>
      <el-table-column prop="userName" label="发送者" width="140" show-overflow-tooltip />
      <el-table-column label="内容">
        <template #default="{ row }">
          <span>{{ row.plainText || (row.hasImage ? '[图片]' : '') }}</span>
        </template>
      </el-table-column>
    </el-table>

    <el-pagination
      v-if="total > 0"
      style="margin-top: 12px; justify-content: flex-end"
      layout="total, prev, pager, next"
      :total="total" :page-size="pageSize" :current-page="page"
      @current-change="(p) => { page = p; load() }"
    />
  </el-card>
</template>

<script setup>
import { ref, onMounted, onUnmounted } from 'vue'
import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr'
import api from '../api'

const items = ref([])
const total = ref(0)
const page = ref(1)
const pageSize = 50
const loading = ref(false)
const groupId = ref('')
const source = ref('')
const groupOptions = ref([])
const live = ref(true)
const connOk = ref(false)
let conn = null

// 库里存的是 UTC 且序列化不带时区标记，直接 new Date() 会被当成本地时间，
// 于是刚发的消息会显示成 8 小时前——看起来就像“记录里查不到”。
const fmt = (s) => {
  if (!s) return '-'
  const iso = /[zZ]$|[+-]\d\d:?\d\d$/.test(s) ? s : s + 'Z'
  return new Date(iso).toLocaleString('zh-CN', { hour12: false })
}

const shortId = (id) => (id ? id.slice(0, 8) + '…' : '私聊')

const kindOf = (row) => {
  if (row.isBotMessage) return row.isProactive
    ? { label: 'Bot 插话', type: 'success' }
    : { label: 'Bot 应答', type: 'warning' }
  if (row.chatType === 1) return { label: '私聊', type: 'info' }
  return row.hasAtBot ? { label: '被 @', type: 'primary' } : { label: '围观', type: 'info' }
}

// 围观是背景信息，不该和真正的内容抢视线；Bot 插话要显眼一点，方便回看它到底说了什么
const rowClass = ({ row }) => {
  if (row.isProactive) return 'row-proactive'
  if (!row.isBotMessage && !row.hasAtBot && row.chatType !== 1) return 'row-observe'
  return ''
}

const matches = (m) => {
  if (groupId.value && m.groupId !== groupId.value) return false
  if (source.value === 'at') return !m.isBotMessage && m.hasAtBot
  if (source.value === 'observe') return !m.isBotMessage && !m.hasAtBot && m.chatType !== 1
  if (source.value === 'bot') return m.isBotMessage && !m.isProactive
  if (source.value === 'proactive') return m.isProactive
  return true
}

const load = async () => {
  loading.value = true
  try {
    const { data } = await api.get('/chat/history', {
      params: { page: page.value, pageSize, groupId: groupId.value || undefined, source: source.value || undefined }
    })
    items.value = data.items
    total.value = data.totalCount
  } finally { loading.value = false }
}

const reload = () => { page.value = 1; load() }

// 群下拉取自群管理接口：从首页消息里归纳会漏掉最近没发言的群，也只能显示 openid 前几位
const loadGroups = async () => {
  const { data } = await api.get('/groups')
  groupOptions.value = (data || []).map((g) => ({
    value: g.groupOpenId,
    label: g.name || `${g.groupOpenId.slice(0, 8)}…`
  }))
}

let throttle = null
const refreshSoon = () => {
  if (throttle) return
  throttle = setTimeout(() => { throttle = null; if (page.value === 1) load() }, 1500)
}

const connect = async () => {
  conn = new HubConnectionBuilder()
    .withUrl('/hub/botstatus')
    .configureLogging(LogLevel.None)
    .withAutomaticReconnect()
    .build()

  conn.on('ReceiveMessage', (m) => {
    total.value += 1
    if (page.value === 1 && matches(m)) items.value = [m, ...items.value].slice(0, pageSize)
  })
  // Bot 的应答与择机插话不走 ReceiveMessage，靠 AI 完成事件回推整页
  conn.on('AiResponseCompleted', refreshSoon)
  conn.on('ConnectionStatusChanged', () => { connOk.value = conn.state === 'Connected' })
  conn.onreconnected(() => { connOk.value = true })
  conn.onclose(() => { connOk.value = false })

  try { await conn.start(); connOk.value = true } catch { connOk.value = false }
}

const disconnect = async () => {
  if (conn) { await conn.stop().catch(() => {}); conn = null }
  connOk.value = false
}

const toggleLive = async (on) => { on ? await connect() : await disconnect() }

onMounted(async () => {
  await Promise.all([load(), loadGroups()])
  if (live.value) await connect()
})

onUnmounted(() => {
  if (throttle) clearTimeout(throttle)
  disconnect()
})
</script>

<style scoped>
.toolbar { display: flex; gap: 10px; align-items: center; flex-wrap: wrap; }
.count { color: #909399; font-size: 12px; margin-left: auto; }
:deep(.row-observe) { color: #909399; }
:deep(.row-proactive) { background: #f0f9eb; }
</style>
