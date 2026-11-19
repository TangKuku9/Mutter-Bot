<template>
  <div class="stickers-page">
    <div class="group-picker">
      <el-select :model-value="groupId" filterable placeholder="选择群聊" aria-label="选择表情图库的群"
        :disabled="saving" @change="chooseGroup">
        <el-option v-for="group in groups" :key="group.groupOpenId" :value="group.groupOpenId"
          :label="group.name || group.groupOpenId" />
      </el-select>
      <el-button :loading="loading" :disabled="saving" @click="refresh">刷新</el-button>
    </div>

    <el-result v-if="failed" icon="warning" title="表情图库读取失败">
      <template #extra><el-button type="primary" @click="refresh">重试</el-button></template>
    </el-result>
    <el-empty v-else-if="!groupId && !loading" description="机器人收到群聊事件后，即可在这里管理该群的表情" />
    <template v-else-if="settings">
      <el-card shadow="never">
        <template #header><b>收藏与发送设置</b></template>
        <div class="collection-row">
          <el-switch v-model="settings.autoCollectStickers" aria-label="自动收藏表情" :disabled="saving" />
          <div><b>自动收藏</b><p>只保存机器人收到的 QQ 静态表情包。</p></div>
        </div>
        <div class="collection-row">
          <el-switch v-model="settings.autoNameStickers" aria-label="AI 自动取名" :disabled="saving" />
          <div><b>AI 自动取名</b><p>为新增的未命名表情取名。取名时会把图片发送给 <router-link to="/ai-settings">AI 设置</router-link> 中当前保存的模型。</p></div>
        </div>
        <el-form label-position="top" :disabled="saving" class="policy-grid" @submit.prevent="saveSettings">
          <el-form-item label="发送场景">
            <el-select v-model="sendMode" aria-label="表情发送场景">
              <el-option v-for="mode in sendModes" :key="mode.value" :label="mode.label" :value="mode.value" />
            </el-select>
          </el-form-item>
          <el-form-item label="@ 回复配图倾向">
            <el-select v-model="settings.stickerReplyTendency" :disabled="!settings.useStickersInReplies" aria-label="@ 回复配图倾向">
              <el-option v-for="level in tendencies" :key="level.value" v-bind="level" />
            </el-select>
          </el-form-item>
          <el-form-item label="择机配图倾向">
            <el-select v-model="settings.stickerProactiveTendency" :disabled="!settings.useStickersInProactive" aria-label="择机配图倾向">
              <el-option v-for="level in tendencies" :key="level.value" v-bind="level" />
            </el-select>
          </el-form-item>
          <el-form-item label="同群最低发送间隔">
            <el-input-number v-model="settings.stickerIntervalMinutes" :min="0" :max="60" :step="1"
              step-strictly aria-label="同群最低发送间隔（分钟）" />
            <span class="minutes">分钟</span>
            <p>0 表示不限。相同表情仍间隔至少 5 分钟。</p>
          </el-form-item>
        </el-form>
        <p v-if="!settings.isEnabled && (settings.autoCollectStickers || sendMode !== 'off')" class="status-note">请先在群管理里启用本群，收藏与发送设置才会生效。</p>
        <p v-else-if="sendMode !== 'off' && !settings.markdownAvailable" class="status-note">
          当前群暂不能发送表情。<router-link :to="{ path: '/groups', query: { markdown: '1' } }">查看 QQ Markdown 设置</router-link>
        </p>
        <p v-if="settings.useStickersInProactive" class="status-note">
          {{ settings.aiProactiveEnabled ? '择机发言仍遵守现有的发言条件和配额。' : '要在择机发言时配图，请先在群管理里启用本群参与择机发言。' }}
        </p>
        <div class="settings-footer">
          <span aria-live="polite">{{ dirty ? '有未保存的修改' : '设置已保存' }}</span>
          <el-button type="primary" :loading="saving" :disabled="!dirty || !validInterval" @click="saveSettings">保存设置</el-button>
        </div>
      </el-card>

      <el-card shadow="never" class="gallery-card" v-loading="loading">
        <template #header>
          <div class="gallery-header">
            <div><b>本群收藏</b><span class="item-count">{{ rows.length }} / 200</span></div>
            <div class="gallery-actions">
              <el-button :disabled="loading || !filtered.length || !copyTargets.length" @click="openCopy">复制到其他群</el-button>
              <el-button type="primary" plain @click="openImport">添加图片</el-button>
            </div>
          </div>
        </template>
        <div v-if="rows.length" class="gallery-filters">
          <el-input v-model="query" placeholder="搜索表情描述" clearable aria-label="搜索表情描述" />
          <el-select v-model="filter" aria-label="按表情状态筛选">
            <el-option label="全部状态" value="all" />
            <el-option label="可选用" value="ready" />
            <el-option label="未标注" value="untagged" />
            <el-option label="已停用" value="disabled" />
          </el-select>
          <el-select v-model="sort" aria-label="表情排序">
            <el-option label="最近出现" value="recent" />
            <el-option label="出现最多" value="seen" />
            <el-option label="发送最多" value="sent" />
          </el-select>
        </div>
        <el-empty v-if="!filtered.length" :description="rows.length ? '没有匹配的表情' : '还没有收藏到表情，可添加静态图片开始使用'" />
        <div v-else class="gallery-grid">
          <button v-for="row in filtered" :key="row.hash" type="button" class="sticker-tile"
            :aria-label="'查看表情：' + (row.label || '未命名表情')" @click="edit(row)">
            <span class="tile-art"><img :src="imageUrl(row)" :alt="row.label || '未命名表情'" loading="lazy" /></span>
            <span class="tile-details">
              <strong :title="row.label">{{ row.label || '未命名表情' }}</strong>
              <span class="tile-meta"><span>出现 {{ row.seenCount }} · 发送 {{ row.sentCount }}</span>
                <el-tag size="small" :type="state(row) === 'ready' ? 'success' : 'info'">{{ stateLabel(row) }}</el-tag>
              </span>
            </span>
          </button>
        </div>
      </el-card>
    </template>

    <el-dialog v-model="editorOpen" :title="draft?.hash ? '表情详情' : '添加图片'"
      :width="narrow ? 'calc(100% - 24px)' : '480px'" :close-on-click-modal="!editorBusy"
      :close-on-press-escape="!editorBusy" :show-close="!editorBusy" @closed="draft = null">
      <div v-if="draft" class="edit-sticker">
        <div v-if="draft.hash || draft.preview" class="edit-preview">
          <img :src="draft.hash ? imageUrl(draft) : draft.preview" :alt="draft.label || '表情预览'" />
        </div>
        <label v-if="!draft.hash" class="file-field">
          <span>静态 PNG、JPEG 或 WebP，最大 16 MiB。超过图库限制时会自动压缩。</span>
          <input type="file" accept="image/png,image/jpeg,image/webp,.png,.jpg,.jpeg,.webp" :disabled="editorBusy" @change="selectFile" />
        </label>
        <el-form label-position="top" :disabled="editorBusy" @submit.prevent="saveSticker">
          <el-form-item label="表情描述">
            <div class="description-field">
              <el-input v-model="draft.label" placeholder="例如：开心鼓掌" maxlength="60" aria-label="表情描述" />
              <el-button v-if="draft.hash" plain :loading="naming" @click="suggestName">AI 取名</el-button>
            </div>
            <p>已标注且启用的表情才会进入模型候选。</p>
          </el-form-item>
          <el-form-item v-if="draft.hash" label="允许模型选用">
            <el-switch v-model="draft.isEnabled" aria-label="允许模型选用此表情" />
          </el-form-item>
        </el-form>
      </div>
      <template #footer>
        <div class="editor-footer">
          <el-popconfirm v-if="draft?.hash" title="从本群图库移除这张表情？" width="240" @confirm="removeSticker">
            <template #reference><el-button type="danger" link :disabled="editorBusy">删除表情</el-button></template>
          </el-popconfirm>
          <el-button class="editor-cancel" :disabled="editorBusy" @click="editorOpen = false">关闭</el-button>
          <el-button type="primary" :loading="busy" :disabled="naming || (!draft?.hash && !draft?.file)" @click="saveSticker">
            {{ draft?.hash ? '保存表情' : '添加到本群' }}
          </el-button>
        </div>
      </template>
    </el-dialog>

    <el-dialog v-model="copyOpen" title="复制到其他群" :width="narrow ? 'calc(100% - 24px)' : '540px'"
      :close-on-click-modal="!copying" :close-on-press-escape="!copying" :show-close="!copying" destroy-on-close>
      <el-form label-position="top" :disabled="copying" @submit.prevent="copySelected">
        <el-form-item label="目标群">
          <el-select v-model="copyTargetId" filterable placeholder="选择目标群" aria-label="目标群" class="copy-target">
            <el-option v-for="group in copyTargets" :key="group.groupOpenId" :value="group.groupOpenId"
              :label="group.name || group.groupOpenId" />
          </el-select>
        </el-form-item>
        <div class="copy-toolbar">
          <el-checkbox :model-value="allSelectedForCopy" :indeterminate="copyHashes.length > 0 && !allSelectedForCopy"
            @change="selectAllForCopy">{{ filter !== 'all' || query.trim() ? '全选筛选结果' : '全选' }}</el-checkbox>
          <span aria-live="polite">已选 {{ copyHashes.length }} / {{ copyRows.length }} 张</span>
        </div>
        <el-checkbox-group v-model="copyHashes" aria-label="要复制的表情" class="copy-list">
          <el-checkbox v-for="row in copyRows" :key="row.hash" :value="row.hash" class="copy-choice">
            <img :src="imageUrl(row)" alt="" loading="lazy" />
            <span :title="row.label">{{ row.label || '未命名表情' }}</span>
          </el-checkbox>
        </el-checkbox-group>
        <p class="status-note">复制表情名称和启用状态，目标群已收藏的同一图片会跳过。</p>
      </el-form>
      <template #footer>
        <el-button :disabled="copying" @click="copyOpen = false">取消</el-button>
        <el-button type="primary" :loading="copying" :disabled="!copyTargetId || !copyHashes.length"
          @click="copySelected">复制所选表情</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup>
import { computed, onMounted, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import api from '../api'
import { useUnsavedChanges } from '../useUnsavedChanges'
import { useMediaQuery } from '../useMediaQuery'
import { useVisiblePolling } from '../useVisiblePolling'

const route = useRoute()
const router = useRouter()
const narrow = useMediaQuery('(max-width: 600px)')
const groups = ref([])
const groupId = ref('')
const settings = ref(null)
const baseline = ref('')
const rows = ref([])
const query = ref('')
const filter = ref('all')
const sort = ref('recent')
const loading = ref(false)
const failed = ref(false)
const saving = ref(false)
const draft = ref(null)
const editorOpen = ref(false)
const busy = ref(false)
const naming = ref(false)
const editorBusy = computed(() => busy.value || naming.value)
const copyOpen = ref(false)
const copying = ref(false)
const copyTargetId = ref('')
const copyRows = ref([])
const copyHashes = ref([])
const copyTargets = computed(() => groups.value.filter(group => group.groupOpenId !== groupId.value))
const allSelectedForCopy = computed(() => copyRows.value.length > 0 && copyHashes.value.length === copyRows.value.length)
const maxUploadBytes = 16 * 1024 * 1024
const endpoint = computed(() => `/groups/${encodeURIComponent(groupId.value)}/stickers`)
const dirty = computed(() => settings.value !== null && JSON.stringify(settings.value) !== baseline.value)
const confirmDiscard = useUnsavedChanges(dirty)
const validInterval = computed(() => Number.isInteger(settings.value?.stickerIntervalMinutes))
const sendModes = [{ label: '关闭发送', value: 'off' }, { label: '仅 @ 回复', value: 'reply' },
  { label: '仅择机发言', value: 'proactive' }, { label: '@ 回复和择机发言', value: 'both' }]
const tendencies = [{ label: '克制', value: 0 }, { label: '自然', value: 1 }, { label: '积极', value: 2 }]
const sendMode = computed({
  get: () => settings.value.useStickersInReplies
    ? settings.value.useStickersInProactive ? 'both' : 'reply'
    : settings.value.useStickersInProactive ? 'proactive' : 'off',
  set: (mode) => {
    settings.value.useStickersInReplies = mode === 'reply' || mode === 'both'
    settings.value.useStickersInProactive = mode === 'proactive' || mode === 'both'
  }
})
const state = (row) => !row.isEnabled ? 'disabled' : row.label ? 'ready' : 'untagged'
const stateLabel = (row) => ({ ready: '可选用', untagged: '未标注', disabled: '已停用' })[state(row)]
const imageUrl = (row) => '/api' + endpoint.value + '/' + encodeURIComponent(row.hash) + '/image'
const filtered = computed(() => {
  const text = query.value.trim().toLowerCase()
  return rows.value.filter(row => (!text || row.label.toLowerCase().includes(text))
    && (filter.value === 'all' || state(row) === filter.value)).sort((a, b) =>
    sort.value === 'seen' ? b.seenCount - a.seenCount : sort.value === 'sent' ? b.sentCount - a.sentCount
      : (b.lastSeenAt ?? '').localeCompare(a.lastSeenAt ?? ''))
})
let requestId = 0
let rowsRequestId = 0

async function load() {
  if (!groupId.value) return
  const current = ++requestId
  const url = endpoint.value
  loading.value = true
  failed.value = false
  try {
    const [policy, images] = await Promise.all([api.get(url + '/settings'), api.get(url)])
    if (current !== requestId) return
    settings.value = policy.data
    baseline.value = JSON.stringify(policy.data)
    rows.value = images.data
  } catch {
    if (current === requestId) failed.value = true
  } finally {
    if (current === requestId) loading.value = false
  }
}

async function loadGroups() {
  loading.value = true
  failed.value = false
  try {
    const { data } = await api.get('/groups')
    groups.value = data
    groupId.value = data.find(group => group.groupOpenId === route.query.group)?.groupOpenId ?? data[0]?.groupOpenId ?? ''
    await load()
  } catch {
    failed.value = true
  } finally { loading.value = false }
}

async function chooseGroup(id) {
  if (id === groupId.value || !await confirmDiscard()) return
  groupId.value = id
  settings.value = null
  rows.value = []
  query.value = ''
  filter.value = 'all'
  await router.replace({ query: { group: id } })
  await load()
}

async function refresh() {
  if (await confirmDiscard()) await (groupId.value ? load() : loadGroups())
}

async function saveSettings() {
  if (!dirty.value || !validInterval.value || saving.value) return
  saving.value = true
  // 刷新尚未完成时保存，以此请求的回执作为更新后的基线。
  requestId++
  loading.value = false
  try {
    const { autoCollectStickers, autoNameStickers, useStickersInReplies, useStickersInProactive, stickerReplyTendency,
      stickerProactiveTendency, stickerIntervalMinutes } = settings.value
    const { data } = await api.put(endpoint.value + '/settings', {
      autoCollectStickers, autoNameStickers, useStickersInReplies, useStickersInProactive, stickerReplyTendency,
      stickerProactiveTendency, stickerIntervalMinutes
    })
    settings.value = data
    baseline.value = JSON.stringify(data)
    ElMessage.success('表情设置已保存')
  } catch {
    // API 显示错误，保留修改。
  } finally { saving.value = false }
}

function edit(row) {
  draft.value = { ...row, initialLabel: row.label }
  editorOpen.value = true
}

function openImport() {
  draft.value = { label: '', file: null, preview: '' }
  editorOpen.value = true
}

function openCopy() {
  copyRows.value = filtered.value
  copyHashes.value = []
  copyTargetId.value = ''
  copyOpen.value = true
}

function selectAllForCopy(selected) {
  copyHashes.value = selected ? copyRows.value.map(row => row.hash) : []
}

async function copySelected() {
  const target = copyTargets.value.find(group => group.groupOpenId === copyTargetId.value)
  if (!target || !copyHashes.value.length || copying.value) return
  copying.value = true
  try {
    const { data } = await api.post(endpoint.value + '/copy', { targetGroupId: target.groupOpenId, hashes: copyHashes.value })
    const name = target.name || target.groupOpenId
    const skipped = data.alreadyInTarget ? `，跳过 ${data.alreadyInTarget} 张重复图片` : ''
    ElMessage.success(data.copied ? `已复制 ${data.copied} 张表情到「${name}」${skipped}` : `「${name}」已收藏所选表情`)
    copyOpen.value = false
  } catch {
    // API 显示错误，保留当前选择。
  } finally { copying.value = false }
}

function selectFile(event) {
  const file = event.target.files?.[0]
  const target = draft.value
  target.file = null
  target.preview = ''
  if (!file) return
  if (!file.size || file.size > maxUploadBytes || (file.type && !['image/png', 'image/jpeg', 'image/webp'].includes(file.type))) {
    ElMessage.error('请选择不超过 16 MiB 的 PNG、JPEG 或 WebP')
    event.target.value = ''
    return
  }
  target.file = file
  const reader = new FileReader()
  reader.onload = () => {
    if (draft.value?.file === file) draft.value.preview = reader.result
  }
  reader.readAsDataURL(file)
}

async function refreshRows(config) {
  const current = ++rowsRequestId
  const groupRequest = requestId
  const url = endpoint.value
  const { data } = await api.get(url, config)
  if (current === rowsRequestId && groupRequest === requestId && url === endpoint.value) rows.value = data
}

async function suggestName() {
  const target = draft.value
  if (!target?.hash || editorBusy.value) return
  const url = endpoint.value
  naming.value = true
  try {
    const { data } = await api.post(url + '/' + target.hash + '/ai-name', null, { timeout: 70000 })
    if (draft.value === target && endpoint.value === url) target.label = data.label
  } catch {
    // API 显示错误，保留当前描述。
  } finally { naming.value = false }
}

async function saveSticker() {
  if (!draft.value || editorBusy.value) return
  const { hash, label, initialLabel, isEnabled, file } = draft.value
  if (!hash && !file) return
  busy.value = true
  try {
    if (hash) {
      const update = { label, isEnabled }
      if (label === initialLabel) update.preserveLabel = true
      await api.put(endpoint.value + '/' + hash, update)
    } else {
      const body = new FormData()
      body.append('file', file)
      body.append('label', label)
      await api.post(endpoint.value, body)
    }
    editorOpen.value = false
    await refreshRows()
  } catch {
    // API 显示错误，保留尚未提交的编辑内容。
  } finally { busy.value = false }
}

async function removeSticker() {
  if (!draft.value?.hash || editorBusy.value) return
  busy.value = true
  const hash = draft.value.hash
  try {
    await api.delete(endpoint.value + '/' + hash)
    rowsRequestId++
    rows.value = rows.value.filter(row => row.hash !== hash)
    editorOpen.value = false
  } catch {
    // API 显示错误。
  } finally { busy.value = false }
}

onMounted(loadGroups)
useVisiblePolling(signal => {
  if (settings.value?.autoNameStickers && !dirty.value && !loading.value && !failed.value
    && !editorOpen.value && !editorBusy.value && !copyOpen.value)
    return refreshRows({ signal, skipErrorMessage: true })
}, 8000)
</script>

<style scoped>
.stickers-page { display: grid; gap: 16px; }
.group-picker, .gallery-header, .settings-footer, .editor-footer { display: flex; align-items: center; gap: 10px; }
.group-picker .el-select { width: min(340px, 70%); }
.collection-row { display: flex; align-items: center; gap: 12px; margin-bottom: 18px; }
.collection-row p, .policy-grid p, .edit-sticker p { margin: 2px 0 0; color: var(--el-text-color-secondary); font-size: 12px; line-height: 1.45; }
.policy-grid { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: 16px; }
.policy-grid .el-select { width: 100%; }
.policy-grid :deep(.el-form-item) { margin-bottom: 6px; }
.minutes { margin-left: 6px; }
.status-note { margin: 8px 0; color: var(--el-text-color-secondary); font-size: 13px; }
.gallery-header, .settings-footer { justify-content: space-between; }
.gallery-header, .gallery-actions { flex-wrap: wrap; }
.gallery-actions, .copy-toolbar { display: flex; align-items: center; gap: 8px; }
.gallery-actions .el-button + .el-button { margin-left: 0; }
.settings-footer { border-top: 1px solid var(--el-border-color-lighter); padding-top: 12px; margin-top: 12px; font-size: 13px; color: var(--el-text-color-secondary); }
.item-count { margin-left: 8px; font-size: 12px; color: var(--el-text-color-secondary); }
.gallery-filters { display: grid; grid-template-columns: minmax(0, 1fr) 132px 132px; gap: 10px; margin-bottom: 14px; }
.gallery-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(min(155px, 46%), 1fr)); gap: 12px; }
.sticker-tile { display: grid; text-align: left; min-width: 0; padding: 0; border-radius: 8px; overflow: hidden; border: 1px solid var(--el-border-color-lighter); background: var(--el-bg-color); color: var(--el-text-color-primary); cursor: pointer; font: inherit; }
.sticker-tile:hover, .sticker-tile:focus-visible { border-color: var(--el-color-primary); }
.tile-art { display: grid; place-items: center; aspect-ratio: 4 / 3; background: var(--el-fill-color-light); }
.tile-art img { width: 88%; height: 88%; max-width: 144px; max-height: 128px; object-fit: contain; }
.tile-details { display: grid; gap: 6px; min-width: 0; padding: 10px; }
.tile-details strong { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.tile-meta { display: flex; gap: 4px; justify-content: space-between; align-items: center; flex-wrap: wrap; color: var(--el-text-color-secondary); font-size: 11px; }
.edit-preview { display: grid; place-items: center; min-height: 160px; background: var(--el-fill-color-light); border-radius: 8px; margin-bottom: 16px; }
.edit-preview img { max-width: 100%; max-height: 320px; object-fit: contain; }
.file-field { display: grid; gap: 8px; font-size: 13px; margin-bottom: 12px; }
.description-field { display: flex; width: 100%; gap: 8px; }
.description-field .el-input { flex: 1; min-width: 0; }
.editor-cancel { margin-left: auto; }
.copy-target { width: 100%; }
.copy-toolbar { justify-content: space-between; color: var(--el-text-color-secondary); font-size: 13px; }
.copy-list { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 180px), 1fr)); gap: 4px 12px;
  max-height: min(42vh, 320px); overflow-y: auto; padding: 6px 10px; border: 1px solid var(--el-border-color); border-radius: 8px; }
.copy-choice { min-width: 0; height: 44px; margin: 0; }
.copy-choice :deep(.el-checkbox__label) { display: flex; align-items: center; gap: 8px; min-width: 0; }
.copy-choice img { width: 36px; height: 36px; flex: none; object-fit: contain; }
.copy-choice span { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
@media (max-width: 1050px) { .policy-grid { grid-template-columns: repeat(2, minmax(0, 1fr)); } }
@media (max-width: 600px) {
  .policy-grid { grid-template-columns: minmax(0, 1fr); gap: 6px; }
  .gallery-filters { grid-template-columns: repeat(2, minmax(0, 1fr)); }
  .gallery-filters .el-input { grid-column: 1 / -1; }
}
</style>
