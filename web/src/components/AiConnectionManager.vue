<template>
  <el-dialog v-model="visible" title="管理服务商" width="1040px" top="5vh" :style="{ maxWidth: 'calc(100vw - 24px)' }"
    append-to-body :before-close="beforeClose" class="connection-manager">
    <div class="connection-layout">
      <aside class="connection-sidebar">
        <el-button type="primary" plain :disabled="busy" @click="createConnection">添加服务商</el-button>
        <el-input v-model="search" placeholder="搜索连接" clearable aria-label="搜索连接" />
        <div class="connection-list">
          <button v-for="item in filteredConnections" :key="item.id" type="button" class="connection-row"
            :class="{ selected: draft.id === item.id }" :disabled="busy" @click="editConnection(item)">
            <strong>{{ item.name }}</strong>
            <span>{{ item.models.length }} 个模型{{ item.isCurrent ? ' · 正在使用' : '' }}{{ item.presetCount ? ` · ${item.presetCount} 个预设` : '' }}</span>
          </button>
          <p v-if="!filteredConnections.length" class="connection-hint">{{ connections.length ? '没有匹配的连接' : '还没有服务商连接' }}</p>
        </div>
      </aside>
      <section class="connection-editor">
        <div class="connection-editor-heading">
          <div><h3>{{ draft.id ? '编辑连接' : '添加服务商' }}</h3><p>保存一次，在 AI 配置与预设中重复选用。</p></div>
          <el-tag v-if="dirty" type="warning" size="small">有未保存修改</el-tag>
        </div>
        <el-alert v-if="error" :title="error" type="error" :closable="false" show-icon />
        <el-form label-position="top" :disabled="busy" class="connection-form">
          <el-form-item label="服务商模板" for="connection-template">
            <el-select id="connection-template" v-model="draft.templateId" :disabled="!!draft.id" filterable @change="applyTemplate">
              <el-option-group v-for="group in templateGroups" :key="group.id" :label="group.label">
                <el-option v-for="template in group.templates" :key="template.id" :value="template.id" :label="template.name" />
              </el-option-group>
            </el-select>
            <p v-if="selectedTemplate?.description" class="connection-hint">{{ selectedTemplate.description }}</p>
            <div v-if="selectedTemplate && selectedTemplate.id !== 'custom'" class="connection-template-links">
              <a v-if="safeHttpUrl(selectedTemplate.documentationUrl)" :href="safeHttpUrl(selectedTemplate.documentationUrl)" target="_blank" rel="noopener noreferrer">官方模型文档</a>
              <a v-if="safeHttpUrl(selectedTemplate.keyHelpUrl)" :href="safeHttpUrl(selectedTemplate.keyHelpUrl)" target="_blank" rel="noopener noreferrer">获取 API Key</a>
              <small v-if="selectedTemplate.modelsUpdatedAt">内置目录更新于 {{ selectedTemplate.modelsUpdatedAt }}</small>
            </div>
          </el-form-item>
          <div class="connection-two-columns">
            <el-form-item label="连接名称" for="connection-name" required>
              <el-input id="connection-name" v-model="draft.name" maxlength="64" placeholder="例如：公司网关、百炼 · 备用账号" />
            </el-form-item>
            <el-form-item label="接口协议" for="connection-protocol" required>
              <el-select id="connection-protocol" v-model="draft.protocol" :disabled="protocolLocked">
                <el-option value="openai" label="OpenAI 兼容（Chat Completions）" />
                <el-option value="gemini" label="Gemini 原生（generateContent）" />
              </el-select>
              <p v-if="protocolLocked" class="connection-hint">协议由所选服务商模板决定，切换模板即可更换。</p>
            </el-form-item>
          </div>
          <el-form-item label="API 地址" for="connection-url" required>
            <el-input id="connection-url" v-model="draft.apiBaseUrl" placeholder="https://api.example.com/v1" />
            <p class="connection-hint">{{ draft.protocol === 'gemini' ? '填写原生接口根地址，例如 https://generativelanguage.googleapis.com/v1beta。' : '填写接口根地址，例如 https://api.example.com/v1，程序会追加 /chat/completions。' }}</p>
          </el-form-item>
          <el-form-item label="API Key" for="connection-key">
            <el-input id="connection-key" v-model="draft.apiKey" type="password" show-password autocomplete="new-password"
              placeholder="本地无认证接口可留空" />
            <p class="connection-hint">{{ draft.id ? '保持打码值保留密钥；清空后保存会移除密钥。更新后引用该连接的预设会使用新密钥。' : '密钥保存在当前服务端，页面只回显打码值。' }}</p>
          </el-form-item>
          <div class="connection-model-heading">
            <h4>模型目录 <small>{{ draft.models.length }}</small></h4>
            <el-input v-model="modelSearch" placeholder="搜索模型 ID 或名称" clearable aria-label="搜索模型" class="connection-model-search" />
            <div class="connection-model-actions">
              <el-button v-if="draft.id && selectedTemplate?.models?.length" @click="mergeBuiltinModels">补充最新内置模型</el-button>
              <el-button :loading="discovering" @click="discover">获取模型列表</el-button>
            </div>
          </div>
          <p class="connection-hint">获取列表后与已有模型合并；接口不提供列表时，可手动添加准确的模型 ID。</p>
          <div class="connection-add-model">
            <el-input v-model="modelEntry" placeholder="输入模型 ID，按 Enter 添加" aria-label="新增模型 ID" @keyup.enter="addModel" />
            <el-button @click="addModel">添加模型</el-button>
          </div>
          <el-table v-if="draft.models.length" :data="pagedModels" max-height="260" size="small" class="connection-models"
            empty-text="没有匹配的模型">
            <el-table-column prop="id" label="模型 ID" min-width="160" show-overflow-tooltip />
            <el-table-column label="显示名称" min-width="150">
              <template #default="{ row }"><el-input v-model="row.name" :aria-label="`模型 ${row.id} 的显示名称`" /></template>
            </el-table-column>
            <el-table-column label="来源" width="64"><template #default="{ row }">{{ modelSource(row) }}</template></el-table-column>
            <el-table-column width="60"><template #default="{ row }"><el-button link type="danger" :aria-label="`移除模型 ${row.id}`" @click="removeModel(row.id)">移除</el-button></template></el-table-column>
          </el-table>
          <el-empty v-else description="添加至少一个模型后即可保存连接" :image-size="44" />
          <el-pagination v-if="filteredModels.length > MODEL_PAGE_SIZE" v-model:current-page="modelPage"
            :page-size="MODEL_PAGE_SIZE" :total="filteredModels.length" layout="prev, pager, next"
            small background class="connection-model-pager" />
          <el-form-item label="默认模型" for="connection-default-model" required>
            <el-select id="connection-default-model" v-model="draft.defaultModel" filterable placeholder="选择默认模型">
              <el-option v-for="model in draft.models" :key="model.id" :value="model.id" :label="model.name || model.id" />
            </el-select>
          </el-form-item>
          <el-collapse v-model="advanced">
            <el-collapse-item title="高级连接选项" name="advanced">
              <el-form-item label="连接超时（秒）" for="connection-timeout">
                <el-input-number id="connection-timeout" v-model="draft.timeoutSeconds" :min="5" :max="120" :value-on-clear="60" />
              </el-form-item>
              <div class="connection-model-heading"><h4>自定义请求头</h4><el-button link type="primary" @click="headers.push({ name: '', value: '' })">添加请求头</el-button></div>
              <p class="connection-hint">同名请求头会覆盖内置 Authorization / x-goog-api-key。</p>
              <div v-for="(header, index) in headers" :key="index" class="connection-header-row">
                <el-input v-model="header.name" placeholder="Header 名称" :aria-label="`请求头 ${index + 1} 名称`" />
                <el-input v-model="header.value" type="password" show-password placeholder="Header 内容" :aria-label="`请求头 ${index + 1} 内容`" />
                <el-button link type="danger" @click="headers.splice(index, 1)">移除</el-button>
              </div>
            </el-collapse-item>
          </el-collapse>
        </el-form>
        <el-alert v-if="testResult" :title="testResult" type="success" :closable="false" show-icon />
      </section>
    </div>
    <template #footer>
      <div class="connection-footer">
        <div>
          <el-button v-if="draft.id" :disabled="busy" @click="duplicate">复制连接</el-button>
          <el-button v-if="draft.id" type="danger" plain :disabled="busy" @click="removeConnection">删除连接</el-button>
        </div>
        <div>
          <el-button :disabled="busy || !draft.models.length" :loading="testing" @click="testConnection">测试默认模型</el-button>
          <el-button :disabled="busy" @click="beforeClose(() => visible = false)">关闭</el-button>
          <el-button type="primary" :loading="saving" :disabled="busy || !draft.models.length" @click="save">保存连接</el-button>
        </div>
      </div>
    </template>
  </el-dialog>
</template>

<script setup>
import { computed, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import api from '../api'
import {
  SECRET_MASK, appendConnectionModel, clearMaskedSecretsForOriginChange, connectionHeaderError,
  filterConnectionModels, groupProviderTemplates, mergeConnectionModelsWithinLimit
} from '../aiConnections'
import { safeHttpUrl } from '../aiSettings'

const props = defineProps({ modelValue: Boolean, connections: { type: Array, default: () => [] }, templates: { type: Array, default: () => [] } })
const emit = defineEmits(['update:modelValue', 'update:dirty', 'saved', 'removed'])
const visible = computed({ get: () => props.modelValue, set: value => emit('update:modelValue', value) })
const blank = () => ({ id: null, name: '', templateId: 'custom', protocol: 'openai', apiBaseUrl: '', apiKey: '', timeoutSeconds: 60, defaultModel: '', models: [] })
const draft = ref(blank()), headers = ref([]), search = ref(''), modelEntry = ref(''), modelSearch = ref(''), advanced = ref([])
const saving = ref(false), testing = ref(false), discovering = ref(false), error = ref(''), testResult = ref(''), baseline = ref('')
const busy = computed(() => saving.value || testing.value || discovering.value)
const payload = () => ({ ...draft.value, headers: Object.fromEntries(headers.value.filter(h => h.name || h.value).map(h => [h.name.trim(), h.value])) })
const fingerprint = () => JSON.stringify({ connection: payload(), modelEntry: modelEntry.value })
const dirty = computed(() => !!baseline.value && fingerprint() !== baseline.value)
const filteredConnections = computed(() => props.connections.filter(c => c.name.toLowerCase().includes(search.value.trim().toLowerCase())))
const templateGroups = computed(() => groupProviderTemplates(props.templates))
const selectedTemplate = computed(() => props.templates.find(template => template.id === draft.value.templateId))
// 新建时协议由模板决定：内置模板的协议就是请求体契约，选错只会在后端被拒绝。
// 编辑已有连接时不锁，允许把历史错配的连接改成模板要求的协议。
const protocolLocked = computed(() => !draft.value.id && !!selectedTemplate.value && selectedTemplate.value.id !== 'custom')
const filteredModels = computed(() => filterConnectionModels(draft.value.models, modelSearch.value))
// 大目录（几千个模型）一次渲染会卡顿：每页 100 条，搜索或增删后回到第一页
const MODEL_PAGE_SIZE = 100
const modelPage = ref(1)
const pagedModels = computed(() => filteredModels.value.slice(
  (modelPage.value - 1) * MODEL_PAGE_SIZE, modelPage.value * MODEL_PAGE_SIZE))
watch([modelSearch, () => filteredModels.value.length], () => { modelPage.value = 1 })
// 内置目录 / 手动添加 / 接口获取三态：内置模型标记 isManual:false 且 isBuiltin:true；
// 保存后 isBuiltin 不落库，按当前模板的模型目录回推，避免把内置模型显示成“获取”。
const modelSource = row => row.isManual ? '手动' : (row.isBuiltin || selectedTemplate.value?.models?.includes(row.id)) ? '内置' : '获取'
watch(() => fingerprint(), () => { testResult.value = '' })
watch(dirty, value => emit('update:dirty', value), { immediate: true })
const originOf = value => {
  try {
    const url = new URL(String(value ?? '').trim())
    return url.protocol && url.host ? `${url.protocol}//${url.host}` : ''
  } catch { return '' }
}
// 编辑已有连接时记住它保存的 origin。换成别的 origin 后打码值一定不会被后端解析，
// 与其等保存/测试返回 400，不如在草稿里清掉并提示重填；换回原地址则恢复掩码。
const baselineOrigin = ref(''), originClearedKey = ref(false)
let originClearedHeaders = []
watch(() => draft.value.apiBaseUrl, value => {
  if (!draft.value.id || !baselineOrigin.value) return
  const origin = originOf(value)
  if (!origin) return // 正在输入，URL 还不完整
  if (origin === baselineOrigin.value) {
    if (originClearedKey.value && draft.value.apiKey === '') draft.value.apiKey = SECRET_MASK
    originClearedKey.value = false
    for (const cleared of originClearedHeaders) {
      const header = headers.value.find(item => item.name === cleared.name)
      if (header && header.value === '') header.value = SECRET_MASK
    }
    originClearedHeaders = []
    return
  }
  const hadKey = draft.value.apiKey === SECRET_MASK
  const hadHeaders = headers.value.filter(header => header.value === SECRET_MASK).map(header => ({ name: header.name }))
  if (!clearMaskedSecretsForOriginChange(draft.value, headers.value)) return
  if (hadKey) originClearedKey.value = true
  originClearedHeaders.push(...hadHeaders)
  ElMessage.warning('接口地址已更换，请重新填写 API Key 与自定义请求头')
})
const reset = connection => {
  const copy = connection ? JSON.parse(JSON.stringify(connection)) : blank()
  headers.value = Object.entries(copy.headers || {}).map(([name, value]) => ({ name, value, originalName: name }))
  delete copy.headers
  // 先记下目标 origin 再切 draft：watcher 看到同 origin，不会把刚恢复的密钥当成“换地址”清掉。
  baselineOrigin.value = originOf(copy.apiBaseUrl)
  originClearedKey.value = false
  originClearedHeaders = []
  draft.value = copy
  modelEntry.value = ''; modelSearch.value = ''; error.value = ''; testResult.value = ''; advanced.value = []
  baseline.value = fingerprint()
}
const canLeave = async () => {
  if (busy.value) return false
  if (!dirty.value) return true
  try { await ElMessageBox.confirm('连接有未保存修改，是否放弃？', '未保存的连接', { confirmButtonText: '放弃修改', cancelButtonText: '继续编辑', type: 'warning' }); return true }
  catch { return false }
}
const beforeClose = async done => {
  if (!(await canLeave())) return
  // 用户已确认放弃草稿：清掉脏标记，避免页面级的离开保护继续拦截路由/关闭标签页
  baseline.value = fingerprint()
  done()
}
const createConnection = async () => { if (await canLeave()) reset() }
const editConnection = async connection => { if (await canLeave()) reset(connection) }
const open = (connection = null) => { reset(connection || props.connections[0]); search.value = ''; visible.value = true }
const openCreate = () => { reset(); search.value = ''; visible.value = true }
defineExpose({ open, openCreate, dirty })
const applyTemplate = id => {
  const template = props.templates.find(item => item.id === id)
  if (!template) return
  draft.value.name = template.id === 'custom' ? '' : template.name
  draft.value.apiBaseUrl = template.apiBaseUrl || ''
  draft.value.models = (template.models || []).map(id => ({ id, name: id, isManual: false, isBuiltin: true }))
  draft.value.defaultModel = template.defaultModel || ''
  draft.value.protocol = template.protocol || 'openai'
  draft.value.apiKey = ''
  headers.value = []
}
const mergeBuiltinModels = () => {
  const before = draft.value.models.length
  const incoming = (selectedTemplate.value?.models || []).map(id => ({ id, name: id, isManual: false, isBuiltin: true }))
  const { models, exceeded } = mergeConnectionModelsWithinLimit(draft.value.models, incoming)
  if (exceeded) { ElMessage.error('最多保存 2000 个模型'); return }
  draft.value.models = models
  const added = draft.value.models.length - before
  ElMessage.info(added ? `已补充 ${added} 个模型，保存连接后生效；原有模型与默认选择已保留` : '内置模型已全部包含在当前目录中')
}
const addModel = () => {
  const id = modelEntry.value.trim()
  if (!id) return
  if (draft.value.models.some(model => model.id === id)) { ElMessage.info('该模型已在列表中'); return }
  const result = appendConnectionModel(draft.value.models, { id, name: id, isManual: true })
  if (!result.added) { ElMessage.error('最多保存 2000 个模型'); return }
  draft.value.models = result.models
  if (!draft.value.defaultModel) draft.value.defaultModel = id
  modelEntry.value = ''
}
const removeModel = id => {
  draft.value.models = draft.value.models.filter(model => model.id !== id)
  if (draft.value.defaultModel === id) draft.value.defaultModel = draft.value.models[0]?.id || ''
}
const validate = () => {
  error.value = ''
  const headerError = connectionHeaderError(headers.value)
  if (headerError) error.value = headerError
  if (!draft.value.name.trim()) error.value = '请填写连接名称'
  if (!draft.value.apiBaseUrl.trim()) error.value = '请填写 API 地址'
  if (!Number.isInteger(draft.value.timeoutSeconds) || draft.value.timeoutSeconds < 5 || draft.value.timeoutSeconds > 120) {
    error.value = '请填写 5～120 之间的连接超时（秒）'
  }
  return !error.value
}
// 删除/测试/获取模型失败时错误只显示在可滚动编辑区顶部，footer 操作后可能看不到；
// 这些操作用 ElMessage 额外提示，顶部 alert 仍保留完整信息。
const apiError = (failure, notify = false) => {
  const message = failure.response?.data?.error || failure.message || '请求失败，当前草稿已保留'
  error.value = message
  if (notify) ElMessage.error(message)
}
const discover = async () => {
  if (!validate()) return
  discovering.value = true
  try {
    const { data } = await api.post('/config/ai/connections/discover', payload(), { skipErrorMessage: true, timeout: 35000 })
    const { models, exceeded } = mergeConnectionModelsWithinLimit(draft.value.models, data)
    if (exceeded) { ElMessage.error('最多保存 2000 个模型'); return }
    draft.value.models = models
    if (!draft.value.defaultModel) draft.value.defaultModel = draft.value.models[0]?.id || ''
    ElMessage.success(`已获取 ${data.length} 个模型，保存连接后可选用`)
  } catch (failure) { apiError(failure, true) }
  finally { discovering.value = false }
}
const testConnection = async () => {
  if (!validate()) return
  testing.value = true; testResult.value = ''
  try {
    const { data } = await api.post('/config/ai/connections/test', payload(), { skipErrorMessage: true, timeout: 35000 })
    testResult.value = `${data.message} · ${data.model} · ${data.elapsedMs} ms`
  } catch (failure) { apiError(failure, true) }
  finally { testing.value = false }
}
const save = async () => {
  if (!validate()) return
  saving.value = true
  try {
    const { data } = draft.value.id
      ? await api.put(`/config/ai/connections/${draft.value.id}`, payload(), { skipErrorMessage: true })
      : await api.post('/config/ai/connections', payload(), { skipErrorMessage: true })
    reset(data); emit('saved', data); ElMessage.success('连接已保存')
  } catch (failure) { apiError(failure, true) }
  finally { saving.value = false }
}
const duplicate = async () => {
  if (!(await canLeave())) return
  saving.value = true
  try {
    const { data } = await api.post(`/config/ai/connections/${draft.value.id}/duplicate`, {}, { skipErrorMessage: true })
    reset(data); emit('saved', data); ElMessage.success('已复制连接，可独立修改')
  } catch (failure) { apiError(failure, true) }
  finally { saving.value = false }
}
const removeConnection = async () => {
  try { await ElMessageBox.confirm(`删除连接「${draft.value.name}」？正在使用或被预设引用的连接需要先解除引用。`, '删除连接', { type: 'warning', confirmButtonText: '删除', cancelButtonText: '取消' }) }
  catch { return }
  saving.value = true
  try {
    const id = draft.value.id
    await api.delete(`/config/ai/connections/${id}`, { skipErrorMessage: true })
    reset(); emit('removed', id); ElMessage.success('连接已删除')
  } catch (failure) { apiError(failure, true) }
  finally { saving.value = false }
}
</script>

<style scoped>
:global(.connection-manager .el-dialog__body) { max-height: calc(90dvh - 140px); overflow: auto; }
.connection-layout { display: grid; grid-template-columns: 210px minmax(0, 1fr); gap: 24px; }
.connection-sidebar { display: flex; flex-direction: column; gap: 12px; min-width: 0; border-right: 1px solid var(--el-border-color-lighter); padding-right: 20px; }
.connection-list { max-height: 520px; overflow: auto; }
.connection-row { display: flex; flex-direction: column; gap: 5px; width: 100%; padding: 12px; margin-bottom: 6px; text-align: left; border: 1px solid transparent; border-radius: 8px; background: transparent; color: var(--el-text-color-primary); cursor: pointer; overflow-wrap: anywhere; }
.connection-row span { font-size: 11px; color: var(--el-text-color-secondary); }
.connection-row:hover, .connection-row.selected { background: var(--el-color-primary-light-9); border-color: var(--el-color-primary-light-5); }
.connection-editor { min-width: 0; max-height: 65vh; overflow: auto; padding-right: 6px; }
.connection-editor-heading, .connection-model-heading { display: flex; justify-content: space-between; align-items: center; gap: 10px; }
.connection-editor-heading { margin-bottom: 16px; }
.connection-editor h3, .connection-editor h4 { margin: 0; }
.connection-editor-heading p, .connection-hint { margin: 6px 0; font-size: 12px; color: var(--el-text-color-secondary); line-height: 1.6; }
.connection-model-heading small { color: var(--el-text-color-secondary); font-weight: 400; }
.connection-model-heading { flex-wrap: wrap; }
.connection-model-search { width: 220px; max-width: 100%; }
.connection-model-actions, .connection-template-links { display: flex; flex-wrap: wrap; gap: 8px; }
.connection-model-actions :deep(.el-button + .el-button) { margin-left: 0; }
.connection-template-links { margin-top: 5px; font-size: 12px; line-height: 1.6; }
.connection-template-links a { color: var(--el-color-primary); text-decoration: none; }
.connection-template-links small { color: var(--el-text-color-secondary); }
.connection-form { margin-top: 14px; }
.connection-form :deep(.el-select) { width: 100%; }
.connection-two-columns { display: grid; grid-template-columns: 1fr 1fr; gap: 14px; }
.connection-add-model, .connection-header-row { display: flex; gap: 8px; margin: 10px 0; }
.connection-header-row > .el-input { min-width: 0; }
.connection-models { margin: 12px 0 18px; }
.connection-model-pager { display: flex; justify-content: flex-end; margin: -10px 0 14px; }
.connection-footer, .connection-footer > div { display: flex; flex-wrap: wrap; gap: 8px; }
.connection-footer { justify-content: space-between; }
.connection-footer :deep(.el-button + .el-button) { margin-left: 0; }
@media (max-width: 700px) {
  .connection-layout { grid-template-columns: minmax(0, 1fr); gap: 14px; }
  .connection-sidebar { border-right: 0; padding-right: 0; }
  .connection-list { max-height: 110px; }
  .connection-row { padding: 8px; margin-bottom: 2px; }
  .connection-editor { max-height: 52vh; }
  .connection-two-columns { grid-template-columns: minmax(0, 1fr); gap: 0; }
  .connection-footer { justify-content: flex-end; }
  .connection-footer > div { justify-content: flex-end; }
}
</style>
