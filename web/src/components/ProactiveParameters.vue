<template>
  <el-card shadow="never" class="proactive-parameters">
    <div class="parameters-heading">
      <div><h2>参数与预设</h2><p>让每个群，都有适合自己的接话节奏。</p></div>
      <div class="parameter-actions">
        <el-button :icon="Collection" :disabled="presetsLoading" @click="libraryOpen = true">预设库</el-button>
        <el-button :icon="FolderAdd" :disabled="!ready || busy || presetsLoading || !!invalidField" @click="openSavePreset">存为预设</el-button>
      </div>
    </div>
    <el-alert v-if="groupsError" type="warning" :closable="false" show-icon class="parameter-alert">
      <template #title>群列表暂时无法读取。<el-button link type="primary" @click="loadGroups">重试</el-button></template>
    </el-alert>
    <div class="scope-panel">
      <div class="scope-picker"><label for="proactive-scope">配置范围</label>
        <el-select id="proactive-scope" v-model="scope" filterable :disabled="savingGroup || savingGlobal" :loading="groupsLoading" aria-label="配置范围">
          <el-option value="" label="全局默认 · 所有群的基础参数" />
          <el-option-group label="群级设置"><el-option v-for="group in groups" :key="group.groupOpenId" :value="group.groupOpenId"
            :label="`${group.name || group.groupOpenId}${groupHasDraft(group.groupOpenId) ? ' · 有草稿' : ''}`" /></el-option-group>
        </el-select>
      </div>
      <div class="scope-state">
        <label class="participation"><span>{{ isGlobal ? '全局择机发言' : '本群参与择机发言' }}</span><el-switch
          :model-value="isGlobal ? globalConfig.enabled : selectedGroup?.draft.enabled || false" :disabled="!ready || busy"
          :aria-label="isGlobal ? '全局择机发言' : '本群参与择机发言'" @change="changeEnabled" /></label>
        <div class="scope-source"><el-tag size="small" :type="!isGlobal && customCount ? 'primary' : 'info'" effect="light">{{ isGlobal ? '全局默认参数' : customCount ? `${customCount} 项本群自定义` : '全部跟随全局' }}</el-tag><span>{{ isGlobal ? '影响仍在跟随全局的群' : '其余参数自动跟随全局' }}</span></div>
      </div>
    </div>
    <el-alert v-if="groupError" title="本群参数暂时无法读取，请重试。" type="warning" :closable="false" show-icon class="parameter-alert"><el-button link type="primary" @click="loadSelectedGroup(true)">重试读取</el-button></el-alert>
    <el-alert v-else-if="!isGlobal && selectedGroup && (!savedGlobal?.enabled || !selectedGroup.isEnabled || !selectedGroup.draft.enabled)"
      :title="!savedGlobal?.enabled ? '全局择机发言已关闭，保存本群参数后仍需开启总开关。' : !selectedGroup.isEnabled ? '本群响应已停用，需先在群管理中启用响应。' : '本群当前不参与择机发言，参数仍可编辑并保存。'"
      type="info" :closable="false" show-icon class="parameter-alert" />
    <p v-if="!isGlobal && dirtyGlobal" class="global-draft-note">全局默认有未保存修改，本群暂按已保存的默认值展示。</p>
    <div class="preset-toolbar">
      <label for="proactive-preset">参数预设</label><el-select id="proactive-preset" v-model="presetId" :loading="presetsLoading" :disabled="presetsLoading || !presets.length" aria-label="选择参数预设">
        <el-option v-for="preset in presets" :key="preset.id" :value="preset.id" :label="`${preset.name}${preset.isBuiltIn ? '' : ' · 我的预设'}`" />
      </el-select>
      <el-button :disabled="!ready || busy || !chosenPreset" @click="openApply(chosenPreset)">预览并应用<el-icon class="action-arrow"><ArrowRight /></el-icon></el-button>
      <el-button v-if="presetsError" link type="primary" @click="loadPresets">重新读取预设</el-button>
    </div>
    <div class="effective-summary" aria-live="polite"><el-icon><MagicStick /></el-icon><span>{{ summary }}</span></div>
    <div v-loading="groupLoading" class="parameter-sections">
      <section v-for="section in mainSections" :key="section.title" class="parameter-section">
        <div class="section-heading"><el-icon><component :is="section.icon" /></el-icon><div><h3>{{ section.title }}</h3><p>{{ section.note }}</p></div></div>
        <ProactiveParameterField v-for="field in section.fields" :key="field.key" :field="field" :model-value="parameterValue(effective, field.key)"
          :global="isGlobal" :inherited="!hasOverride(selectedGroup?.draft.overrides, field.key)" :disabled="!ready || busy"
          @source="changeSource(field, $event)" @update:model-value="changeValue(field, $event)" />
      </section>
    </div>
    <el-collapse v-model="advancedOpen" class="parameter-advanced"><el-collapse-item name="advanced">
      <template #title><span>更多判定与通道设置</span><small>{{ fields.length - 6 }} 项</small></template>
      <div class="advanced-fields"><ProactiveParameterField v-for="field in fields.slice(6)" :key="field.key" :field="field" :model-value="parameterValue(effective, field.key)"
        :global="isGlobal" :inherited="!hasOverride(selectedGroup?.draft.overrides, field.key)" :disabled="!ready || busy"
        @source="changeSource(field, $event)" @update:model-value="changeValue(field, $event)" /></div>
      <p v-if="!isGlobal && selectedGroup?.memoryAllowed === false" class="global-draft-note">群管理已禁止本群使用记忆，此处开启也不会注入。</p>
    </el-collapse-item></el-collapse>
    <div class="global-policy">
      <template v-if="isGlobal"><div class="policy-control"><label for="proactive-global-daily">全部群每日上限</label><el-input-number :key="String(busy || !ready)" id="proactive-global-daily" :model-value="globalConfig.maxGlobalPerDay" :min="1" :max="500" :precision="0" :controls="false" :disabled="busy || !ready" @change="changeGlobalLimit('maxGlobalPerDay', $event)" /><span>次</span></div>
        <div class="policy-control"><label for="proactive-account-minute">账号每分钟上限</label><el-input-number :key="String(busy || !ready)" id="proactive-account-minute" :model-value="globalConfig.activePerAccountPerMinute" :min="1" :max="savedGlobal?.ceilingAccountPerMinute || 60" :precision="0" :controls="false" :disabled="busy || !ready" @change="changeGlobalLimit('activePerAccountPerMinute', $event)" /><span>条</span></div></template>
      <template v-else><span><b>全局约束</b>　全部群每日 {{ savedGlobal?.maxGlobalPerDay ?? '—' }} 次 · 账号每分钟 {{ savedGlobal?.activePerAccountPerMinute ?? '—' }} 条</span><el-button link type="primary" :disabled="busy" @click="scope = ''">查看全局默认<el-icon class="action-arrow"><TopRight /></el-icon></el-button></template>
    </div>
    <p v-if="invalidField" class="parameter-validation" role="alert">请填写有效的{{ invalidField }}后再保存。</p>
    <footer class="parameter-footer"><div class="save-status" aria-live="polite"><span :class="{ dirty: currentDirty }"></span>{{ !ready ? '等待配置读取' : currentDirty ? '当前范围有修改未保存' : '当前设置已保存' }}<small v-if="otherDraftCount">另有 {{ otherDraftCount }} 个群保留草稿</small></div>
      <div class="parameter-actions"><el-button v-if="currentDirty" link :disabled="busy" @click="discardCurrent">撤销修改</el-button><el-button v-if="!isGlobal" :disabled="!ready || busy || !customCount" @click="resetOpen = true">全部跟随全局</el-button>
        <el-button type="primary" :loading="isGlobal ? savingGlobal : savingGroup" :disabled="!ready || busy || !currentDirty || !!invalidField" @click="saveCurrent">{{ isGlobal ? '保存全局默认' : '保存本群设置' }}</el-button></div>
    </footer>
  </el-card>

  <el-dialog v-model="libraryOpen" title="参数预设" width="600px" class="proactive-preset-dialog" :style="dialogStyle" append-to-body>
    <p class="preset-context">挑选一组参数，应用到「{{ scopeName }}」的草稿。</p>
    <el-alert v-if="presetsError" type="warning" title="预设暂时无法读取" :closable="false"><el-button @click="loadPresets">重试</el-button></el-alert>
    <section v-for="builtIn in [true, false]" :key="String(builtIn)" class="preset-group"><h4>{{ builtIn ? '内置预设' : '我的预设' }}</h4>
      <p v-if="presetsError" class="preset-context">{{ builtIn ? '内置预设暂时无法读取。' : '预设暂时无法读取。' }}</p>
      <p v-else-if="presetsLoading" class="preset-context">正在读取…</p>
      <p v-else-if="!presets.some(p => p.isBuiltIn === builtIn)" class="preset-context">还没有自定义预设。</p>
      <div v-for="preset in presetsError ? [] : presets.filter(p => p.isBuiltIn === builtIn)" :key="preset.id" class="preset-row"><div><b>{{ preset.name }}</b><p>{{ preset.description }}</p><small>{{ overrideCount(preset.values) }} 项参数<template v-if="preset.values.cooldownMinutes != null"> · 间隔 {{ preset.values.cooldownMinutes }} 分钟</template><template v-if="preset.values.maxPerGroupPerDay != null"> · 每日 {{ preset.values.maxPerGroupPerDay }} 次</template></small></div>
        <div class="parameter-actions"><el-popconfirm v-if="!preset.isBuiltIn" title="删除这个预设？已应用的群设置不会改变。" width="260" @confirm="deletePreset(preset)"><template #reference><el-button link type="danger" :disabled="presetsLoading || !!deletingPreset" :loading="deletingPreset === preset.id">删除</el-button></template></el-popconfirm><el-button :disabled="!ready || busy" @click="libraryOpen = false; openApply(preset)">查看并应用</el-button></div>
      </div>
    </section>
    <template #footer><el-button type="primary" :disabled="!ready || busy || presetsLoading || !!invalidField" @click="libraryOpen = false; openSavePreset()">把当前参数存为预设</el-button></template>
  </el-dialog>
  <el-dialog v-model="savePresetOpen" title="存为预设" width="560px" :style="dialogStyle" append-to-body :close-on-click-modal="false" :before-close="closeSavePreset">
    <p class="preset-context">从「{{ presetSourceName }}」提取一组可复用参数。</p>
    <el-form label-position="top" :model="presetForm" class="preset-save-form" :disabled="savingPreset">
      <el-form-item label="预设名称"><el-input v-model="presetForm.name" maxlength="24" show-word-limit placeholder="例如：晚间闲聊" /></el-form-item>
      <el-form-item label="简短说明（选填）"><el-input v-model="presetForm.description" maxlength="120" placeholder="这组参数适合什么场景" /></el-form-item>
      <el-form-item label="保存范围"><el-radio-group v-model="presetForm.mode" class="preset-mode"><el-radio value="all">全部可分群参数（{{ fields.length }} 项）</el-radio><el-radio value="custom" :disabled="!presetCustomCount">仅本群自定义项（{{ presetCustomCount }} 项）</el-radio></el-radio-group></el-form-item>
    </el-form><p class="preset-context">预设包含参数，不包含参与开关和账号总量限制。</p>
    <template #footer><el-button :disabled="savingPreset" @click="savePresetOpen = false">取消</el-button><el-button type="primary" :loading="savingPreset" @click="createPreset">保存预设</el-button></template>
  </el-dialog>
  <el-dialog v-model="applyOpen" :title="`应用「${applyingPreset?.name || ''}」`" width="600px" :style="dialogStyle" append-to-body>
    <p class="preset-context">应用到「{{ scopeName }}」 · {{ applyRows.length }} 项参数</p>
    <el-table :data="applyRows" class="preset-diff"><el-table-column prop="label" label="参数" min-width="120" /><el-table-column label="当前" min-width="140"><template #default="{ row }">{{ row.before }}<small v-if="!isGlobal">{{ row.inherited ? '跟随全局' : '本群自定义' }}</small></template></el-table-column><el-table-column label="应用后" min-width="140"><template #default="{ row }"><span class="preset-next">{{ row.after }}</span><small v-if="!isGlobal">本群自定义</small></template></el-table-column></el-table>
    <p class="preset-context apply-note">先载入草稿，保存设置后生效。未包含的参数保持当前设置。预设以后修改或删除，不会连带改动已保存的群。</p>
    <template #footer><el-button @click="applyOpen = false">取消</el-button><el-button type="primary" :disabled="busy" @click="applyPreset">应用到当前草稿</el-button></template>
  </el-dialog>
  <el-dialog v-model="resetOpen" title="恢复跟随全局" width="440px" :style="dialogStyle" append-to-body><p class="preset-context">将「{{ scopeName }}」的 {{ customCount }} 项自定义参数恢复为跟随全局。保存本群设置后生效。</p><template #footer><el-button @click="resetOpen = false">取消</el-button><el-button type="primary" @click="resetOverrides">载入全局默认</el-button></template></el-dialog>
</template>
<script setup>
import { ref, reactive, computed, watch, onMounted, onUnmounted } from 'vue'
import { ElMessage } from 'element-plus'
import { Collection, FolderAdd, ArrowRight, TopRight, MagicStick, ChatDotRound, Clock } from '@element-plus/icons-vue'
import api from '../api'
import { LatestRequest } from '../latestRequest'
import { proactiveFields, copy, parameterValue, hasOverride, overrideCount, onlyParameters, changeParameter, applyParameterPreset, effectiveParameters, draftSignature, parameterText, defaultProactiveConfig, invalidParameterField, sortPresets } from '../proactiveParameters'
import ProactiveParameterField from './ProactiveParameterField.vue'

const props = defineProps({ globalConfig: { type: Object, required: true }, savedGlobal: Object, savingGlobal: Boolean, refreshing: Boolean, dirtyGlobal: Boolean })
const emit = defineEmits(['update:globalConfig', 'change-global', 'save-global', 'reset-global', 'dirty-groups', 'groups-changed'])
const scope = ref(''), groups = ref([]), groupStates = reactive({}), groupsLoading = ref(false), groupsError = ref(false)
const groupLoading = ref(false), groupError = ref(false), savingGroup = ref(false)
const presets = ref([]), presetId = ref('daily'), presetsLoading = ref(false), presetsError = ref(false), deletingPreset = ref(null)
const libraryOpen = ref(false), savePresetOpen = ref(false), applyOpen = ref(false), resetOpen = ref(false), advancedOpen = ref([])
const applyingPreset = ref(null), savingPreset = ref(false), presetForm = reactive({ name: '', description: '', mode: 'all' })
const presetSourceName = ref(''), presetCustomCount = ref(0)
let presetAllValues = {}, presetCustomValues = {}, refreshTask = null, disposed = false
// 预设读取的代次：创建/删除成功后自增，让仍在途的旧 GET 响应作废，不覆盖本地最新列表。
let presetLoadToken = 0
// 当前在途的群配置请求；refresh 结束前等它，避免刷新中切群时父页 refreshing 提前复位。
let groupLoadTask = null
const groupRequest = new LatestRequest(), listRequest = new LatestRequest(), presetRequest = new LatestRequest(), writes = new AbortController()
watch(scope, () => loadSelectedGroup(), { flush: 'sync' })
const dialogStyle = { maxWidth: 'calc(100vw - 32px)' }
const isGlobal = computed(() => !scope.value)
const selectedGroup = computed(() => groupStates[scope.value])
const scopeName = computed(() => isGlobal.value ? '全局默认' : groups.value.find(g => g.groupOpenId === scope.value)?.name || scope.value)
const effective = computed(() => isGlobal.value ? props.globalConfig : effectiveParameters(props.savedGlobal || defaultProactiveConfig(), selectedGroup.value?.draft.overrides))
const ready = computed(() => !!props.savedGlobal && (isGlobal.value || (!!selectedGroup.value && !groupError.value)))
const busy = computed(() => props.savingGlobal || props.refreshing || savingGroup.value || groupLoading.value)
const fields = computed(() => proactiveFields.map(field => ({ ...field, max: field.key === 'maxAttachedImages' ? props.savedGlobal?.ceilingAttachedImages || field.max : field.key === 'activePerGroupPerMinute' ? props.savedGlobal?.ceilingGroupPerMinute || field.max : field.key === 'activePerGroupPerDay' ? props.savedGlobal?.ceilingGroupPerDay || field.max : field.max })))
const mainSections = computed(() => [
  { title: '接话节奏', note: '把参与感和打扰程度调到合适的位置。', icon: ChatDotRound, fields: fields.value.slice(0, 3) },
  { title: '观察与时段', note: '在合适的时间，读懂正在聊的话题。', icon: Clock, fields: fields.value.slice(3, 6) }
])
const groupHasDraft = id => groupStates[id] && draftSignature(groupStates[id].draft) !== draftSignature(groupStates[id].original)
const dirtyGroups = computed(() => Object.keys(groupStates).filter(groupHasDraft))
watch(() => dirtyGroups.value.length > 0, value => emit('dirty-groups', value), { immediate: true, flush: 'sync' })
const otherDraftCount = computed(() => dirtyGroups.value.filter(id => id !== scope.value).length)
const currentDirty = computed(() => isGlobal.value ? props.dirtyGlobal : !!groupHasDraft(scope.value))
const customCount = computed(() => overrideCount(selectedGroup.value?.draft.overrides))
const summary = computed(() => `${parameterText(fields.value[0], effective.value.liveliness)}倾向 · 至少间隔 ${effective.value.cooldownMinutes} 分钟 · 每日最多 ${effective.value.maxPerGroupPerDay} 次 · ${parameterText(fields.value[5], parameterValue(effective.value, 'quietHours'))}`)
const chosenPreset = computed(() => presets.value.find(p => p.id === presetId.value))
const invalidField = computed(() => invalidParameterField(effective.value, isGlobal.value, fields.value))
const applyRows = computed(() => fields.value.filter(field => hasOverride(applyingPreset.value?.values, field.key)).map(field => ({
  label: field.label, before: parameterText(field, parameterValue(effective.value, field.key)),
  after: parameterText(field, parameterValue(applyingPreset.value.values, field.key)), inherited: !hasOverride(selectedGroup.value?.draft.overrides, field.key)
})))

function updateGlobal(value) { emit('update:globalConfig', value); emit('change-global') }
function changeEnabled(value) { if (isGlobal.value) updateGlobal({ ...props.globalConfig, enabled: value }); else selectedGroup.value.draft.enabled = value }
function changeGlobalLimit(key, value) { updateGlobal({ ...props.globalConfig, [key]: value }) }
function changeValue(field, value) {
  if (isGlobal.value) { updateGlobal(changeParameter(props.globalConfig, field.key, value)); return }
  const hadOverride = hasOverride(selectedGroup.value.draft.overrides, field.key)
  selectedGroup.value.draft.overrides = changeParameter(selectedGroup.value.draft.overrides, field.key, value)
  // 清空群级自定义数值 = 恢复跟随全局：显式提示，避免“草稿静默变回继承”被误读为已保存自定义。
  if (value == null && hadOverride) ElMessage.info(`「${field.label}」已恢复为跟随全局`)
}
function changeSource(field, source) {
  selectedGroup.value.draft.overrides = changeParameter(selectedGroup.value.draft.overrides, field.key, parameterValue(effective.value, field.key), source === 'inherit')
}
function discardCurrent() { if (isGlobal.value) emit('reset-global'); else selectedGroup.value.draft = copy(selectedGroup.value.original) }
function resetOverrides() { selectedGroup.value.draft.overrides = {}; resetOpen.value = false }

async function loadGroups() {
  groupsLoading.value = true
  try {
    await listRequest.run(signal => api.get('/groups', { signal }), ({ data }) => {
      groups.value = Array.isArray(data) ? data : []; groupsError.value = false
      // 群列表整体替换后，草稿只保留仍存在的群；当前群被移除时回到全局默认，避免草稿搁浅。
      const ids = new Set(groups.value.map(g => g.groupOpenId))
      for (const id of Object.keys(groupStates)) if (!ids.has(id)) delete groupStates[id]
      if (scope.value && !ids.has(scope.value)) scope.value = ''
    })
  }
  catch { if (!disposed) groupsError.value = true }
  finally { if (!listRequest.pending) groupsLoading.value = false }
}
async function loadSelectedGroup(force = false) {
  groupRequest.cancel()
  groupLoading.value = false
  groupError.value = false
  if (!scope.value) return
  if (selectedGroup.value && (!force || groupHasDraft(scope.value))) return
  const id = scope.value
  groupLoading.value = true
  const task = (async () => {
    try {
      await groupRequest.run(signal => api.get(`/proactive/groups/${encodeURIComponent(id)}/config`, { signal }), ({ data }) => {
        const draft = { enabled: !!data.enabled, overrides: onlyParameters(data.overrides || {}) }
        groupStates[id] = { name: data.name, isEnabled: data.isEnabled, memoryAllowed: data.memoryAllowed, original: copy(draft), draft }
      })
    } catch { if (!disposed && scope.value === id) groupError.value = true }
    finally { if (!groupRequest.pending) groupLoading.value = false }
  })()
  groupLoadTask = task
  try { await task } finally { if (groupLoadTask === task) groupLoadTask = null }
}
async function loadPresets() {
  const token = ++presetLoadToken
  presetsLoading.value = true
  try {
    await presetRequest.run(signal => api.get('/proactive/presets', { signal }), ({ data }) => {
      if (token !== presetLoadToken) return // 创建/删除后的本地列表更新，旧的读取响应作废
      presets.value = sortPresets(Array.isArray(data) ? data : []); presetsError.value = false
      if (!presets.value.some(p => p.id === presetId.value)) presetId.value = presets.value[0]?.id || ''
    })
  } catch { if (!disposed && token === presetLoadToken) presetsError.value = true }
  // 复位只看请求是否在途：创建/删除成功会推进 token，但不能让 presetsLoading 卡住。
  finally { if (!presetRequest.pending) presetsLoading.value = false }
}
async function refresh() {
  if (refreshTask) return refreshTask
  refreshTask = (async () => {
    await Promise.allSettled([loadGroups(), loadPresets(), loadSelectedGroup(true)])
    // 刷新中用户切群会另起群请求：等它结束再放开父页 refreshing，spinner 不提前结束。
    await groupLoadTask?.catch(() => {})
  })()
  try { await refreshTask } finally { refreshTask = null }
}
async function saveCurrent() {
  if (!ready.value || busy.value || invalidField.value) return
  if (isGlobal.value) { emit('save-global'); return }
  savingGroup.value = true
  const id = scope.value
  try {
    const { data } = await api.put(`/proactive/groups/${encodeURIComponent(id)}/config`, copy(selectedGroup.value.draft), { signal: writes.signal })
    if (disposed) return
    const draft = { enabled: !!data.enabled, overrides: onlyParameters(data.overrides || {}) }
    groupStates[id] = { name: data.name, isEnabled: data.isEnabled, memoryAllowed: data.memoryAllowed, original: copy(draft), draft }
    ElMessage.success('本群择机参数已保存'); emit('groups-changed')
  } catch { /* API 层提示失败，保留当前草稿供重试。 */ }
  finally { savingGroup.value = false }
}
function openApply(preset) { applyingPreset.value = copy(preset); applyOpen.value = true }
function applyPreset() {
  if (isGlobal.value) updateGlobal(applyParameterPreset(props.globalConfig, applyingPreset.value.values))
  else selectedGroup.value.draft.overrides = applyParameterPreset(selectedGroup.value.draft.overrides, applyingPreset.value.values)
  presetId.value = applyingPreset.value.id; applyOpen.value = false
  ElMessage.success('已载入当前草稿，保存设置后生效')
}
function openSavePreset() {
  presetSourceName.value = scopeName.value; presetAllValues = onlyParameters(copy(effective.value))
  presetCustomValues = isGlobal.value ? {} : onlyParameters(copy(selectedGroup.value.draft.overrides)); presetCustomCount.value = overrideCount(presetCustomValues)
  Object.assign(presetForm, { name: '', description: '', mode: 'all' }); savePresetOpen.value = true
}
function closeSavePreset(done) { if (!savingPreset.value) done() }
async function createPreset() {
  const name = presetForm.name.trim()
  if (!name) { ElMessage.warning('请填写预设名称'); return }
  if (savingPreset.value || presetsLoading.value) return
  savingPreset.value = true
  try {
    const { data } = await api.post('/proactive/presets', { name, description: presetForm.description.trim(), values: copy(presetForm.mode === 'custom' ? presetCustomValues : presetAllValues) }, { signal: writes.signal })
    if (disposed) return
    // 后端按 UpdatedAt 倒序返回；新预设排到对应位置，并让在途的旧读取响应作废。
    presetLoadToken++
    presets.value = sortPresets([...presets.value, data]); presetsError.value = false
    presetId.value = data.id; savePresetOpen.value = false
    ElMessage.success('预设已保存，可应用到其他群')
  } catch { /* 重名或网络失败时保留对话框与输入。 */ }
  finally { savingPreset.value = false }
}
async function deletePreset(preset) {
  // 并发删除共用一个 loading 位：删除进行中直接忽略新的请求，避免第一个 finally 清掉第二个的 loading。
  if (deletingPreset.value || presetsLoading.value) return
  deletingPreset.value = preset.id
  try {
    await api.delete(`/proactive/presets/${encodeURIComponent(preset.id)}`, { signal: writes.signal })
    if (disposed) return
    presetLoadToken++
    presets.value = presets.value.filter(p => p.id !== preset.id); presetsError.value = false
    if (presetId.value === preset.id) presetId.value = presets.value[0]?.id || ''
    ElMessage.success('预设已删除，群设置保持原样')
  } catch { /* 删除失败时保留原列表。 */ }
  finally { if (deletingPreset.value === preset.id) deletingPreset.value = null }
}
defineExpose({ refresh })
onMounted(refresh)
onUnmounted(() => { disposed = true; groupRequest.cancel(); listRequest.cancel(); presetRequest.cancel(); writes.abort() })
</script>
<style scoped>
.proactive-parameters { margin-top: 16px; }
.parameters-heading, .parameter-actions, .scope-source, .field-control, .preset-toolbar, .parameter-footer, .save-status, .global-policy, .policy-control { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
.parameters-heading { justify-content: space-between; align-items: flex-start; gap: 16px; margin-bottom: 22px; }
.parameters-heading h2 { margin: 0; font-size: 20px; font-weight: 600; color: var(--panel-text); }
.parameters-heading p { margin: 6px 0 0; font-size: 13px; color: var(--panel-text-muted); }
.parameter-actions { gap: 8px; }
.parameter-actions :deep(.el-button + .el-button), .preset-toolbar :deep(.el-button + .el-button) { margin-left: 0; }
.scope-panel { display: grid; grid-template-columns: minmax(180px, 1fr) minmax(0, 1fr); gap: 20px; padding: 18px 20px; border: 1px solid var(--panel-border); border-radius: 10px; }
.scope-picker label { display: block; font-size: 12px; color: var(--panel-text-muted); margin-bottom: 8px; }
.scope-picker :deep(.el-select) { width: 100%; }
.scope-state { display: flex; flex-direction: column; justify-content: center; align-items: flex-end; gap: 8px; min-width: 0; }
.participation { display: flex; align-items: center; gap: 10px; color: var(--panel-text-regular); }
.scope-source { justify-content: flex-end; font-size: 12px; color: var(--panel-text-muted); }
.parameter-alert { margin-top: 12px; }
.global-draft-note { color: var(--el-color-warning); font-size: 12px; margin: 12px 0 0; }
.preset-toolbar { margin: 18px 0; gap: 10px; }
.preset-toolbar label { color: var(--panel-text-regular); font-size: 13px; }
.preset-toolbar :deep(.el-select) { width: 200px; max-width: 100%; }
.action-arrow { margin-left: 5px; }
.effective-summary { display: flex; align-items: flex-start; gap: 8px; color: var(--panel-text-regular); font-size: 13px; margin-bottom: 20px; }
.effective-summary > .el-icon { margin-top: 3px; color: var(--panel-accent-text); flex: none; }
.parameter-sections { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 16px; }
.parameter-section { padding: 20px 20px 4px; border: 1px solid var(--panel-border); border-radius: 10px; min-width: 0; }
.section-heading { display: flex; align-items: flex-start; gap: 10px; padding-bottom: 6px; }
.section-heading > .el-icon { color: var(--panel-accent-text); margin-top: 4px; }
.section-heading h3 { margin: 0; font-size: 15px; font-weight: 600; color: var(--panel-text); }
.section-heading p { margin: 4px 0 0; color: var(--panel-text-muted); font-size: 12px; }
.parameter-advanced { margin-top: 16px; border: 1px solid var(--panel-border); border-radius: 10px; padding: 0 20px; }
.parameter-advanced :deep(.el-collapse-item__header) { height: auto; min-height: 56px; gap: 10px; font-weight: 400; font-size: 14px; border: 0; }
.parameter-advanced :deep(.el-collapse-item__header small) { color: var(--panel-text-muted); font-size: 12px; margin-left: auto; }
.parameter-advanced :deep(.el-collapse-item__title) { display: flex; align-items: center; gap: 12px; justify-content: space-between; flex: 1; min-width: 0; }
.parameter-advanced :deep(.el-collapse-item__arrow) { margin-left: 0; }
.parameter-advanced :deep(.el-collapse-item__wrap) { border: 0; }
.parameter-advanced :deep(.el-collapse-item__content) { padding-bottom: 6px; }
.advanced-fields { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); column-gap: 28px; }
.advanced-fields :deep(.parameter-field) { border-top: 1px solid var(--panel-border-soft); }
.global-policy { justify-content: space-between; border-top: 1px solid var(--panel-border); padding: 18px 0; margin-top: 20px; font-size: 12px; color: var(--panel-text-muted); gap: 16px; }
.global-policy b { font-weight: 500; color: var(--panel-text-regular); }
.policy-control :deep(.el-input-number) { width: 92px; }
.policy-control :deep(input[type=number]) { appearance: textfield; -moz-appearance: textfield; }
.policy-control :deep(input::-webkit-inner-spin-button), .policy-control :deep(input::-webkit-outer-spin-button) { -webkit-appearance: none; margin: 0; }
.parameter-footer { justify-content: space-between; gap: 14px; border-top: 1px solid var(--panel-border); padding-top: 18px; }
.save-status { color: var(--panel-text-muted); font-size: 13px; }
.save-status > span { width: 6px; height: 6px; border-radius: 50%; background: var(--el-color-success); }
.save-status > span.dirty { background: var(--el-color-warning); }
.save-status small { font-size: 12px; }
.parameter-validation { color: var(--el-color-danger); font-size: 13px; }
.preset-context { margin: 0; color: var(--panel-text-muted); font-size: 13px; line-height: 1.7; }
.preset-group { margin-top: 20px; }
.preset-group h4 { color: var(--panel-text-muted); font-size: 12px; font-weight: 400; margin: 0 0 9px; }
.preset-row { display: flex; justify-content: space-between; align-items: center; flex-wrap: wrap; gap: 12px; padding: 15px 0; border-top: 1px solid var(--panel-border-soft); }
.preset-row b { font-weight: 500; color: var(--panel-text); }
.preset-row p { margin: 4px 0; color: var(--panel-text-muted); font-size: 13px; }
.preset-row small { color: var(--panel-text-muted); font-size: 12px; }
.preset-save-form { margin-top: 18px; }
.preset-mode { display: flex; flex-direction: column; align-items: flex-start; }
.preset-mode :deep(.el-radio) { white-space: normal; margin: 0; min-height: 36px; height: auto; max-width: 100%; }
.preset-mode :deep(.el-radio__label) { white-space: normal; line-height: 1.6; }
.preset-diff { margin-top: 16px; }
.preset-diff small { display: block; color: var(--panel-text-muted); font-size: 12px; }
.preset-next { color: var(--panel-accent-text); }
.apply-note { margin-top: 14px; }
@media (max-width: 1100px) { .scope-panel { grid-template-columns: 1fr; gap: 12px; } .scope-state { align-items: flex-start; } .scope-source { justify-content: flex-start; } }
@media (max-width: 760px) { .parameter-sections { grid-template-columns: 1fr; } .parameter-section { padding: 16px 16px 0; } }
@media (max-width: 600px) {
  .parameters-heading h2 { font-size: 18px; }
  .scope-panel { padding: 14px; }
  .advanced-fields { grid-template-columns: 1fr; }
  .parameter-advanced { padding: 0 14px; }
  .preset-toolbar :deep(.el-select) { width: calc(100% - 75px); flex: 1; }
  .parameter-footer > .parameter-actions { width: 100%; justify-content: flex-end; }
  .parameter-footer :deep(.el-button) { max-width: 100%; }
}
</style>
