<template>
  <el-dialog v-model="visible" title="QQ群 Markdown" width="min(560px, calc(100vw - 24px))"
    :before-close="close" :close-on-click-modal="false">
    <p class="markdown-summary">用于群聊 AI 回复的来源链接、联网配图和表情包。</p>
    <div v-loading="loading" class="markdown-body">
      <el-alert v-if="failed" type="error" title="读取设置失败" :closable="false">
        <el-button link @click="load">重试</el-button>
      </el-alert>
      <template v-else-if="saved && draft">
        <p class="markdown-current">当前范围 <strong>{{ scopeLabel }}</strong><span v-if="platformLabel"> · {{ platformLabel }}</span></p>
        <p v-if="saved.mode === 'inherit' && saved.effectiveMode === 'selected'" class="markdown-current legacy-groups">
          {{ saved.groupIds.map(id => props.groups.find(group => group.groupOpenId === id)?.name || id).join('、') }}
        </p>
        <el-form label-position="top" :disabled="saving" @submit.prevent="save">
          <el-form-item label="群聊发送范围">
            <el-select v-model="draft.mode" aria-label="Markdown 发送范围">
              <el-option label="沿用配置文件" value="inherit" />
              <el-option label="关闭" value="off" />
              <el-option label="全部群" value="all" />
              <el-option label="仅选定群" value="selected" />
            </el-select>
          </el-form-item>
          <el-form-item v-if="draft.mode === 'selected'" label="启用 Markdown 的群">
            <el-input v-if="options.length > 8" v-model="query" clearable placeholder="搜索群名或 OpenId" aria-label="搜索群聊" />
            <el-checkbox-group v-model="draft.groupIds" aria-label="启用 Markdown 的群" class="markdown-groups">
              <el-checkbox v-for="group in filteredOptions" :key="group.id" :value="group.id">{{ group.name }}</el-checkbox>
            </el-checkbox-group>
            <p v-if="!filteredOptions.length" class="markdown-current">{{ options.length ? '没有匹配的群聊' : '还没有可选择的群聊' }}</p>
          </el-form-item>
        </el-form>
      </template>
    </div>
    <template #footer>
      <el-button :disabled="saving" @click="close()">关闭</el-button>
      <el-button type="primary" :loading="saving" :disabled="!dirty || !valid" @click="save">保存设置</el-button>
    </template>
  </el-dialog>
</template>

<script setup>
import { computed, ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import api from '../api'
import { useUnsavedChanges } from '../useUnsavedChanges'

const visible = defineModel({ type: Boolean })
const props = defineProps({ groups: { type: Array, required: true } })
const endpoint = '/groups/markdown-settings'
const saved = ref(null)
const draft = ref(null)
const loading = ref(false)
const failed = ref(false)
const saving = ref(false)
const query = ref('')
const dirty = computed(() => visible.value && !!saved.value && (draft.value.mode !== saved.value.mode
  || (draft.value.mode === 'selected' && (draft.value.groupIds.length !== saved.value.groupIds.length
    || draft.value.groupIds.some(id => !saved.value.groupIds.includes(id))))))
const valid = computed(() => !!draft.value && (draft.value.mode !== 'selected' || draft.value.groupIds.length > 0))
const confirmDiscard = useUnsavedChanges(dirty)
const scopeLabel = computed(() => ({ off: '关闭', all: '全部群', selected: `${saved.value.groupIds.length} 个选定群` })[saved.value.effectiveMode])
const platformLabel = computed(() => {
  if (saved.value.effectiveMode === 'off') return ''
  if (saved.value.platformState === 'Denied') return `QQ 拒绝（${saved.value.platformDeniedCode}），当前使用纯文本`
  return saved.value.platformState === 'Ok' ? 'QQ 已确认可用' : '等待 QQ 首次确认'
})
const options = computed(() => {
  const choices = new Map(props.groups.map(group => [group.groupOpenId, group.name || group.groupOpenId]))
  for (const id of draft.value?.groupIds ?? []) if (!choices.has(id)) choices.set(id, id)
  return Array.from(choices, ([id, name]) => ({ id, name }))
})
const filteredOptions = computed(() => {
  const text = query.value.trim().toLowerCase()
  return options.value.filter(group => group.name.toLowerCase().includes(text) || group.id.toLowerCase().includes(text))
})

function apply(data) {
  saved.value = data
  draft.value = { mode: data.mode, groupIds: [...data.groupIds] }
}

async function load() {
  if (loading.value) return
  loading.value = true
  failed.value = false
  saved.value = null
  query.value = ''
  try { apply((await api.get(endpoint)).data) }
  catch { failed.value = true }
  finally { loading.value = false }
}

async function close(done) {
  if (saving.value || !await confirmDiscard()) return
  if (typeof done === 'function') done()
  else visible.value = false
}

async function save() {
  if (!dirty.value || !valid.value || saving.value) return
  saving.value = true
  try {
    apply((await api.put(endpoint, draft.value)).data)
    ElMessage.success('Markdown 设置已保存')
  } catch {
    // API 会显示保存错误，保留当前编辑。
  } finally { saving.value = false }
}

watch(visible, open => { if (open) void load() }, { immediate: true })
</script>

<style scoped>
.markdown-summary { margin: 0 0 12px; }
.markdown-body { min-height: 60px; }
.markdown-current { margin: 0 0 16px; color: var(--el-text-color-secondary); }
.markdown-current strong { color: var(--el-text-color-primary); }
.legacy-groups { overflow-wrap: anywhere; max-height: 6em; overflow: auto; }
.markdown-groups { display: grid; max-height: 190px; overflow: auto; width: 100%; }
.markdown-groups :deep(.el-checkbox__label) { white-space: normal; overflow-wrap: anywhere; }
.el-select { width: 100%; }
</style>
