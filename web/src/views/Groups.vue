<template>
  <div>
    <el-card shadow="never">
      <template #header>
        <div class="card-head">
          <b>群列表与配置</b>
          <el-button size="small" @click="load">刷新</el-button>
        </div>
      </template>
      <el-table :data="groups" v-loading="loading" stripe>
        <el-table-column prop="name" label="群名" min-width="140">
          <template #default="{ row }">{{ row.name || '（未命名）' }}</template>
        </el-table-column>
        <el-table-column label="群 OpenId" min-width="150" show-overflow-tooltip>
          <template #default="{ row }"><code>{{ row.groupOpenId }}</code></template>
        </el-table-column>
        <el-table-column label="响应开关" width="90">
          <template #default="{ row }">
            <el-tag size="small" :type="row.config?.isEnabled ? 'success' : 'danger'">
              {{ row.config?.isEnabled ? '启用' : '停用' }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column label="围观范围" width="110">
          <template #default="{ row }">{{ triggerText(row.config?.triggerMode) }}</template>
        </el-table-column>
        <el-table-column label="AI 择机" width="90">
          <template #default="{ row }">
            <el-tag size="small" :type="row.config?.aiProactiveEnabled ? 'success' : 'info'">
              {{ row.config?.aiProactiveEnabled ? '参与' : '不参与' }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column prop="config.activeCharacter" label="人设" width="100">
          <template #default="{ row }">{{ characterLabel(row.config?.activeCharacter) }}</template>
        </el-table-column>
        <el-table-column label="加入时间" width="160">
          <template #default="{ row }">{{ fmt(row.addedAt) }}</template>
        </el-table-column>
        <el-table-column label="操作" width="90">
          <template #default="{ row }">
            <el-button size="small" type="primary" plain @click="edit(row)">编辑</el-button>
          </template>
        </el-table-column>
      </el-table>
    </el-card>

    <el-dialog v-model="dialog" title="群配置" width="560px">
      <el-form :model="form" label-width="110px" v-if="form">
        <el-form-item label="启用响应">
          <el-switch v-model="form.isEnabled" />
        </el-form-item>
        <el-form-item label="围观范围">
          <el-select v-model="form.triggerMode" style="width: 280px">
            <el-option :value="0" label="只记录被 @ 的消息" />
            <el-option :value="2" label="围观并记录全部消息" />
          </el-select>
          <div class="sub-hint">只影响聊天历史是否收录群友原话；要不要接话由「择机发言」决定，与本项无关</div>
        </el-form-item>
        <el-form-item label="AI 择机发言">
          <el-switch v-model="form.aiProactiveEnabled" />
          <div class="sub-hint">还需在「择机发言」页打开总开关才生效</div>
        </el-form-item>
        <el-form-item label="@ 识别码">
          <el-input v-model="form.botMentionOpenId" clearable placeholder="留空 = 认不出有人 @ 机器人" style="width: 420px" />
          <div class="sub-hint">
            开了群全量消息后 QQ 不再单独下发 @ 事件，只能靠消息正文里的 &lt;@识别码&gt; 判断有没有人 @ 它。
            填机器人在本群的 member openid（一串十六进制）。不填的话 @ 它会没人理；跨群通常是同一个值，可直接复制。
          </div>
        </el-form-item>
        <el-form-item label="人设">
          <el-select v-model="form.activeCharacter" clearable placeholder="留空用全局默认" style="width: 260px">
            <el-option v-for="c in characters" :key="c.name" :label="c.displayName" :value="c.name">
              <span>{{ c.displayName }}<el-tag v-if="c.isCustom" size="small" type="warning" style="margin-left: 6px">自定义</el-tag></span>
              <span style="float: right; color: #909399; font-size: 12px">{{ c.name }}</span>
            </el-option>
          </el-select>
        </el-form-item>
        <el-form-item label="群记忆摘要">
          <el-input v-model="form.groupMemory" type="textarea" :rows="5" maxlength="4000" show-word-limit
            placeholder="人工维护的群背景摘要，仅用于择机发言参考；留空可清空" />
          <div class="sub-hint">最多 4000 字。不会自动摘要或更新，也不会用于普通 @ 回复。</div>
        </el-form-item>
        <el-form-item label="群专属提示词">
          <el-input v-model="form.customSystemPrompt" type="textarea" :rows="3"
            placeholder="留空则用全局系统提示词" />
        </el-form-item>
        <el-form-item label="Temperature">
          <el-input-number v-model="form.customTemperature" :min="0" :max="2" :step="0.1" :precision="1"
            placeholder="留空用全局" />
          <el-button link type="primary" style="margin-left: 8px" @click="form.customTemperature = null">用全局</el-button>
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="dialog = false">取消</el-button>
        <el-button type="primary" :loading="saving" @click="save">保存</el-button>
      </template>
    </el-dialog>

    <el-card shadow="never" style="margin-top: 16px">
      <template #header><b>主动消息用量（自然月累计）</b></template>
      <el-table :data="quotas" size="small">
        <el-table-column prop="month" label="月份" width="100" />
        <el-table-column prop="scope" label="范围" width="90" />
        <el-table-column prop="key" label="目标" show-overflow-tooltip />
        <el-table-column prop="count" label="已用条数" width="90" />
        <el-table-column label="状态" width="90">
          <template #default="{ row }">
            <el-tag size="small" :type="row.rejected ? 'danger' : 'success'">{{ row.rejected ? '曾被拒' : '正常' }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column label="最后发送" width="170">
          <template #default="{ row }">{{ fmt(row.lastSentAt) }}</template>
        </el-table-column>
      </el-table>
    </el-card>
  </div>
</template>

<script setup>
import { ref, onMounted } from 'vue'
import { ElMessage } from 'element-plus'
import api from '../api'

const groups = ref([])
const quotas = ref([])
const characters = ref([])
const loading = ref(false)
const dialog = ref(false)
const saving = ref(false)
const form = ref(null)
const editingId = ref('')

const fmt = (s) => s ? new Date(s).toLocaleString('zh-CN', { hour12: false }) : '-'
const triggerText = (m) => m === 2 ? '围观全部' : '仅被 @'
const characterLabel = (name) => {
  if (!name) return '-'
  const c = characters.value.find((x) => x.name === name)
  return c ? c.displayName : name
}

const load = async () => {
  loading.value = true
  try {
    const [g, q, c] = await Promise.all([
      api.get('/groups'),
      api.get('/groups/quotas'),
      api.get('/config/characters')
    ])
    groups.value = g.data
    quotas.value = q.data
    characters.value = c.data
  } finally { loading.value = false }
}

const edit = async (row) => {
  editingId.value = row.groupOpenId
  const memory = await api.get(`/proactive/groups/${encodeURIComponent(editingId.value)}/memory`)
  form.value = {
    isEnabled: row.config?.isEnabled ?? true,
    groupMemory: memory.data.summary ?? '',
    triggerMode: row.config?.triggerMode ?? 0,
    aiProactiveEnabled: row.config?.aiProactiveEnabled ?? false,
    botMentionOpenId: row.config?.botMentionOpenId ?? '',
    activeCharacter: row.config?.activeCharacter ?? '',
    customSystemPrompt: row.config?.customSystemPrompt ?? '',
    customTemperature: row.config?.customTemperature ?? null
  }
  dialog.value = true
}

const save = async () => {
  saving.value = true
  try {
    const { groupMemory, ...config } = form.value
    const { data } = await api.put(`/groups/${editingId.value}/config`, config)
    if (groupMemory?.trim()) {
      await api.put(`/proactive/groups/${encodeURIComponent(editingId.value)}/memory`, { summary: groupMemory })
    } else {
      await api.delete(`/proactive/groups/${encodeURIComponent(editingId.value)}/memory`)
    }
    ElMessage.success(data.message)
    dialog.value = false
    load()
  } finally { saving.value = false }
}

onMounted(load)
</script>

<style scoped>
.card-head { display: flex; justify-content: space-between; align-items: center; }
.sub-hint { color: #909399; font-size: 12px; line-height: 1.5; }
</style>
