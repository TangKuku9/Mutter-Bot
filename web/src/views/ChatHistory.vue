<template>
  <el-card shadow="never" class="chat-history-card">
    <div class="chat-workspace">
      <aside class="chat-groups" aria-label="群聊列表">
        <div class="groups-heading"><h2>群聊</h2><span>{{ groups.length }}</span></div>
        <el-input v-model="groupSearch" placeholder="查找群聊" aria-label="查找群聊" :prefix-icon="Search" clearable size="small" class="group-search" />
        <div class="group-list">
          <button type="button" class="group-button all-groups" :aria-pressed="!groupId" @click="selectGroup('')"><el-icon aria-hidden="true"><Collection /></el-icon><span><b>全部记录</b><small>群聊与已保存的私聊</small></span></button>
          <button v-for="group in filteredGroups" :key="group.groupOpenId" type="button" class="group-button" :aria-pressed="groupId === group.groupOpenId" :title="group.name || group.groupOpenId" @click="selectGroup(group.groupOpenId)"><span class="group-avatar" aria-hidden="true">{{ (group.name || '群').slice(-1) }}</span><span><b>{{ group.name || shortId(group.groupOpenId) }}</b><small>{{ groupSummary(group.config) }}</small></span></button>
          <p v-if="groupSearch && !filteredGroups.length" class="group-empty">未找到对应群聊</p>
        </div>
        <div v-if="groupsError" class="group-empty"><p>暂时无法读取群列表</p><el-button link type="primary" size="small" @click="loadGroups">重试</el-button></div>
        <p class="group-note">消息与决策，围绕同一个群展开。</p>
      </aside>
      <section class="chat-conversation" aria-label="聊天记录">
        <header class="conversation-heading"><div><h2>{{ selectedGroup?.name || (groupId ? shortId(groupId) : '全部记录') }}</h2><p>{{ groupDescription }}</p></div><el-tag v-if="selectedGroup?.config?.isEnabled === false" type="info" size="small">群聊已停用</el-tag><el-tag v-else-if="selectedGroup?.config?.aiProactiveEnabled" size="small">群级择机已开启</el-tag></header>
        <div class="mobile-group-picker"><el-select v-model="groupId" placeholder="选择群聊" aria-label="选择群聊" filterable @change="resetView"><el-option label="全部记录" value="" /><el-option v-for="group in groups" :key="group.groupOpenId" :value="group.groupOpenId" :label="group.name || shortId(group.groupOpenId)" /></el-select></div>
        <div class="chat-toolbar">
          <div class="source-filters" role="group" aria-label="消息来源"><button v-for="option in sources" :key="option.value" type="button" :aria-pressed="source === option.value" @click="setSource(option.value)">{{ option.label }}</button></div>
          <div class="chat-live"><el-switch v-model="live" aria-label="实时更新" @change="toggleLive" /><span>{{ live ? connOk ? '实时更新' : '连接中' : '已暂停' }}</span><el-button size="small" :loading="loading" @click="refreshLatest">刷新</el-button></div>
        </div>
        <form class="chat-search" role="search" @submit.prevent="applySearch"><el-input v-model="searchInput" aria-label="查找聊天记录" placeholder="查找内容、发送者或决策理由" :prefix-icon="Search" maxlength="200" clearable @clear="applySearch" /><el-button native-type="submit" :loading="loading">查找</el-button></form>
        <div v-if="keyword" class="search-summary" role="status"><span>查找“{{ keyword }}” · 共 {{ total }} 条记录</span><el-button link type="primary" size="small" @click="clearSearch">清除查找</el-button></div>
        <div v-if="hasNew" class="new-message-notice" role="status"><span>有新的消息或评估记录</span><el-button link type="primary" size="small" @click="refreshLatest">查看新记录</el-button></div>
        <el-alert v-if="loadError" title="聊天记录暂时无法读取，请刷新重试。" type="warning" :closable="false" show-icon class="history-error" />
        <div ref="stream" v-loading="loading" class="conversation-stream" aria-label="会话内容" :aria-busy="loading" @scroll="onScroll">
          <div v-if="!loading && !entries.length && !loadError" class="chat-empty"><span class="empty-brand" aria-hidden="true"></span><h3>{{ keyword ? '未找到匹配记录' : source ? '当前没有这类记录' : '还没有已保存的聊天记录' }}</h3><p>{{ keyword ? '可以更换关键词，或调整群聊与消息来源。' : '收到并保存消息后，会在这里按时间展示。' }}</p><el-button v-if="keyword || source" link type="primary" @click="showAll">查看全部记录</el-button><p v-if="source === 'observe' && !keyword" class="observe-help">如需回看普通群消息，请在群管理中选择“围观并记录全部消息”。</p></div>
          <template v-for="entry in entries" :key="entry.key">
            <div v-if="entry.kind === 'day'" class="chat-day"><span>{{ entry.label }}</span></div>
            <article v-else-if="entry.kind === 'decision'" class="decision-event"><el-icon aria-hidden="true"><Clock /></el-icon><time>{{ chatTime(entry.createdAt) }}</time><el-tag :type="runtimeType(entry.outcome)" size="small">{{ runtimeLabel(entry.outcome) }}</el-tag><span v-if="!groupId" class="event-group">{{ entry.groupName || shortId(entry.groupId) }}</span><span class="event-reason"><HighlightedText :text="entry.reason || entry.topic || ''" :keyword="keyword" /></span><el-button link type="primary" size="small" @click="openDetails(entry)">查看原因<el-icon aria-hidden="true"><ArrowRight /></el-icon></el-button></article>
            <article v-else class="chat-message" :class="{ 'bot-message': entry.isBotMessage }"><span class="sender-avatar" :class="{ 'bot-avatar': entry.isBotMessage }" aria-hidden="true">{{ entry.isBotMessage ? '' : senderName(entry).slice(-1) }}</span><div class="message-body"><div class="message-meta"><b><HighlightedText :text="senderName(entry)" :keyword="keyword" /></b><time :title="chatDateTime(entry.createdAt)">{{ chatTime(entry.createdAt) }}</time><span class="message-source">{{ chatKind(entry) }}</span><span v-if="!groupId" class="message-group">{{ entry.chatType === 1 ? '私聊' : entry.groupName || shortId(entry.groupId) }}</span></div><p class="message-text"><HighlightedText :text="entry.plainText || (entry.hasImage ? '[图片]' : '[空消息]')" :keyword="keyword" /></p><span v-if="entry.hasImage && entry.plainText" class="image-note">附图片</span><div v-if="entry.isBotMessage" class="message-bottom"><span><el-icon aria-hidden="true"><Check /></el-icon>已保存回复</span><el-button v-if="entry.isProactive || entry.decisionId" link type="primary" size="small" @click="openDetails(entry)">{{ entry.decisionId ? '接话详情' : '历史详情' }}<el-icon aria-hidden="true"><TopRight /></el-icon></el-button></div></div></article>
          </template>
        </div>
        <footer class="conversation-footer"><div><span>共 {{ total }} 条记录</span><span>时间按北京时间显示</span></div><el-pagination v-if="total > pageSize" layout="prev, pager, next" :total="total" :page-size="pageSize" :current-page="page" :pager-count="5" prev-text="较新" next-text="更早" @current-change="onPage" /></footer>
      </section>
    </div>
  </el-card>
  <ChatDecisionDrawer v-model="drawerVisible" :entry="selectedEntry" />
</template>
<script setup>
import { ref, computed, nextTick, onMounted, onUnmounted } from 'vue'
import { Search, Collection, Clock, ArrowRight, TopRight, Check } from '@element-plus/icons-vue'
import { subscribeRealtime, realtimeState } from '../realtime'
import { LatestRequest, matchesChatMessage } from '../latestRequest'
import { chatTime, chatDateTime, chatKind, senderName, timelineEntries, groupSummary, groupRecordingNote } from '../chatHistory'
import { runtimeLabel, runtimeType } from '../runtimeLabels'
import HighlightedText from '../components/HighlightedText.vue'
import ChatDecisionDrawer from '../components/ChatDecisionDrawer.vue'
import api from '../api'
const items = ref([]), groups = ref([]), groupId = ref(''), groupSearch = ref(''), source = ref('')
const searchInput = ref(''), keyword = ref(''), snapshot = ref(null)
const page = ref(1), total = ref(0), pageSize = 50
const loading = ref(false), loadError = ref(false), groupsError = ref(false)
const live = ref(true), hasNew = ref(false), following = ref(true), stream = ref(null)
const drawerVisible = ref(false), selectedEntry = ref(null)
const request = new LatestRequest(), groupRequest = new AbortController()
const connOk = computed(() => live.value && realtimeState.connected)
const sources = [{ value: '', label: '全部' }, { value: 'observe', label: '围观' }, { value: 'at', label: '被 @' }, { value: 'bot', label: 'Bot 应答' }, { value: 'proactive', label: 'Bot 插话' }, { value: 'decision', label: '评估记录' }]
const selectedGroup = computed(() => groups.value.find(group => group.groupOpenId === groupId.value))
const filteredGroups = computed(() => groups.value.filter(group => `${group.name || ''} ${group.groupOpenId}`.toLocaleLowerCase().includes(groupSearch.value.toLocaleLowerCase())))
const groupDescription = computed(() => groupId.value ? groupRecordingNote(selectedGroup.value?.config) : '按时间查看已保存的消息与择机评估')
const entries = computed(() => timelineEntries(items.value))
const shortId = id => id ? `${id.slice(0, 8)}…` : '未记录群聊'
let unsubscribe = null, timer = null, disposed = false
const onScroll = () => { const el = stream.value; if (el) following.value = el.scrollHeight - el.scrollTop - el.clientHeight < 48 }
const load = async () => {
  loading.value = true; loadError.value = false
  const params = { page: page.value, pageSize, groupId: groupId.value || undefined, source: source.value || undefined, q: keyword.value || undefined, snapshot: snapshot.value || undefined }
  try {
    await request.run(signal => api.get('/chat/timeline', { params, signal, skipErrorMessage: true }), ({ data }) => {
      items.value = data.items; total.value = data.totalCount; page.value = data.page; snapshot.value = data.snapshot
      if (!params.snapshot) hasNew.value = false
      nextTick(() => { if (stream.value && following.value) stream.value.scrollTop = stream.value.scrollHeight })
    })
  } catch { loadError.value = true }
  finally { if (!request.pending) loading.value = false }
}
const cancelTimer = () => { if (timer) { clearTimeout(timer); timer = null } }
const resetView = () => { cancelTimer(); snapshot.value = null; page.value = 1; items.value = []; total.value = 0; following.value = true; hasNew.value = false; load() }
const refreshLatest = () => { cancelTimer(); snapshot.value = null; page.value = 1; following.value = true; load() }
const selectGroup = value => { if (groupId.value !== value) { groupId.value = value; resetView() } }
const setSource = value => { if (source.value !== value) { source.value = value; resetView() } }
const applySearch = () => { keyword.value = searchInput.value.trim(); resetView() }
const clearSearch = () => { searchInput.value = ''; applySearch() }
const showAll = () => { source.value = ''; searchInput.value = ''; keyword.value = ''; resetView() }
const onPage = value => { cancelTimer(); page.value = value; following.value = true; load() }
const openDetails = entry => { selectedEntry.value = entry; drawerVisible.value = true }
const loadGroups = async () => {
  try { const { data } = await api.get('/groups', { signal: groupRequest.signal, skipErrorMessage: true }); groups.value = data || []; groupsError.value = false }
  catch { if (!disposed) groupsError.value = true }
}
const signalNew = () => {
  if (disposed || !live.value) return
  if (drawerVisible.value || page.value !== 1 || keyword.value || !following.value) { hasNew.value = true; return }
  if (!timer) timer = setTimeout(() => { timer = null; if (!disposed) { if (!drawerVisible.value && page.value === 1 && !keyword.value && following.value) refreshLatest(); else hasNew.value = true } }, 1500)
}
const connect = () => {
  if (unsubscribe || disposed) return
  unsubscribe = subscribeRealtime({
    ReceiveMessage: message => { if (source.value !== 'decision' && matchesChatMessage(message, { groupId: groupId.value, source: source.value })) signalNew() },
    AiResponseCompleted: message => { if ((!groupId.value || message.groupId === groupId.value) && ['', 'bot', 'proactive'].includes(source.value)) signalNew() },
    ProactiveEvaluated: event => { if ((!groupId.value || event.groupId === groupId.value) && ['', 'decision', 'proactive'].includes(source.value)) signalNew() },
    Resync: signalNew
  })
}
const disconnect = () => { unsubscribe?.(); unsubscribe = null; cancelTimer() }
const toggleLive = on => { on ? connect() : disconnect() }
onMounted(async () => { connect(); await Promise.allSettled([load(), loadGroups()]) })
onUnmounted(() => { disposed = true; disconnect(); request.cancel(); groupRequest.abort() })
</script>
<style scoped>
.chat-history-card :deep(.el-card__body) { padding: 0; }
.chat-workspace { display: grid; grid-template-columns: 200px minmax(0, 1fr); }
.chat-groups { min-width: 0; border-right: 1px solid var(--panel-border-soft); }
.groups-heading { display: flex; justify-content: space-between; align-items: center; padding: 19px 18px 14px; }
.groups-heading h2 { margin: 0; font-size: 14px; font-weight: 600; }
.groups-heading > span { color: var(--panel-text-muted); font-size: 12px; }
.group-search { width: calc(100% - 28px); margin: 0 14px 12px; }
.group-list { max-height: 560px; overflow-y: auto; scrollbar-width: thin; }
.group-button { display: flex; align-items: center; gap: 10px; width: calc(100% - 16px); padding: 12px 10px; margin: 0 8px 4px; border: 0; border-radius: 8px; background: transparent; text-align: left; font-family: inherit; color: var(--panel-text-regular); cursor: pointer; }
.group-button:hover { background: var(--panel-raised); }
.group-button[aria-pressed="true"] { background: var(--panel-accent-soft); }
.group-button > span:last-child { min-width: 0; }
.group-button b { display: block; font-size: 13px; font-weight: 500; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.group-button[aria-pressed="true"] b { color: var(--panel-accent-text); }
.group-button small { display: block; margin-top: 3px; font-size: 12px; color: var(--panel-text-muted); }
.group-avatar { display: grid; place-items: center; width: 29px; height: 32px; flex: none; border-radius: 8px; background: var(--panel-raised); color: var(--panel-text-muted); font-size: 12px; }
.all-groups > .el-icon { width: 29px; font-size: 18px; flex: none; color: var(--panel-accent-text); }
.group-note, .group-empty { margin: 20px 18px; font-size: 12px; color: var(--panel-text-muted); }
.group-empty p { margin: 0; }
.chat-conversation { min-width: 0; }
.conversation-heading { display: flex; justify-content: space-between; align-items: center; flex-wrap: wrap; gap: 10px; padding: 20px 24px 16px; border-bottom: 1px solid var(--panel-border-soft); }
.conversation-heading h2 { margin: 0; font-size: 16px; font-weight: 600; overflow-wrap: anywhere; }
.conversation-heading p { margin: 4px 0 0; font-size: 12px; color: var(--panel-text-muted); }
.chat-toolbar { display: flex; justify-content: space-between; align-items: center; flex-wrap: wrap; gap: 12px; padding: 14px 18px 10px; }
.source-filters { display: flex; flex-wrap: wrap; gap: 3px; }
.source-filters button { background: transparent; color: var(--panel-text-muted); border: 0; border-radius: 6px; padding: 6px 9px; font: inherit; font-size: 13px; cursor: pointer; }
.source-filters button:hover { background: var(--panel-raised); }
.source-filters button[aria-pressed="true"] { color: var(--panel-accent-text); background: var(--panel-accent-soft); font-weight: 500; }
.chat-live { display: flex; align-items: center; gap: 7px; color: var(--panel-text-muted); font-size: 12px; }
.chat-live .el-button { margin-left: 5px; }
.chat-search { display: flex; align-items: center; gap: 8px; padding: 0 24px 16px; }
.chat-search > .el-input { min-width: 0; }
.search-summary, .new-message-notice { display: flex; justify-content: space-between; align-items: center; flex-wrap: wrap; gap: 8px; padding: 10px 24px; font-size: 13px; color: var(--panel-text-muted); border-top: 1px solid var(--panel-border-soft); }
.search-summary > span { overflow-wrap: anywhere; }
.new-message-notice { color: var(--panel-accent-text); background: var(--panel-accent-soft); }
.history-error { margin: 0 24px 12px; width: auto; }
.conversation-stream { min-height: 340px; max-height: min(720px, 65vh); overflow-y: auto; scrollbar-width: thin; padding: 0 24px 24px; overflow-anchor: none; }
.chat-day { display: flex; align-items: center; gap: 10px; margin: 14px 0 22px; color: var(--panel-text-muted); font-size: 12px; }
.chat-day::after { content: ''; flex: 1; height: 1px; background: var(--panel-border-soft); }
.chat-message { display: flex; align-items: flex-start; gap: 10px; margin-bottom: 24px; }
.chat-message:last-child { margin-bottom: 0; }
.sender-avatar { width: 30px; height: 30px; display: grid; place-items: center; flex: none; border-radius: 8px; background: var(--panel-raised); color: var(--panel-text-muted); font-size: 12px; }
.bot-avatar { background: var(--panel-accent-text); border-radius: 0; mask: url('/logo-mark.svg') center / contain no-repeat; }
.message-body { min-width: 0; flex: 1; }
.message-meta { display: flex; align-items: baseline; flex-wrap: wrap; gap: 6px 10px; margin-bottom: 6px; color: var(--panel-text-muted); font-size: 12px; }
.message-meta b { font-size: 13px; font-weight: 500; color: var(--panel-text-regular); overflow-wrap: anywhere; }
.message-meta time { font-variant-numeric: tabular-nums; }
.message-group { overflow-wrap: anywhere; }
.message-text { margin: 0; white-space: pre-wrap; overflow-wrap: anywhere; color: var(--panel-text); font-size: 14px; line-height: 1.85; }
.bot-message .message-text { background: var(--panel-accent-soft); border-radius: 2px 10px 10px 10px; padding: 11px 14px; }
.bot-message .message-source { color: var(--panel-accent-text); }
.message-bottom { display: flex; flex-wrap: wrap; align-items: center; gap: 12px; margin-top: 7px; font-size: 12px; color: var(--panel-text-muted); }
.message-bottom > span { display: flex; align-items: center; gap: 4px; }
.message-bottom .el-button .el-icon { margin-left: 4px; }
.image-note { display: inline-block; margin-top: 4px; color: var(--panel-text-muted); font-size: 12px; }
.decision-event { display: flex; align-items: baseline; flex-wrap: wrap; gap: 7px 10px; padding-top: 13px; margin: 4px 0 24px 40px; border-top: 1px solid var(--panel-border-soft); color: var(--panel-text-muted); font-size: 12px; }
.decision-event > .el-icon { align-self: center; }
.decision-event time { font-variant-numeric: tabular-nums; }
.event-reason { overflow-wrap: anywhere; }
.event-group { overflow-wrap: anywhere; }
.conversation-footer { display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: 12px; padding: 13px 24px; border-top: 1px solid var(--panel-border-soft); }
.conversation-footer > div:first-child { display: flex; flex-wrap: wrap; gap: 8px 16px; color: var(--panel-text-muted); font-size: 12px; }
.chat-empty { min-height: 310px; display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 12px; text-align: center; color: var(--panel-text-muted); }
.chat-empty h3 { margin: 0; font-size: 15px; font-weight: 500; color: var(--panel-text-regular); }
.chat-empty p { margin: 0; font-size: 13px; line-height: 1.8; }
.empty-brand { width: 38px; height: 30px; background: var(--panel-accent-text); mask: url('/logo-mark.svg') center / contain no-repeat; opacity: .65; }
.observe-help { max-width: 390px; }
.mobile-group-picker { display: none; }
html.compact .chat-message { margin-bottom: 18px; }
html.compact .conversation-heading { padding-top: 16px; padding-bottom: 13px; }
html.compact .decision-event { margin-bottom: 18px; }
@media (max-width: 1200px) { .chat-workspace { grid-template-columns: 180px minmax(0, 1fr); } .conversation-heading, .chat-search, .conversation-footer { padding-left: 18px; padding-right: 18px; } .conversation-stream { padding-left: 18px; padding-right: 18px; } }
@media (max-width: 700px) { .chat-workspace { display: block; } .chat-groups { display: none; } .mobile-group-picker { display: block; padding: 14px 16px 0; } .mobile-group-picker .el-select { width: 100%; } .conversation-heading { padding: 16px; } .chat-toolbar { padding: 12px 10px; gap: 8px; } .chat-search { padding: 0 16px 14px; } .conversation-stream { padding: 0 14px 20px; min-height: 300px; max-height: 65vh; } .sender-avatar { width: 26px; height: 26px; } .chat-message { gap: 8px; } .decision-event { margin-left: 34px; } .conversation-footer { padding: 12px 16px; } .search-summary, .new-message-notice { padding: 10px 16px; } .source-filters button { font-size: 12px; padding: 6px 8px; } }
@media (pointer: coarse) { .source-filters button, .group-button { min-height: 44px; } }
</style>
