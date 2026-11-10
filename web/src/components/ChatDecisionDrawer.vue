<template>
  <el-drawer v-model="visible" :size="narrow ? '100%' : '420px'" class="chat-decision-drawer" title="接话详情" destroy-on-close>
    <div v-if="loading" class="detail-loading" role="status">正在读取决策留痕…</div>
    <div v-else-if="error" class="detail-empty" role="status"><h3>{{ error }}</h3><p>聊天正文仍可查看。</p><el-button size="small" @click="load">重试</el-button></div>
    <div v-else-if="!detail" class="detail-empty"><span class="detail-brand" aria-hidden="true"></span><h3>未记录决策详情</h3><p>这条历史消息没有可精确关联的决策留痕。</p><p>原有聊天内容可以继续查看。</p></div>
    <template v-else>
      <el-tag :type="runtimeType(detail.outcome)" size="small">{{ runtimeLabel(detail.outcome) }}</el-tag>
      <h3 class="detail-topic">{{ detail.topic || runtimeLabel(detail.outcome) }}</h3>
      <p class="detail-reason">{{ detail.reason || detail.detail || '这次评估未记录详细理由。' }}</p>
      <section v-if="reply" class="detail-section"><h4>{{ decisionSent(detail.outcome) ? '回复内容' : '生成内容（未确认发送）' }}</h4><p class="detail-reply">{{ reply }}</p></section>
      <section class="detail-section"><h4>这次评估</h4><dl class="detail-facts"><div><dt>群聊</dt><dd>{{ detail.groupName || detail.groupId }}</dd></div><div><dt>评估时间</dt><dd>{{ chatDateTime(detail.evaluatedAt) }}</dd></div><div><dt>观察窗口</dt><dd>{{ detail.messageCount }} 条消息</dd></div><div><dt>结果</dt><dd>{{ runtimeLabel(detail.outcome) }}</dd></div></dl></section>
      <section class="detail-section"><h4>本次使用</h4><dl class="detail-facts"><div><dt>群记忆</dt><dd>{{ detail.memoryCount == null ? '未记录' : `${detail.memoryCount} 条` }}<template v-if="detail.memoryPromptChars != null"> · 注入 {{ detail.memoryPromptChars }} 字</template></dd></div><div><dt>附图</dt><dd>{{ detail.imageCount == null ? '未记录' : `${detail.imageCount} 张` }}</dd></div><div><dt>联网核实</dt><dd>{{ detail.searchUsed == null ? '未记录' : detail.searchUsed ? '已发起联网改写' : '未发起' }}</dd></div><div v-if="detail.searchQuery"><dt>检索词</dt><dd>{{ detail.searchQuery }}</dd></div></dl></section>
      <details class="detail-disclosure"><summary>请求与用量<el-icon aria-hidden="true"><ArrowRight /></el-icon></summary><dl class="detail-facts"><div><dt>判定模型</dt><dd>{{ detail.model || '未记录' }}</dd></div><div><dt>判定输入 / 输出</dt><dd>{{ tokens(detail.promptTokens) }} / {{ tokens(detail.completionTokens) }}</dd></div><div><dt>改写输入 / 输出</dt><dd>{{ tokens(detail.speechPromptTokens) }} / {{ tokens(detail.speechCompletionTokens) }}</dd></div></dl></details>
      <details v-if="detail.window" class="detail-disclosure"><summary>当时的观察窗口<el-icon aria-hidden="true"><ArrowRight /></el-icon></summary><p v-if="detail.window.omittedMessages" class="snapshot-note">另有 {{ detail.window.omittedMessages }} 条未保存在快照中。</p><article v-for="(message, index) in detail.window.messages" :key="index" class="snapshot-message"><div><b>{{ message.senderName || '未记录发送者' }}</b><time>{{ chatTime(message.utcAt) }}</time></div><p>{{ message.text }}</p><small v-if="message.hasImage">含图片</small><small v-if="message.truncated">正文较长，快照仅保存前 2000 字</small></article></details>
      <p v-else class="snapshot-note">这条历史留痕未保存观察窗口，无法还原当时完整上下文。</p>
    </template>
    <section v-if="entry?.kind === 'message' && !detail && !loading" class="detail-section"><h4>聊天正文</h4><p class="detail-reply">{{ entry.plainText || (entry.hasImage ? '[图片]' : '[空消息]') }}</p></section>
    <template #footer><el-button @click="visible = false">返回会话</el-button></template>
  </el-drawer>
</template>
<script setup>
import { computed, onUnmounted, ref, watch } from 'vue'
import { ArrowRight } from '@element-plus/icons-vue'
import api from '../api'
import { LatestRequest } from '../latestRequest'
import { useMediaQuery } from '../useMediaQuery'
import { runtimeLabel, runtimeType } from '../runtimeLabels'
import { chatDateTime, chatTime, decisionSent } from '../chatHistory'
const props = defineProps({ modelValue: Boolean, entry: { type: Object, default: null } })
const emit = defineEmits(['update:modelValue'])
const visible = computed({ get: () => props.modelValue, set: value => emit('update:modelValue', value) })
const narrow = useMediaQuery('(max-width: 600px)')
const detail = ref(null), error = ref(''), loading = ref(false)
const request = new LatestRequest()
const reply = computed(() => props.entry?.kind === 'message' ? props.entry.plainText : detail.value?.line)
const tokens = value => value == null ? '未记录' : `${value.toLocaleString('zh-CN')} Token`
const load = async () => {
  request.cancel()
  detail.value = null; error.value = ''
  if (!props.entry?.decisionId) { loading.value = false; return }
  loading.value = true
  try { await request.run(signal => api.get(`/chat/timeline/decisions/${props.entry.decisionId}`, { signal, skipErrorMessage: true }), ({ data }) => { detail.value = data }) }
  catch (err) { error.value = err.response?.status === 404 ? '这条决策留痕已不存在' : '暂时无法读取决策详情' }
  finally { if (!request.pending) loading.value = false }
}
watch([() => props.modelValue, () => props.entry], ([open]) => { if (open) load(); else request.cancel() })
onUnmounted(() => request.cancel())
</script>
<style scoped>
.detail-loading, .detail-empty { padding: 20px 0; color: var(--panel-text-muted); font-size: 14px; }
.detail-empty h3 { margin: 14px 0 8px; font-size: 16px; color: var(--panel-text); font-weight: 600; }
.detail-empty p { margin: 5px 0; line-height: 1.8; }
.detail-empty .el-button { margin-top: 12px; }
.detail-brand { display: block; width: 34px; height: 28px; background: var(--panel-accent-text); mask: url('/logo-mark.svg') center / contain no-repeat; }
.detail-topic { margin: 14px 0 9px; color: var(--panel-text); font-size: 18px; line-height: 1.55; font-weight: 600; overflow-wrap: anywhere; }
.detail-reason { margin: 0; font-size: 14px; line-height: 1.85; color: var(--panel-text-regular); white-space: pre-wrap; overflow-wrap: anywhere; }
.detail-section { margin-top: 24px; }
.detail-section h4 { margin: 0 0 9px; color: var(--panel-text-muted); font-size: 12px; font-weight: 500; }
.detail-reply { margin: 0; padding: 12px 14px; border-radius: 9px; color: var(--panel-text); background: var(--panel-accent-soft); font-size: 14px; line-height: 1.85; white-space: pre-wrap; overflow-wrap: anywhere; }
.detail-facts { margin: 0; }
.detail-facts > div { display: flex; align-items: baseline; justify-content: space-between; gap: 14px; padding: 8px 0; }
.detail-facts dt { flex: none; color: var(--panel-text-muted); font-size: 12px; }
.detail-facts dd { margin: 0; text-align: right; color: var(--panel-text-regular); font-size: 13px; overflow-wrap: anywhere; font-variant-numeric: tabular-nums; }
.detail-disclosure { margin-top: 20px; padding-top: 16px; border-top: 1px solid var(--panel-border-soft); }
.detail-disclosure summary { display: flex; align-items: center; justify-content: space-between; color: var(--panel-text-regular); font-size: 13px; list-style: none; cursor: pointer; }
.detail-disclosure summary::-webkit-details-marker { display: none; }
.detail-disclosure[open] summary .el-icon { transform: rotate(90deg); }
.detail-disclosure dl { margin-top: 8px; }
.snapshot-note { margin: 18px 0 0; color: var(--panel-text-muted); font-size: 12px; line-height: 1.8; }
.snapshot-message { margin-top: 18px; }
.snapshot-message > div { display: flex; flex-wrap: wrap; gap: 10px; font-size: 12px; color: var(--panel-text-muted); }
.snapshot-message b { font-weight: 500; color: var(--panel-text-regular); overflow-wrap: anywhere; }
.snapshot-message p { margin: 5px 0 0; white-space: pre-wrap; overflow-wrap: anywhere; font-size: 14px; line-height: 1.8; }
.snapshot-message small { display: block; margin-top: 4px; font-size: 12px; color: var(--panel-text-muted); }
@media (pointer: coarse) { summary { min-height: 44px; } }
</style>
