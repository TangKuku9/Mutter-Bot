<template>
  <el-card shadow="never" v-loading="loading">
    <template #header>
      <div class="card-head">
        <b>AI 配置</b>
        <el-button size="small" @click="openHelp('presetId')">参数说明</el-button>
      </div>
    </template>

    <el-form :model="form" label-width="130px" style="max-width: 760px">
      <el-form-item label="服务商预设">
        <el-select v-model="presetId" placeholder="选择预设自动填充" clearable style="width: 320px" @change="applyPreset">
          <el-option v-for="p in form.availableProviders" :key="p.id" :label="p.name" :value="p.id">
            <span>{{ p.name }}</span>
            <span style="float: right; color: #909399; font-size: 12px">{{ p.defaultModel }}</span>
          </el-option>
        </el-select>
        <ParamHelpButton :label="helpLabel('presetId')" @click="openHelp('presetId')" />
        <div class="hint" v-if="presetHelp">{{ presetHelp }}</div>
      </el-form-item>
      <el-form-item label="API 地址">
        <el-input v-model="form.apiBaseUrl" placeholder="https://api.example.com/v1" />
        <ParamHelpButton :label="helpLabel('apiBaseUrl')" @click="openHelp('apiBaseUrl')" />
      </el-form-item>
      <el-form-item label="API Key">
        <el-input v-model="form.apiKey" type="password" show-password placeholder="留空或保持打码值表示不修改" />
        <ParamHelpButton :label="helpLabel('apiKey')" @click="openHelp('apiKey')" />
      </el-form-item>
      <el-form-item label="模型">
        <el-input v-model="form.modelName" placeholder="模型名" />
        <ParamHelpButton :label="helpLabel('modelName')" @click="openHelp('modelName')" />
      </el-form-item>
      <el-form-item label="人设">
        <el-select v-model="form.personality" clearable placeholder="选择角色人设" style="width: 320px">
          <el-option v-for="c in form.availableCharacters" :key="c.name" :label="c.displayName" :value="c.name">
            <span>{{ c.displayName }}<el-tag v-if="c.isCustom" size="small" type="warning" style="margin-left: 6px">自定义</el-tag></span>
            <span style="float: right; color: #909399; font-size: 12px">{{ c.description }}</span>
          </el-option>
        </el-select>
        <el-button style="margin-left: 12px" @click="manageDialog = true">管理人设</el-button>
        <ParamHelpButton :label="helpLabel('personality')" @click="openHelp('personality')" />
      </el-form-item>
      <el-form-item label="系统提示词">
        <el-input v-model="form.systemPrompt" type="textarea" :rows="5" placeholder="留空则使用内置默认提示词" />
        <ParamHelpButton :label="helpLabel('systemPrompt')" @click="openHelp('systemPrompt')" />
      </el-form-item>
      <el-form-item label="Temperature">
        <el-slider v-model="form.temperature" :min="0" :max="2" :step="0.1" show-input style="max-width: 420px" />
        <ParamHelpButton :label="helpLabel('temperature')" @click="openHelp('temperature')" />
      </el-form-item>
      <el-form-item label="最大 Tokens">
        <el-input-number v-model="form.maxTokens" :min="256" :max="32768" :step="256" />
        <ParamHelpButton :label="helpLabel('maxTokens')" @click="openHelp('maxTokens')" />
      </el-form-item>
      <el-form-item label="上下文条数">
        <el-input-number v-model="form.maxContextMessages" :min="2" :max="100" />
        <ParamHelpButton :label="helpLabel('maxContextMessages')" @click="openHelp('maxContextMessages')" />
      </el-form-item>
      <el-form-item label="附图张数">
        <el-input-number v-model="form.maxAttachedImages" :min="0" :max="form.ceilingAttachedImages || 10" />
        <ParamHelpButton :label="helpLabel('maxAttachedImages')" @click="openHelp('maxAttachedImages')" />
      </el-form-item>
      <el-form-item label="联网搜索">
        <el-switch v-model="form.webSearchEnabled" />
        <ParamHelpButton :label="helpLabel('webSearchEnabled')" @click="openHelp('webSearchEnabled')" />
        <el-button size="small" style="margin-left: 12px" :loading="probing" :disabled="probing" @click="probeWebSearch">
          检测联网搜索
        </el-button>
      </el-form-item>
      <el-form-item>
        <el-button type="primary" :loading="saving" @click="save">保存配置</el-button>
        <el-button @click="load">重新加载</el-button>
      </el-form-item>
    </el-form>

    <!-- 人设管理 -->
    <el-dialog v-model="manageDialog" title="人设管理" width="720px">
      <el-table :data="form.availableCharacters" size="small">
        <el-table-column prop="displayName" label="名称" width="120">
          <template #default="{ row }">
            {{ row.displayName }}
            <el-tag v-if="row.isCustom" size="small" type="warning">自定义</el-tag>
            <el-tag v-else size="small" type="info">内置</el-tag>
          </template>
        </el-table-column>
        <el-table-column prop="name" label="标识" width="100" />
        <el-table-column prop="description" label="描述" min-width="160" show-overflow-tooltip />
        <el-table-column label="操作" width="130">
          <template #default="{ row }">
            <el-button size="small" link type="primary" @click="openEdit(row)">{{ row.isCustom ? '编辑' : '查看' }}</el-button>
            <el-popconfirm v-if="row.isCustom" title="删除后引用该人设的配置将回退为默认，确认删除？" @confirm="removeCharacter(row)">
              <template #reference>
                <el-button size="small" link type="danger">删除</el-button>
              </template>
            </el-popconfirm>
          </template>
        </el-table-column>
      </el-table>
      <template #footer>
        <el-button type="primary" @click="openEdit(null)">新增自定义人设</el-button>
        <el-button @click="manageDialog = false">关闭</el-button>
      </template>
    </el-dialog>

    <!-- 人设编辑/查看 -->
    <el-dialog v-model="editDialog" :title="editTitle" width="560px">
      <el-form :model="charForm" label-width="100px" v-if="charForm">
        <el-form-item label="标识" required>
          <el-input v-model="charForm.name" :disabled="charReadonly || editing" placeholder="英文标识，如 waifu，保存后不可改" maxlength="32" />
        </el-form-item>
        <el-form-item label="名称" required>
          <el-input v-model="charForm.displayName" :disabled="charReadonly" placeholder="显示名称，如 温柔学姐" />
        </el-form-item>
        <el-form-item label="描述">
          <el-input v-model="charForm.description" :disabled="charReadonly" placeholder="一句话简介，供列表展示" />
        </el-form-item>
        <el-form-item label="角色提示词">
          <el-input v-model="charForm.systemPrompt" :disabled="charReadonly" type="textarea" :rows="6" placeholder="设定角色身份、语气与行为规则，选中该人设后追加到系统提示词" />
        </el-form-item>
      </el-form>
      <template #footer v-if="!charReadonly">
        <el-button @click="editDialog = false">取消</el-button>
        <el-button type="primary" :loading="charSaving" @click="saveCharacter">保存</el-button>
      </template>
    </el-dialog>

    <!-- 联网搜索探测报告：判定矩阵全量展示，不命中即停——中转可能同时接受多种形状但只有一种真生效 -->
    <el-dialog v-model="probeDialog" title="联网搜索探测报告" width="680px">
      <el-alert v-if="probeReport && !probeReport.reachable" type="error" :closable="false" show-icon
        :title="probeReport.error || '上游不可达'" style="margin-bottom: 12px" />
      <el-alert v-else-if="probeReport && probeReport.authFailed" type="error" :closable="false" show-icon
        :title="probeReport.error || '鉴权失败'" style="margin-bottom: 12px" />
      <template v-else-if="probeReport">
        <div class="probe-summary">
          以「现在北京时间几点？」实测当前填写的地址与模型（基线 {{ probeReport.basePromptTokens ?? '?' }} prompt tokens）：
          <b>{{ verifiedCount }}</b> 种形状已验证、{{ ignoredCount }} 种被静默忽略。
        </div>
        <el-table :data="probeReport.results" size="small" v-loading="probing">
          <el-table-column label="请求形状" min-width="200">
            <template #default="{ row }">
              <code style="font-size: 12px">{{ row.shape }}</code>
              <div class="probe-desc">{{ row.description }}</div>
            </template>
          </el-table-column>
          <el-table-column label="判定" width="130">
            <template #default="{ row }">
              <el-tag size="small" :type="verdictType(row.status)">{{ verdictLabel(row.status) }}</el-tag>
            </template>
          </el-table-column>
          <el-table-column label="依据" min-width="240">
            <template #default="{ row }">
              <span>{{ row.note || '-' }}</span>
              <div v-if="row.groundingField" class="probe-desc">证据字段：{{ row.groundingField }}</div>
            </template>
          </el-table-column>
        </el-table>
        <div class="probe-footnote">
          「已验证」= 响应携带检索证据字段，或带搜索比基线多出大量 prompt token（搜索结果被注入）。
          「被静默忽略」= 请求 2xx 但无任何检索证据——这正是开关打开却毫无效果的情形。
          探测按表单当前值实测，不依赖已保存配置。
        </div>
      </template>
      <template #footer>
        <el-button @click="probeDialog = false">关闭</el-button>
      </template>
    </el-dialog>

    <ParamHelpDrawer v-model="helpVisible" :title="helpTitle" :lines="helpLines" :current="helpCurrent"
      :index="helpIndex" :total="helpTotal" @step="stepHelp" />
  </el-card>
</template>

<script setup>
import { ref, computed, onMounted } from 'vue'
import { ElMessage } from 'element-plus'
import api from '../api'
import { useParamHelp } from '../paramHelp'
import ParamHelpButton from '../components/ParamHelpButton.vue'
import ParamHelpDrawer from '../components/ParamHelpDrawer.vue'

const form = ref({ availableProviders: [], availableCharacters: [] })
const presetId = ref('')
const loading = ref(false)
const saving = ref(false)

// 联网搜索探测：用户点按钮才跑，结果按形状全量列出
const probing = ref(false)
const probeDialog = ref(false)
const probeReport = ref(null)
const verifiedCount = computed(() => (probeReport.value?.results || []).filter((r) => r.status === 'verified').length)
const ignoredCount = computed(() => (probeReport.value?.results || []).filter((r) => r.status === 'no_evidence').length)
const verdictLabel = (s) => ({
  verified: '已验证', no_evidence: '被静默忽略', rejected: '被上游拒绝',
  auth_failed: '鉴权失败', unreachable: '不可达', upstream_error: '上游错误'
}[s] || s)
const verdictType = (s) => ({
  verified: 'success', no_evidence: 'warning', rejected: 'info',
  auth_failed: 'danger', unreachable: 'danger', upstream_error: 'danger'
}[s] || 'info')

const probeWebSearch = async () => {
  if (!form.value.apiBaseUrl || !form.value.modelName) {
    ElMessage.warning('探测前先填写 API 地址与模型名')
    return
  }
  probing.value = true
  probeDialog.value = true
  probeReport.value = null
  try {
    // 矩阵 + 基线最多 12 次请求，远超 axios 默认 30s，单独放宽
    const { data } = await api.post('/config/ai/probe-websearch', {
      apiBaseUrl: form.value.apiBaseUrl,
      apiKey: form.value.apiKey,
      modelName: form.value.modelName
    }, { timeout: 170000 })
    probeReport.value = data
  } catch {
    probeDialog.value = false
  } finally { probing.value = false }
}

// 人设管理状态
const manageDialog = ref(false)
const editDialog = ref(false)
const charForm = ref(null)
const charReadonly = ref(false)
const editing = ref(false)
const charSaving = ref(false)

const editTitle = computed(() => {
  if (charReadonly.value) return '查看内置人设'
  return editing.value ? `编辑人设：${charForm.value?.name}` : '新增自定义人设'
})

const presetHelp = computed(() => {
  const p = (form.value.availableProviders || []).find((x) => x.id === presetId.value)
  return p?.keyHelpText || ''
})

const HELP_ORDER = [
  'presetId', 'apiBaseUrl', 'apiKey', 'modelName', 'personality', 'systemPrompt',
  'temperature', 'maxTokens', 'maxContextMessages', 'maxAttachedImages', 'webSearchEnabled'
]

const HELP = {
  presetId: {
    title: '服务商预设',
    value: () => (form.value.availableProviders || []).find((p) => p.id === presetId.value)?.name || '未选择',
    lines: [
      '选一个预设会填好 API 地址和默认模型，API Key 仍要自己填。',
      '没有合适的选「自定义」，地址和模型名自己写。',
      '预设只负责填一次，选完照样可以改地址或模型。'
    ]
  },
  apiBaseUrl: {
    title: 'API 地址',
    value: (f) => f.apiBaseUrl || '未填写',
    lines: [
      'OpenAI 兼容接口的根地址，一般以 /v1 结尾。',
      '选了预设会自动填好，只有自建或中转才需要改。'
    ]
  },
  apiKey: {
    title: 'API Key',
    value: (f) => (f.apiKey ? '已配置' : '未配置'),
    lines: [
      '只存在本机数据库，页面不回显明文。',
      '留空或保持打码值表示不修改；填新值会直接覆盖。'
    ]
  },
  modelName: {
    title: '模型',
    value: (f) => f.modelName || '未填写',
    lines: [
      '填服务商文档里的模型名，例如 deepseek-chat、qwen-plus。',
      '选了预设会带一个默认值，可以改。'
    ]
  },
  personality: {
    title: '人设',
    value: (f) => (f.availableCharacters || []).find((c) => c.name === f.personality)?.displayName || '未指定',
    lines: [
      '全局人设，作用于 @ 回复与私聊。',
      '「群管理」里为单个群指定的人设优先级更高。',
      '内置人设只能查看，自定义人设可以增删改。'
    ]
  },
  systemPrompt: {
    title: '系统提示词',
    value: (f) => (f.systemPrompt ? `${f.systemPrompt.length} 字` : '内置默认'),
    lines: [
      '留空则使用内置默认提示词。',
      '「群管理」里的「群专属提示词」会整段替换掉这里的全局提示词，是替换不是追加。'
    ]
  },
  temperature: {
    title: 'Temperature',
    value: (f) => `${f.temperature ?? '-'}`,
    lines: [
      '越高越随机，越低越守着说。',
      '默认 0.7；想让它稳一点就调到 0.3 上下。'
    ]
  },
  maxTokens: {
    title: '最大 Tokens',
    value: (f) => `${f.maxTokens}`,
    lines: [
      '单次回复的输出上限，默认 2048。',
      '与「择机发言」的「判定输出上限」互不影响，各管各的。'
    ]
  },
  maxContextMessages: {
    title: '上下文条数',
    value: (f) => `${f.maxContextMessages} 条`,
    lines: [
      '@ 回复与私聊带多少条历史消息，默认 20。',
      '「择机发言」有自己的「背景历史条数」，不受这里影响。'
    ]
  },
  maxAttachedImages: {
    title: '附图张数',
    value: (f) => (f.maxAttachedImages > 0 ? `${f.maxAttachedImages} 张` : '不看图'),
    lines: [
      '@ 回复与私聊最多带几张图，取消息里最靠前的几张；0 = 不看图。',
      '表情包和动图不会附给模型（GIF 会把请求打成 500），实际附上的可能少于这个数。',
      '「择机发言」有自己的「附图张数」，在那边单独设置。'
    ]
  },
  webSearchEnabled: {
    title: '联网搜索',
    value: (f) => (f.webSearchEnabled ? '开启' : '关闭'),
    lines: [
      '打开后，@ 回复与私聊可以先联网检索再回答。',
      '只在服务商支持时生效，会更慢也更费 token。'
    ]
  }
}

const {
  visible: helpVisible, index: helpIndex, total: helpTotal, title: helpTitle,
  lines: helpLines, current: helpCurrent, label: helpLabel, open: openHelp, step: stepHelp
} = useParamHelp(HELP, HELP_ORDER, () => form.value)

const load = async () => {
  loading.value = true
  try {
    const { data } = await api.get('/config/ai')
    form.value = data
  } finally { loading.value = false }
}

const applyPreset = (id) => {
  const p = (form.value.availableProviders || []).find((x) => x.id === id)
  if (!p) return
  form.value.apiBaseUrl = p.apiBaseUrl
  if (p.defaultModel) form.value.modelName = p.defaultModel
}

const refreshCharacters = async () => {
  const { data } = await api.get('/config/characters')
  form.value.availableCharacters = data
}

const openEdit = (row) => {
  editing.value = !!row
  charReadonly.value = !!row && !row.isCustom
  charForm.value = row
    ? { name: row.name, displayName: row.displayName, description: row.description, systemPrompt: row.systemPrompt }
    : { name: '', displayName: '', description: '', systemPrompt: '' }
  editDialog.value = true
}

const saveCharacter = async () => {
  if (!charForm.value.name.trim() || !charForm.value.displayName.trim()) {
    ElMessage.warning('标识与名称不能为空')
    return
  }
  charSaving.value = true
  try {
    if (editing.value) {
      await api.put(`/config/characters/${charForm.value.name}`, charForm.value)
    } else {
      await api.post('/config/characters', charForm.value)
    }
    ElMessage.success('人设已保存')
    editDialog.value = false
    await refreshCharacters()
  } finally { charSaving.value = false }
}

const removeCharacter = async (row) => {
  await api.delete(`/config/characters/${row.name}`)
  ElMessage.success('人设已删除')
  if (form.value.personality === row.name) form.value.personality = ''
  await refreshCharacters()
}

const save = async () => {
  saving.value = true
  try {
    await api.put('/config/ai', {
      apiBaseUrl: form.value.apiBaseUrl,
      apiKey: form.value.apiKey,
      modelName: form.value.modelName,
      systemPrompt: form.value.systemPrompt,
      personality: form.value.personality,
      temperature: form.value.temperature,
      maxTokens: form.value.maxTokens,
      maxContextMessages: form.value.maxContextMessages,
      maxAttachedImages: form.value.maxAttachedImages,
      webSearchEnabled: form.value.webSearchEnabled
    })
    ElMessage.success('AI 配置已保存')
    await load()
  } finally { saving.value = false }
}

onMounted(load)
</script>

<style scoped>
.card-head { display: flex; justify-content: space-between; align-items: center; }
/* 预设提示是选中后才知道的结果，留在原地更醒目；width 100% 保证它独占一行 */
.hint { width: 100%; color: #909399; font-size: 12px; margin-top: 4px; }
/* 整行控件给右边的「?」留出位置，否则宽度占满会把按钮挤到下一行 */
.el-form-item__content > .el-input,
.el-form-item__content > .el-textarea { width: calc(100% - 32px); }
.probe-summary { margin-bottom: 12px; font-size: 13px; line-height: 1.6; }
.probe-desc { color: #909399; font-size: 12px; }
.probe-footnote { color: #909399; font-size: 12px; line-height: 1.6; margin-top: 10px; }
</style>
