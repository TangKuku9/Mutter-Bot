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
</style>
