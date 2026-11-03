const labels = {
  queued: '等待处理', started: '开始处理', running: '处理中', completed: '处理结束',
  context: '整理上下文', images: '准备图片', generate: '生成回复', judge: '判断是否发言',
  search: '联网检索', probe: '连接与搜索探测', rewrite: '整理最终回复', delivery_policy: '检查发送条件', sending: '发送到 QQ',
  sent: '发送成功', pending: '等待回执', failed: '处理失败', unknown: '发送结果未确认',
  skipped: '已跳过', expired: '排队超时', timeout: '处理超时', cancelled: '已取消', success: '请求成功',
  http_error: '服务商拒绝请求', network_error: '连接服务商失败', invalid_response: '返回格式异常',
  skipped_ai_declined: '决定不发言', skipped_disabled: '已关闭', skipped_quiet: '处于免打扰时段',
  skipped_cooldown: '仍在冷却', skipped_group_cap: '达到本群日限', skipped_global_cap: '达到全局日限',
  failed_ai_call: '模型调用失败', skipped_ai_unparsable: '判定格式异常', skipped_ai_truncated: '判定输出被截断',
  skipped_ai_unavailable: 'AI 尚未配置', skipped_ai_busy: '模型调用繁忙', skipped_after_search: '检索后决定不发言',
  skipped_duplicate: '近期已发过相同内容', sent_passive: '已回复消息', sent_active: '已主动发送',
  failed_rejected: '接收方拒收', failed_rate_limited: '触发发送频控', failed_no_channel: '没有可用发送方式',
  delivery_unknown: '发送结果未确认', failed_api_error: '平台发送失败'
}
export const runtimeLabel = (state) => labels[state] || '其他结果'
export const runtimeType = (state = '') => {
  if (['sent', 'success', 'sent_active', 'sent_passive'].includes(state)) return 'success'
  if (state.includes('fail') || ['http_error', 'network_error', 'invalid_response'].includes(state)) return 'danger'
  if (['unknown', 'timeout', 'expired', 'delivery_unknown'].includes(state)) return 'warning'
  return 'info'
}
export const formatTime = (value) => {
  if (!value) return '-'
  return new Date(/[zZ]$|[+-]\d\d:?\d\d$/.test(value) ? value : value + 'Z').toLocaleString('zh-CN', { hour12: false })
}
