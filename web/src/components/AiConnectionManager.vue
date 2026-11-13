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
            <el-select id="connection-template" v-model="draft.templateId" :disabled="!!draft.id" @change="applyTemplate">
              <el-option v-for="template in templates" :key="template.id" :value="template.id" :label="template.name" />
            </el-select>
          </el-form-item>
          <div class="connection-two-columns">
            <el-form-item label="连接名称" for="connection-name" required>
              <el-input id="connection-name" v-model="draft.name" maxlength="64" placeholder="例如：公司网关、百炼 · 备用账号" />
            </el-form-item>
            <el-form-item label="接口协议" for="connection-protocol" required>
              <el-select id="connection-protocol" v-model="draft.protocol">
                <el-option value="openai" label="OpenAI 兼容（Chat Completions）" />
                <el-option value="gemini" label="Gemini 原生（generateContent）" />
              </el-select>
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
            <el-button :loading="discovering" @click="discover">获取模型列表</el-button>
          </div>
          <p class="connection-hint">获取列表后与已有模型合并；接口不提供列表时，可手动添加准确的模型 ID。</p>
          <div class="connection-add-model">
            <el-input v-model="modelEntry" placeholder="输入模型 ID，按 Enter 添加" aria-label="新增模型 ID" @keyup.enter="addModel" />
            <el-button @click="addModel">添加模型</el-button>
          </div>
          <el-table v-if="draft.models.length" :data="draft.models" max-height="260" size="small" class="connection-models">
            <el-table-column prop="id" label="模型 ID" min-width="160" show-overflow-tooltip />
            <el-table-column label="显示名称" min-width="150">
              <template #default="{ row }"><el-input v-model="row.name" :aria-label="`模型 ${row.id} 的显示名称`" /></template>
            </el-table-column>
            <el-table-column label="来源" width="64"><template #default="{ row }">{{ row.isManual ? '手动' : '获取' }}</template></el-table-column>
            <el-table-column width="60"><template #default="{ row }"><el-button link type="danger" :aria-label="`移除模型 ${row.id}`" @click="removeModel(row.id)">移除</el-button></template></el-table-column>
          </el-table>
          <el-empty v-else description="添加至少一个模型后即可保存连接" :image-size="44" />
          <el-form-item label="默认模型" for="connection-default-model" required>
            <el-select id="connection-default-model" v-model="draft.defaultModel" filterable placeholder="选择默认模型">
              <el-option v-for="model in draft.models" :key="model.id" :value="model.id" :label="model.name || model.id" />
            </el-select>
          </el-form-item>
          <el-collapse v-model="advanced">
            <el-collapse-item title="高级连接选项" name="advanced">
              <el-form-item label="连接超时（秒）" for="connection-timeout">
                <el-input-number id="connection-timeout" v-model="draft.timeoutSeconds" :min="5" :max="120" />
              </el-form-item>
              <div class="connection-model-heading"><h4>自定义请求头</h4><el-button link type="primary" @click="headers.push({ name: '', value: '' })">添加请求头</el-button></div>
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
import { mergeConnectionModels } from '../aiConnections'

const props = defineProps({ modelValue: Boolean, connections: { type: Array, default: () => [] }, templates: { type: Array, default: () => [] } })
const emit = defineEmits(['update:modelValue', 'saved', 'removed'])
const visible = computed({ get: () => props.modelValue, set: value => emit('update:modelValue', value) })
const blank = () => ({ id: null, name: '', templateId: 'custom', protocol: 'openai', apiBaseUrl: '', apiKey: '', timeoutSeconds: 60, defaultModel: '', models: [] })
const draft = ref(blank()), headers = ref([]), search = ref(''), modelEntry = ref(''), advanced = ref([])
const saving = ref(false), testing = ref(false), discovering = ref(false), error = ref(''), testResult = ref(''), baseline = ref('')
const busy = computed(() => saving.value || testing.value || discovering.value)
const payload = () => ({ ...draft.value, headers: Object.fromEntries(headers.value.filter(h => h.name || h.value).map(h => [h.name.trim(), h.value])) })
const fingerprint = () => JSON.stringify({ connection: payload(), modelEntry: modelEntry.value })
const dirty = computed(() => !!baseline.value && fingerprint() !== baseline.value)
const filteredConnections = computed(() => props.connections.filter(c => c.name.toLowerCase().includes(search.value.trim().toLowerCase())))
watch(() => fingerprint(), () => { testResult.value = '' })
const reset = connection => {
  const copy = connection ? JSON.parse(JSON.stringify(connection)) : blank()
  headers.value = Object.entries(copy.headers || {}).map(([name, value]) => ({ name, value }))
  delete copy.headers
  draft.value = copy
  modelEntry.value = ''; error.value = ''; testResult.value = ''; advanced.value = []
  baseline.value = fingerprint()
}
const canLeave = async () => {
  if (busy.value) return false
  if (!dirty.value) return true
  try { await ElMessageBox.confirm('连接有未保存修改，是否放弃？', '未保存的连接', { confirmButtonText: '放弃修改', cancelButtonText: '继续编辑', type: 'warning' }); return true }
  catch { return false }
}
const beforeClose = async done => { if (await canLeave()) done() }
const createConnection = async () => { if (await canLeave()) reset() }
const editConnection = async connection => { if (await canLeave()) reset(connection) }
const open = (connection = null) => { reset(connection || props.connections[0]); visible.value = true }
const openCreate = () => { reset(); visible.value = true }
defineExpose({ open, openCreate })
const applyTemplate = id => {
  const template = props.templates.find(item => item.id === id)
  if (!template) return
  draft.value.name = template.id === 'custom' ? '' : template.name
  draft.value.apiBaseUrl = template.apiBaseUrl || ''
  draft.value.models = (template.models || []).map(id => ({ id, name: id, isManual: true }))
  draft.value.defaultModel = template.defaultModel || ''
  draft.value.protocol = 'openai'
}
const addModel = () => {
  const id = modelEntry.value.trim()
  if (!id) return
  if (draft.value.models.some(model => model.id === id)) { ElMessage.info('该模型已在列表中'); return }
  draft.value.models.push({ id, name: id, isManual: true })
  if (!draft.value.defaultModel) draft.value.defaultModel = id
  modelEntry.value = ''
}
const removeModel = id => {
  draft.value.models = draft.value.models.filter(model => model.id !== id)
  if (draft.value.defaultModel === id) draft.value.defaultModel = draft.value.models[0]?.id || ''
}
const validate = () => {
  error.value = ''
  const names = headers.value.filter(h => h.name || h.value).map(h => h.name.trim().toLowerCase())
  if (new Set(names).size !== names.length) error.value = '自定义请求头名称不能重复'
  if (!draft.value.name.trim()) error.value = '请填写连接名称'
  if (!draft.value.apiBaseUrl.trim()) error.value = '请填写 API 地址'
  return !error.value
}
const apiError = failure => { error.value = failure.response?.data?.error || failure.message || '请求失败，当前草稿已保留' }
const discover = async () => {
  if (!validate()) return
  discovering.value = true
  try {
    const { data } = await api.post('/config/ai/connections/discover', payload(), { skipErrorMessage: true, timeout: 35000 })
    draft.value.models = mergeConnectionModels(draft.value.models, data)
    if (!draft.value.defaultModel) draft.value.defaultModel = draft.value.models[0]?.id || ''
    ElMessage.success(`已获取 ${data.length} 个模型，保存连接后可选用`)
  } catch (failure) { apiError(failure) }
  finally { discovering.value = false }
}
const testConnection = async () => {
  if (!validate()) return
  testing.value = true; testResult.value = ''
  try {
    const { data } = await api.post('/config/ai/connections/test', payload(), { skipErrorMessage: true, timeout: 35000 })
    testResult.value = `${data.message} · ${data.model} · ${data.elapsedMs} ms`
  } catch (failure) { apiError(failure) }
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
  } catch (failure) { apiError(failure) }
  finally { saving.value = false }
}
const duplicate = async () => {
  if (!(await canLeave())) return
  saving.value = true
  try {
    const { data } = await api.post(`/config/ai/connections/${draft.value.id}/duplicate`, {}, { skipErrorMessage: true })
    reset(data); emit('saved', data); ElMessage.success('已复制连接，可独立修改')
  } catch (failure) { apiError(failure) }
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
  } catch (failure) { apiError(failure) }
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
.connection-form { margin-top: 14px; }
.connection-form :deep(.el-select) { width: 100%; }
.connection-two-columns { display: grid; grid-template-columns: 1fr 1fr; gap: 14px; }
.connection-add-model, .connection-header-row { display: flex; gap: 8px; margin: 10px 0; }
.connection-header-row > .el-input { min-width: 0; }
.connection-models { margin: 12px 0 18px; }
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
