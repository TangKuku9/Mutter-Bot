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

    <el-dialog v-model="dialog" width="560px">
      <template #header>
        <div class="dialog-head">
          <b>群配置</b>
          <el-button size="small" @click="openHelp('isEnabled')">参数说明</el-button>
        </div>
      </template>
      <el-form :model="form" label-width="110px" v-if="form">
        <el-form-item label="启用响应">
          <el-switch v-model="form.isEnabled" />
          <ParamHelpButton :label="helpLabel('isEnabled')" @click="openHelp('isEnabled')" />
        </el-form-item>
        <el-form-item label="围观范围">
          <el-select v-model="form.triggerMode" style="width: 280px">
            <el-option :value="0" label="只记录被 @ 的消息" />
            <el-option :value="2" label="围观并记录全部消息" />
          </el-select>
          <ParamHelpButton :label="helpLabel('triggerMode')" @click="openHelp('triggerMode')" />
        </el-form-item>
        <el-form-item label="AI 择机发言">
          <el-switch v-model="form.aiProactiveEnabled" />
          <ParamHelpButton :label="helpLabel('aiProactiveEnabled')" @click="openHelp('aiProactiveEnabled')" />
        </el-form-item>
        <el-form-item label="@ 识别码">
          <el-input v-model="form.botMentionOpenId" clearable placeholder="留空 = 认不出有人 @ 机器人" style="width: 355px" />
          <ParamHelpButton :label="helpLabel('botMentionOpenId')" @click="openHelp('botMentionOpenId')" />
        </el-form-item>
        <el-form-item label="人设">
          <el-select v-model="form.activeCharacter" clearable placeholder="留空用全局默认" style="width: 260px">
            <el-option v-for="c in characters" :key="c.name" :label="c.displayName" :value="c.name">
              <span>{{ c.displayName }}<el-tag v-if="c.isCustom" size="small" type="warning" style="margin-left: 6px">自定义</el-tag></span>
              <span style="float: right; color: #909399; font-size: 12px">{{ c.name }}</span>
            </el-option>
          </el-select>
          <ParamHelpButton :label="helpLabel('activeCharacter')" @click="openHelp('activeCharacter')" />
        </el-form-item>
        <el-form-item label="群记忆摘要">
          <el-input v-model="form.groupMemory" type="textarea" :rows="5" maxlength="4000" show-word-limit
            placeholder="人工维护的群背景摘要，仅用于择机发言参考；留空可清空" />
          <ParamHelpButton :label="helpLabel('groupMemory')" @click="openHelp('groupMemory')" />
          <!-- 下面两行是当前状态，不是参数说明，留在原地 -->
          <div class="sub-hint" v-if="memoryUnavailable">摘要读取失败，本次保存不会改动群记忆；关闭后重新打开可重试。</div>
          <div class="sub-hint" v-else-if="memoryUpdatedAt">上次更新 {{ fmt(memoryUpdatedAt) }}</div>
        </el-form-item>
        <el-form-item label="本群记忆注入">
          <el-select v-model="form.memoryInjectionEnabled" clearable placeholder="跟随全局" style="width: 260px">
            <el-option :value="true" label="开启：本群注入" />
            <el-option :value="false" label="关闭：本群不注入" />
          </el-select>
          <ParamHelpButton :label="helpLabel('memoryInjectionEnabled')" @click="openHelp('memoryInjectionEnabled')" />
        </el-form-item>
        <el-form-item label="群专属提示词">
          <el-input v-model="form.customSystemPrompt" type="textarea" :rows="3"
            placeholder="留空则用全局系统提示词" />
          <ParamHelpButton :label="helpLabel('customSystemPrompt')" @click="openHelp('customSystemPrompt')" />
        </el-form-item>
        <el-form-item label="Temperature">
          <el-input-number v-model="form.customTemperature" :min="0" :max="2" :step="0.1" :precision="1"
            placeholder="留空用全局" />
          <el-button link type="primary" style="margin-left: 8px" @click="form.customTemperature = null">用全局</el-button>
          <ParamHelpButton :label="helpLabel('customTemperature')" @click="openHelp('customTemperature')" />
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

    <ParamHelpDrawer v-model="helpVisible" :title="helpTitle" :lines="helpLines" :current="helpCurrent"
      :index="helpIndex" :total="helpTotal" @step="stepHelp" />
  </div>
</template>

<script setup>
import { ref, onMounted } from 'vue'
import { ElMessage } from 'element-plus'
import api from '../api'
import { useParamHelp } from '../paramHelp'
import ParamHelpButton from '../components/ParamHelpButton.vue'
import ParamHelpDrawer from '../components/ParamHelpDrawer.vue'

const groups = ref([])
const quotas = ref([])
const characters = ref([])
const loading = ref(false)
const dialog = ref(false)
const saving = ref(false)
const form = ref(null)
const editingId = ref('')
const memoryUnavailable = ref(false)
const memoryUpdatedAt = ref(null)

const fmt = (s) => s ? new Date(s).toLocaleString('zh-CN', { hour12: false }) : '-'
const triggerText = (m) => m === 2 ? '围观全部' : '仅被 @'
const characterLabel = (name) => {
  if (!name) return '-'
  const c = characters.value.find((x) => x.name === name)
  return c ? c.displayName : name
}

const HELP_ORDER = [
  'isEnabled', 'triggerMode', 'aiProactiveEnabled', 'botMentionOpenId', 'activeCharacter',
  'groupMemory', 'memoryInjectionEnabled', 'customSystemPrompt', 'customTemperature'
]

const HELP = {
  isEnabled: {
    title: '启用响应',
    value: (f) => (f.isEnabled ? '启用' : '停用'),
    lines: [
      '关掉后本群完全不响应：@ 它也不回，也不再围观采集。',
      '想临时静音一个群用这个，不用把机器人退群。'
    ]
  },
  triggerMode: {
    title: '围观范围',
    value: (f) => (f.triggerMode === 2 ? '围观并记录全部消息' : '只记录被 @ 的消息'),
    lines: [
      '只记录被 @ 的消息：群友的普通发言不落库，但择机窗口照旧采集，只是留在内存里。',
      '围观并记录全部消息：普通发言会落库，之后能在「聊天历史」里翻到。',
      '这项只管落不落库，要不要接话由「择机发言」决定。'
    ]
  },
  aiProactiveEnabled: {
    title: 'AI 择机发言',
    value: (f) => (f.aiProactiveEnabled ? '参与' : '不参与'),
    lines: [
      '本群是否参与择机发言。参与才会把群友的普通发言送进评估窗口。',
      '还要在「择机发言」页打开总开关，两处都开才生效。'
    ]
  },
  botMentionOpenId: {
    title: '@ 识别码',
    value: (f) => (f.botMentionOpenId ? '已填写' : '未填写'),
    lines: [
      '开了「围观并记录全部消息」后，QQ 不再单独下发 @ 事件，只能靠消息正文里的 <@识别码> 判断有没有人 @ 它。',
      '填机器人在本群的 member openid，一串十六进制。跨群通常是同一个值，可以直接复制。',
      '留空的后果是有人 @ 它也没人理。'
    ]
  },
  activeCharacter: {
    title: '人设',
    value: (f) => characters.value.find((c) => c.name === f.activeCharacter)?.displayName || '跟随全局',
    lines: [
      '留空用「AI 设置」里的全局人设。',
      '这里选了就优先用本群的，盖住全局那一项。'
    ]
  },
  groupMemory: {
    title: '群记忆摘要',
    value: (f) => (f.groupMemory ? `${f.groupMemory.length} 字` : '空'),
    lines: [
      '人工维护的群背景，只在择机发言判定时作为参考带上。',
      '不会自动摘要或更新，也不会用于普通 @ 回复。',
      '最多 4000 字。清空并保存就是删除本群记忆。'
    ]
  },
  memoryInjectionEnabled: {
    title: '本群记忆注入',
    value: (f) => (f.memoryInjectionEnabled === true ? '开启' : f.memoryInjectionEnabled === false ? '关闭' : '跟随全局'),
    lines: [
      '留空 = 跟随「择机发言」页的全局开关。',
      '关闭只停本群的注入，上面的摘要仍原样保留。'
    ]
  },
  customSystemPrompt: {
    title: '群专属提示词',
    value: (f) => (f.customSystemPrompt ? `${f.customSystemPrompt.length} 字` : '跟随全局'),
    lines: [
      '留空用「AI 设置」里的全局系统提示词。',
      '填了就整段替换掉全局的，不会拼接在后面。'
    ]
  },
  customTemperature: {
    title: 'Temperature',
    value: (f) => (f.customTemperature == null ? '跟随全局' : `${f.customTemperature}`),
    lines: [
      '留空用「AI 设置」里的全局 Temperature。',
      '填了就只作用于本群的 @ 回复。'
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
  const id = editingId.value
  memoryUnavailable.value = false
  memoryUpdatedAt.value = null
  form.value = {
    isEnabled: row.config?.isEnabled ?? true,
    groupMemory: '',
    memoryInjectionEnabled: row.config?.memoryInjectionEnabled ?? null,
    triggerMode: row.config?.triggerMode ?? 0,
    aiProactiveEnabled: row.config?.aiProactiveEnabled ?? false,
    botMentionOpenId: row.config?.botMentionOpenId ?? '',
    activeCharacter: row.config?.activeCharacter ?? '',
    customSystemPrompt: row.config?.customSystemPrompt ?? '',
    customTemperature: row.config?.customTemperature ?? null
  }
  dialog.value = true
  try {
    const { data } = await api.get(`/proactive/groups/${encodeURIComponent(id)}/memory`)
    if (editingId.value !== id) return
    form.value.groupMemory = data.summary ?? ''
    // 空摘要回的是 DateTime.MinValue，显示成"上次更新 0001 年"没有意义
    memoryUpdatedAt.value = data.summary ? (data.updatedAt ?? null) : null
  } catch {
    // 摘要拉取失败不该连群配置都改不了：弹窗照常可用，只是本次不碰群记忆
    if (editingId.value === id) memoryUnavailable.value = true
  }
}

const save = async () => {
  saving.value = true
  try {
    const { groupMemory, ...config } = form.value
    // el-select 清空给的是空串，而服务端这一列是可空 bool：空串会直接 400
    config.memoryInjectionEnabled =
      config.memoryInjectionEnabled === '' || config.memoryInjectionEnabled == null
        ? null
        : config.memoryInjectionEnabled
    const { data } = await api.put(`/groups/${editingId.value}/config`, config)
    // 读取失败时跳过，避免拿空串把已有摘要误清空；空串交给服务端当清空处理
    if (!memoryUnavailable.value) {
      await api.put(`/proactive/groups/${encodeURIComponent(editingId.value)}/memory`, { summary: groupMemory })
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
.dialog-head { display: flex; justify-content: space-between; align-items: center; }
/* 摘要读取失败与上次更新是当前状态，不是参数说明 */
.sub-hint { color: #909399; font-size: 12px; line-height: 1.5; }
/* 整行控件给右边的「?」留出位置，否则宽度占满会把按钮挤到下一行 */
.el-form-item__content > .el-input,
.el-form-item__content > .el-textarea { width: calc(100% - 32px); }
</style>
