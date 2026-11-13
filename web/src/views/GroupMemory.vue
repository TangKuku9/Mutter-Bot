<template>
  <div class="memory-page" v-loading="loading">
    <el-card shadow="never">
      <div class="memory-header">
        <el-select :model-value="groupId" placeholder="选择一个群" filterable aria-label="选择群记忆的群" @change="changeGroup">
          <el-option v-for="group in groups" :key="group.groupOpenId" :value="group.groupOpenId" :label="group.name || group.groupOpenId" />
        </el-select>
        <el-button :disabled="!groupId || saving" @click="refresh">刷新</el-button>
      </div>
      <el-empty v-if="!groupId" description="选择已有群后配置记忆学习" />
      <template v-else-if="loaded">
        <el-alert title="首次开启只学习开启后的新消息；旧生产环境的历史记录不会自动触发总结。" type="info" :closable="false" show-icon />
        <el-tabs v-model="tab">
          <el-tab-pane label="学习设置" name="settings">
            <el-form label-position="top" @submit.prevent="save">
              <div class="memory-heading"><h3>何时学习</h3><el-switch v-model="config.enabled" active-text="自动学习" aria-label="自动学习" /></div>
              <p class="memory-hint">三种条件可以同时开启，任一满足即触发；同时命中只执行一次。没有新增记录时不调用模型。</p>
              <div class="memory-rules">
                <section class="memory-rule">
                  <el-checkbox v-model="config.countEnabled" aria-label="按新增记录">按新增记录</el-checkbox>
                  <div class="memory-inline"><span>累计新增</span><el-input-number v-model="config.count" :min="1" :max="1000" :disabled="!config.countEnabled" aria-label="新增记录触发条数" /><span>条</span></div>
                </section>
                <section class="memory-rule">
                  <el-checkbox v-model="config.intervalEnabled" aria-label="按时间间隔">按时间间隔</el-checkbox>
                  <div class="memory-inline"><span>距离上次完成</span><el-input-number v-model="config.intervalMinutes" :min="1" :max="10080" :disabled="!config.intervalEnabled" aria-label="学习间隔分钟数" /><span>分钟</span></div>
                </section>
                <section class="memory-rule">
                  <el-checkbox v-model="config.scheduleEnabled" aria-label="按定时任务">按定时任务</el-checkbox>
                  <div v-for="(schedule, index) in config.schedules" :key="index" class="memory-inline memory-schedule">
                    <el-select v-model="schedule.frequency" :disabled="!config.scheduleEnabled" :aria-label="`定时频率 ${index + 1}`">
                      <el-option label="每天" value="daily" /><el-option label="工作日" value="workdays" /><el-option label="每周" value="weekly" />
                    </el-select>
                    <el-select v-if="schedule.frequency === 'weekly'" v-model="schedule.weekday" :disabled="!config.scheduleEnabled" :aria-label="`每周日期 ${index + 1}`">
                      <el-option v-for="(day, dayIndex) in weekdays" :key="dayIndex" :value="dayIndex" :label="day" />
                    </el-select>
                    <input v-model="schedule.time" type="time" class="memory-time" :disabled="!config.scheduleEnabled" :aria-label="`定时时刻 ${index + 1}`" />
                    <el-button link type="danger" :disabled="config.schedules.length === 1 || !config.scheduleEnabled" :aria-label="`移除定时 ${index + 1}`" @click="config.schedules.splice(index, 1)">移除</el-button>
                  </div>
                  <el-button link type="primary" :disabled="!config.scheduleEnabled || config.schedules.length >= 4" @click="config.schedules.push({ frequency: 'daily', time: '09:00', weekday: 1 })">添加时间</el-button>
                  <p class="memory-hint">北京时间；最多 4 个时刻，重复时刻会合并。空闲时跳过，不补跑过去的定时任务。</p>
                </section>
              </div>
              <el-divider />
              <h3>由谁总结记忆</h3>
              <p class="memory-hint">独立于日常聊天，复用 AI 设置中的服务商连接。未选择时不会使用聊天模型代替。</p>
              <ProviderModelPicker v-model:connection-id="config.providerConnectionId" v-model:model-name="config.modelName" :connections="connections" id-prefix="memory" />
              <el-form-item label="模型 ID（也可直接填写）"><el-input v-model="config.modelName" maxlength="256" aria-label="手动填写记忆模型 ID" placeholder="选择目录中的模型，或填写连接支持的模型 ID" /></el-form-item>
              <div class="memory-save"><span class="memory-hint">{{ dirty ? '有未保存修改' : '设置已同步' }}</span><el-button type="primary" :loading="saving" native-type="submit" @click.prevent="save">保存设置</el-button></div>
            </el-form>
          </el-tab-pane>
          <el-tab-pane :label="`记忆条目 ${items.length}`" name="items">
            <div class="memory-heading"><h3>自动记忆 <small>{{ items.length }} / 128 条</small></h3><el-popconfirm title="清空后只学习后续新消息，确定清空自动记忆？" @confirm="clear"><template #reference><el-button type="danger" plain :disabled="busy">清空自动记忆</el-button></template></el-popconfirm></div>
            <p class="memory-hint">管理员原有摘要继续在群管理中单独维护。删除的条目不会从旧聊天历史重新回灌。</p>
            <el-empty v-if="!items.length" description="还没有自动记忆，开启后会从新消息中逐步学习" />
            <article v-for="item in items" :key="item.id" class="memory-item">
              <div class="memory-heading"><p>{{ item.content }}</p><el-button link type="danger" :disabled="busy" :aria-label="`删除记忆 ${item.id}`" @click="remove(item.id)">删除</el-button></div>
              <div class="memory-tags"><el-tag v-for="tag in item.tags" :key="tag" size="small" type="info">{{ tag }}</el-tag></div>
              <p class="memory-hint">最近确认 {{ fmt(item.lastSeenAt) }} · 到期 {{ fmt(item.expiresAt) }}</p>
              <el-button link type="primary" @click="showSources(item.id)">查看来源</el-button>
            </article>
          </el-tab-pane>
          <el-tab-pane :label="`运行记录 ${runs.length}`" name="runs">
            <el-empty v-if="!runs.length" description="暂无学习运行记录" />
            <article v-for="(run, index) in runs" :key="index" class="memory-item">
              <div class="memory-heading"><b>{{ run.reasons.join(' ＋ ') }}</b><el-tag :type="run.outcome === 'success' ? 'success' : 'warning'">{{ outcome(run.outcome) }}</el-tag></div>
              <p class="memory-hint">{{ fmt(run.at) }} · 处理 {{ run.processed }} 条 · 新增 {{ run.added }} / 更新 {{ run.updated }} / 删除 {{ run.removed }}</p>
              <p class="memory-hint">总结模型 {{ run.model }}</p>
            </article>
          </el-tab-pane>
        </el-tabs>
        <div class="memory-status" aria-live="polite">
          <span>{{ outcome(status.lastOutcome) }}</span><span>待处理 {{ status.pending }}{{ status.pendingCapped ? '+' : '' }} 条</span><span>今日学习 {{ status.callsToday }} / 12 次</span>
          <span v-if="!status.groupEnabled">本群响应未启用，学习暂停</span>
        </div>
      </template>
      <el-result v-else-if="!loading && groupId" icon="warning" title="记忆配置读取失败" sub-title="重新读取成功后才能保存" />
    </el-card>
    <el-dialog v-model="sourcesOpen" title="记忆来源" width="min(560px, 94vw)">
      <el-empty v-if="!sources.length" description="来源消息已不可用" />
      <article v-for="source in sources" :key="source.id" class="memory-item"><b>{{ source.userName }}</b><p>{{ source.plainText }}</p><small>{{ fmt(source.createdAt) }}</small></article>
    </el-dialog>
  </div>
</template>

<script setup>
import { computed, onMounted, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import api from '../api'
import ProviderModelPicker from '../components/ProviderModelPicker.vue'
import { useUnsavedChanges } from '../useUnsavedChanges'

const route = useRoute(), router = useRouter()
const groups = ref([]), connections = ref([]), groupId = ref(''), config = ref(null)
const items = ref([]), runs = ref([]), status = ref({}), revision = ref(0), original = ref('')
const loading = ref(false), loaded = ref(false), saving = ref(false), busy = ref(false), tab = ref('settings')
const sources = ref([]), sourcesOpen = ref(false)
const weekdays = ['周日', '周一', '周二', '周三', '周四', '周五', '周六']
const dirty = computed(() => loaded.value && JSON.stringify(config.value) !== original.value)
const confirmDiscard = useUnsavedChanges(dirty)
const endpoint = () => `/groups/${encodeURIComponent(groupId.value)}/memory-auto`
const fmt = value => value ? new Date(value).toLocaleString('zh-CN', { hour12: false, timeZone: 'Asia/Shanghai' }) : '—'
const outcome = value => ({ success: '学习完成', baseline_set: '已建立新消息起点', boundary_reset: '学习范围已更新', not_enabled: '尚未开启', running: '正在学习', interrupted: '上次学习未完成，等待下一触发', model_failed: '模型调用失败', invalid_json: '输出格式无效', timeout: '总结超时', truncated: '输出被截断', input_too_large: '总结输入超出模型窗口', connection_missing: '总结连接不可用', skipped_after_failures: '连续失败，本批已跳过', skipped_noise_or_old: '已跳过无效或过期记录' }[value] || '等待新消息')
function apply(data, preserve = false) {
  revision.value = data.revision; items.value = data.items; runs.value = data.runs; status.value = data.status
  original.value = JSON.stringify(data.config)
  if (!preserve) config.value = data.config
  loaded.value = true
}
async function load(preserve = false) {
  const { data } = await api.get(endpoint())
  apply(data, preserve)
}
async function changeGroup(id) {
  if (id === groupId.value || !await confirmDiscard()) return
  groupId.value = id; loaded.value = false; loading.value = true
  try { await load(); await router.replace({ query: { group: id } }) } finally { loading.value = false }
}
async function refresh() {
  if (!await confirmDiscard()) return
  loading.value = true
  try { await load() } finally { loading.value = false }
}
async function save() {
  if (saving.value || !loaded.value) return
  saving.value = true
  try {
    const { data } = await api.put(endpoint(), { config: config.value, revision: revision.value })
    apply(data); ElMessage.success('群记忆设置已保存')
  } finally { saving.value = false }
}
async function clear() {
  busy.value = true
  try { await api.delete(endpoint()); await load(true); ElMessage.success('自动记忆已清空，后续只学习新消息') } finally { busy.value = false }
}
async function remove(id) {
  busy.value = true
  try { await api.delete(`${endpoint()}/${id}`); await load(true) } finally { busy.value = false }
}
async function showSources(id) {
  const { data } = await api.get(`${endpoint()}/${id}/sources`)
  sources.value = data; sourcesOpen.value = true
}
onMounted(async () => {
  loading.value = true
  try {
    const results = await Promise.allSettled([api.get('/groups'), api.get('/config/ai/connections')])
    if (results[0].status !== 'fulfilled') return
    groups.value = results[0].value.data
    if (results[1].status === 'fulfilled') connections.value = results[1].value.data
    const requested = typeof route.query.group === 'string' ? route.query.group : ''
    groupId.value = groups.value.some(g => g.groupOpenId === requested) ? requested : groups.value[0]?.groupOpenId || ''
    if (groupId.value) await load()
  } finally { loading.value = false }
})
</script>

<style scoped>
.memory-header, .memory-heading, .memory-inline, .memory-save, .memory-status { display: flex; align-items: center; gap: 12px; flex-wrap: wrap; }
.memory-header { margin-bottom: 18px; }.memory-header .el-select { width: min(360px, 100%); }
.memory-heading, .memory-save { justify-content: space-between; }.memory-heading h3 { margin: 8px 0; font-weight: 500; }
.memory-hint { color: var(--el-text-color-secondary); font-size: 12px; line-height: 1.7; margin: 8px 0 14px; }
.memory-rules { display: grid; gap: 12px; }.memory-rule { border: 1px solid var(--el-border-color-lighter); border-radius: 8px; padding: 14px; }
.memory-inline { margin-top: 6px; }.memory-schedule .el-select { width: 120px; }
.memory-time { background: var(--el-fill-color-blank); color: var(--el-text-color-primary); border: 1px solid var(--el-border-color); border-radius: 4px; padding: 7px 10px; font: inherit; max-width: 100%; }
.memory-item { padding: 16px 0; border-bottom: 1px solid var(--el-border-color-lighter); overflow-wrap: anywhere; }.memory-item p { margin: 6px 0; white-space: pre-wrap; }
.memory-tags { display: flex; flex-wrap: wrap; gap: 6px; margin-top: 8px; }.memory-status { border-top: 1px solid var(--el-border-color-lighter); padding-top: 16px; color: var(--el-text-color-secondary); font-size: 12px; }
.memory-save { margin-top: 18px; }h3 small { color: var(--el-text-color-secondary); font-weight: 400; font-size: 12px; }
@media (max-width: 520px) { .memory-inline .el-input-number { width: 115px; }.memory-header .el-select { width: 100%; } }
</style>
