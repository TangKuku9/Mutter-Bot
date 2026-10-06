<template>
  <el-card shadow="never" v-loading="loading">
    <template #header><b>AI 配置</b></template>

    <el-form :model="form" label-width="130px" style="max-width: 760px">
      <el-form-item label="服务商预设">
        <el-select v-model="presetId" placeholder="选择预设自动填充" clearable style="width: 320px" @change="applyPreset">
          <el-option v-for="p in form.availableProviders" :key="p.id" :label="p.name" :value="p.id">
            <span>{{ p.name }}</span>
            <span style="float: right; color: #909399; font-size: 12px">{{ p.defaultModel }}</span>
          </el-option>
        </el-select>
        <div class="hint" v-if="presetHelp">{{ presetHelp }}</div>
      </el-form-item>
      <el-form-item label="API 地址">
        <el-input v-model="form.apiBaseUrl" placeholder="https://api.example.com/v1" />
      </el-form-item>
      <el-form-item label="API Key">
        <el-input v-model="form.apiKey" type="password" show-password placeholder="留空或保持打码值表示不修改" />
      </el-form-item>
      <el-form-item label="模型">
        <el-input v-model="form.modelName" placeholder="模型名" />
      </el-form-item>
      <el-form-item label="人设">
        <el-select v-model="form.personality" clearable placeholder="选择角色人设" style="width: 320px">
          <el-option v-for="c in form.availableCharacters" :key="c.name" :label="c.displayName" :value="c.name">
            <span>{{ c.displayName }}<el-tag v-if="c.isCustom" size="small" type="warning" style="margin-left: 6px">自定义</el-tag></span>
            <span style="float: right; color: #909399; font-size: 12px">{{ c.description }}</span>
          </el-option>
        </el-select>
        <el-button style="margin-left: 12px" @click="manageDialog = true">管理人设</el-button>
        <div class="hint">全局生效；群内单独指定的人设优先级更高</div>
      </el-form-item>
      <el-form-item label="系统提示词">
        <el-input v-model="form.systemPrompt" type="textarea" :rows="5" placeholder="留空则使用内置默认提示词" />
      </el-form-item>
      <el-form-item label="Temperature">
        <el-slider v-model="form.temperature" :min="0" :max="2" :step="0.1" show-input style="max-width: 420px" />
      </el-form-item>
      <el-form-item label="最大 Tokens">
        <el-input-number v-model="form.maxTokens" :min="256" :max="32768" :step="256" />
      </el-form-item>
      <el-form-item label="上下文条数">
        <el-input-number v-model="form.maxContextMessages" :min="2" :max="100" />
      </el-form-item>
      <el-form-item label="联网搜索">
        <el-switch v-model="form.webSearchEnabled" />
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
  </el-card>
</template>

<script setup>
import { ref, computed, onMounted } from 'vue'
import { ElMessage } from 'element-plus'
import api from '../api'

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
      webSearchEnabled: form.value.webSearchEnabled
    })
    ElMessage.success('AI 配置已保存')
    await load()
  } finally { saving.value = false }
}

onMounted(load)
</script>

<style scoped>
.hint { color: #909399; font-size: 12px; margin-top: 4px; }
</style>
