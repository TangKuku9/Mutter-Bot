<template>
  <div v-loading="loadingGroups">
    <el-card shadow="never">
      <div class="toolbar">
        <span>选择群：</span>
        <el-select v-model="groupId" style="width: 340px" placeholder="请选择群" @change="loadAll">
          <el-option v-for="g in groups" :key="g" :label="g.slice(0, 12) + '…'" :value="g" />
        </el-select>
        <el-button :disabled="!groupId" @click="loadAll">刷新</el-button>
      </div>
    </el-card>

    <el-tabs v-model="tab" style="margin-top: 8px" v-if="groupId">
      <el-tab-pane label="排行榜" name="rank">
        <el-table :data="leaderboard" stripe v-loading="loadingRank">
          <el-table-column label="#" width="60" type="index" />
          <el-table-column prop="nickname" label="道号" width="160" />
          <el-table-column prop="realmName" label="境界" width="110">
            <template #default="{ row }"><el-tag size="small" effect="plain">{{ row.realmName }}</el-tag></template>
          </el-table-column>
          <el-table-column label="修为">
            <template #default="{ row }">{{ row.cultivationPoints.toLocaleString() }}</template>
          </el-table-column>
        </el-table>
      </el-tab-pane>

      <el-tab-pane label="事件审计日志" name="events">
        <el-table :data="events" size="small" v-loading="loadingEvents">
          <el-table-column label="时间" width="165">
            <template #default="{ row }">{{ fmt(row.createdAt) }}</template>
          </el-table-column>
          <el-table-column prop="eventType" label="类型" width="140">
            <template #default="{ row }"><el-tag size="small" effect="plain">{{ row.eventType }}</el-tag></template>
          </el-table-column>
          <el-table-column prop="memberOpenId" label="玩家" width="120" show-overflow-tooltip>
            <template #default="{ row }">{{ row.memberOpenId ? row.memberOpenId.slice(0, 8) + '…' : '（群）' }}</template>
          </el-table-column>
          <el-table-column prop="detailJson" label="详情" show-overflow-tooltip />
          <el-table-column label="AI 输出" width="80">
            <template #default="{ row }">
              <el-popover v-if="row.aiRawOutput" width="420" trigger="click">
                <template #reference><el-button size="small" link type="primary">查看</el-button></template>
                <pre class="json-pre">{{ pretty(row.aiRawOutput) }}</pre>
              </el-popover>
            </template>
          </el-table-column>
        </el-table>
      </el-tab-pane>

      <el-tab-pane label="世界状态" name="world">
        <el-descriptions :column="2" border v-if="world">
          <el-descriptions-item label="灵气浓度">Lv.{{ world.worldQiLevel }}（挂机 ×{{ world.worldQiFactor.toFixed(1) }}）</el-descriptions-item>
          <el-descriptions-item label="上次事件生成">{{ fmt(world.lastEventGeneratedAt) }}</el-descriptions-item>
          <el-descriptions-item label="待决事件" :span="2">
            <span v-if="!world.pendingEventJson">无</span>
            <span v-else>
              有（{{ pendingTitle }}），到期：{{ fmt(world.pendingEventExpiresAt) }}
              <el-tag v-if="pendingExpired" type="danger" size="small">已过期</el-tag>
            </span>
          </el-descriptions-item>
          <el-descriptions-item label="渡劫会话" :span="2">
            <span v-if="!world.tribulationSession">无</span>
            <span v-else>
              {{ world.tribulationSession.nickname }} 正在渡劫（护法 {{ world.tribulationSession.protectorCount }} 人，
              加成 +{{ (world.tribulationSession.bonus * 100).toFixed(0) }}%）
              <el-tag v-if="world.tribulationSession.isExpired" type="warning" size="small">已过期·悬挂</el-tag>
            </span>
          </el-descriptions-item>
        </el-descriptions>
        <div style="margin-top: 14px">
          <el-popconfirm title="确定清理该群的渡劫会话？" @confirm="clearTribulation">
            <template #reference>
              <el-button type="warning" :disabled="!world?.tribulationSession">清理渡劫会话</el-button>
            </template>
          </el-popconfirm>
          <span class="hint">用于处理卡死的悬挂会话（对应以前的手工 SQL 清理）。</span>
        </div>
      </el-tab-pane>

      <el-tab-pane label="玩家运维" name="players">
        <el-table :data="players" size="small" v-loading="loadingPlayers">
          <el-table-column prop="nickname" label="道号" width="140" />
          <el-table-column prop="realmName" label="境界" width="90" />
          <el-table-column label="修为" width="110">
            <template #default="{ row }">{{ row.cultivationPoints.toLocaleString() }}</template>
          </el-table-column>
          <el-table-column label="灵石" width="90">
            <template #default="{ row }">{{ row.spiritStones.toLocaleString() }}</template>
          </el-table-column>
          <el-table-column prop="lifespan" label="寿元" width="70" />
          <el-table-column label="最后更新" width="160">
            <template #default="{ row }">{{ fmt(row.updatedAt) }}</template>
          </el-table-column>
          <el-table-column prop="memberOpenId" label="OpenId" show-overflow-tooltip />
          <el-table-column label="操作" width="110">
            <template #default="{ row }">
              <el-popconfirm :title="`确定重置 ${row.nickname} 的全部冷却？`" @confirm="resetCd(row)">
                <template #reference>
                  <el-button size="small" type="warning" plain>重置冷却</el-button>
                </template>
              </el-popconfirm>
            </template>
          </el-table-column>
        </el-table>
      </el-tab-pane>

      <el-tab-pane label="数据库备份" name="backup">
        <el-button type="primary" :loading="backing" @click="backup">立即备份</el-button>
        <el-button @click="loadBackups">刷新列表</el-button>
        <el-table :data="backups" size="small" style="margin-top: 12px">
          <el-table-column prop="name" label="备份文件" />
          <el-table-column prop="sizeMb" label="大小(MB)" width="110" />
          <el-table-column label="时间" width="180">
            <template #default="{ row }">{{ fmt(row.lastWriteTime) }}</template>
          </el-table-column>
        </el-table>
      </el-tab-pane>

      <el-tab-pane label="富交互" name="richui">
        <el-button :loading="loadingUi" @click="loadUi">刷新能力态</el-button>
        <span class="hint">能力态由平台实际返回错误码驱动，被拒后按 TTL 自动重试探测。</span>
        <el-descriptions :column="2" border style="margin-top: 12px" v-if="ui">
          <el-descriptions-item label="总开关">
            <el-tag size="small" :type="ui.configEnabled ? 'success' : 'info'">{{ ui.configEnabled ? '已启用' : '已关闭（纯文本）' }}</el-tag>
          </el-descriptions-item>
          <el-descriptions-item label="配置键盘模式">{{ ui.configuredKeyboardMode }}</el-descriptions-item>
          <el-descriptions-item label="Markdown 能力">
            <el-tag size="small" :type="capTagType(ui.markdownState)">{{ capText(ui.markdownState) }}</el-tag>
            <span v-if="ui.markdownDeniedCode" class="hint">err={{ ui.markdownDeniedCode }} @ {{ fmt(ui.markdownDeniedAt) }}</span>
          </el-descriptions-item>
          <el-descriptions-item label="按钮组件能力">
            <el-tag size="small" :type="capTagType(ui.keyboardState)">{{ capText(ui.keyboardState) }}</el-tag>
            <span v-if="ui.keyboardDeniedCode" class="hint">err={{ ui.keyboardDeniedCode }} @ {{ fmt(ui.keyboardDeniedAt) }}</span>
          </el-descriptions-item>
          <el-descriptions-item label="文字链替身">{{ ui.cmdInputLinks ? '开' : '关' }}</el-descriptions-item>
          <el-descriptions-item label="@ 真实玩家">{{ ui.atUserTags ? '开' : '关' }}</el-descriptions-item>
          <el-descriptions-item label="引用回复">{{ ui.messageReference ? '开' : '关' }}</el-descriptions-item>
          <el-descriptions-item label="INTERACTION 事件">{{ ui.interactionIntent ? '已订阅（需平台授权）' : '未订阅' }}</el-descriptions-item>
          <el-descriptions-item label="灰度白名单" :span="2">
            <span v-if="!ui.groupAllowList || !ui.groupAllowList.length">未配置（全部群生效）</span>
            <el-tag v-for="g in ui.groupAllowList" :key="g" size="small" effect="plain" style="margin-right: 6px">{{ g.slice(0, 10) }}…</el-tag>
          </el-descriptions-item>
          <el-descriptions-item label="发送计数" :span="2">
            富消息 {{ ui.counters.rich }}、markdown {{ ui.counters.markdown }}、带按钮 {{ ui.counters.keyboard }}、
            文字链 {{ ui.counters.links }}、降级重发 {{ ui.counters.degradeRetries }}、回落纯文本 {{ ui.counters.plainFallback }}
          </el-descriptions-item>
        </el-descriptions>
        <el-alert v-if="ui" type="info" :closable="false" show-icon style="margin-top: 12px" :title="ui.hint" />
      </el-tab-pane>
    </el-tabs>
  </div>
</template>

<script setup>
import { ref, computed, onMounted } from 'vue'
import { ElMessage } from 'element-plus'
import api from '../api'

const tab = ref('rank')
const groups = ref([])
const groupId = ref('')
const loadingGroups = ref(false)
const leaderboard = ref([])
const loadingRank = ref(false)
const events = ref([])
const loadingEvents = ref(false)
const world = ref(null)
const players = ref([])
const loadingPlayers = ref(false)
const backups = ref([])
const backing = ref(false)
const ui = ref(null)
const loadingUi = ref(false)

const capText = (s) => ({ Ok: '可用', Denied: '被拒（已降级）', Unknown: '未探测' }[s] || s)
const capTagType = (s) => ({ Ok: 'success', Denied: 'danger', Unknown: 'info' }[s] || 'info')

const fmt = (s) => s ? new Date(s).toLocaleString('zh-CN', { hour12: false }) : '-'
const pretty = (s) => { try { return JSON.stringify(JSON.parse(s), null, 2) } catch { return s } }

const pendingTitle = computed(() => {
  try { return JSON.parse(world.value?.pendingEventJson || '{}').Title || '未知事件' } catch { return '未知事件' }
})
const pendingExpired = computed(() =>
  world.value?.pendingEventExpiresAt && new Date(world.value.pendingEventExpiresAt) < new Date())

const loadGroups = async () => {
  loadingGroups.value = true
  try {
    const { data } = await api.get('/cultivation/groups')
    groups.value = data
    if (data.length && !groupId.value) { groupId.value = data[0]; loadAll() }
  } finally { loadingGroups.value = false }
}

const loadAll = async () => {
  if (!groupId.value) return
  loadingRank.value = loadingEvents.value = loadingPlayers.value = true
  try {
    const [lb, ev, wd, pl] = await Promise.all([
      api.get(`/cultivation/leaderboard/${groupId.value}`),
      api.get(`/cultivation/events/${groupId.value}`),
      api.get(`/cultivation/world/${groupId.value}`).catch(() => ({ data: null })),
      api.get(`/cultivation/players/${groupId.value}`)
    ])
    leaderboard.value = lb.data
    events.value = ev.data
    world.value = wd.data
    players.value = pl.data
  } finally { loadingRank.value = loadingEvents.value = loadingPlayers.value = false }
  loadBackups()
}

const loadUi = async () => {
  loadingUi.value = true
  try {
    const { data } = await api.get('/cultivation/ui/capabilities')
    ui.value = data
  } catch (e) {
    ElMessage.error('读取富交互能力态失败：' + (e?.response?.status || e?.message))
  } finally { loadingUi.value = false }
}

const loadBackups = async () => {
  const { data } = await api.get('/cultivation/backups')
  backups.value = data
}

const clearTribulation = async () => {
  const { data } = await api.post(`/cultivation/world/${groupId.value}/clear-tribulation`)
  ElMessage.success(data.message)
  loadAll()
}

const resetCd = async (row) => {
  const { data } = await api.post('/cultivation/cooldowns/reset', {
    groupId: groupId.value, memberOpenId: row.memberOpenId
  })
  ElMessage.success(`${row.nickname}：${data.message}`)
}

const backup = async () => {
  backing.value = true
  try {
    const { data } = await api.post('/cultivation/backup')
    ElMessage.success(`${data.message}：${data.file}`)
    loadBackups()
  } finally { backing.value = false }
}

onMounted(() => { loadGroups(); loadUi() })
</script>

<style scoped>
.toolbar { display: flex; align-items: center; gap: 10px; }
.hint { color: #909399; font-size: 12px; margin-left: 10px; }
.json-pre { max-height: 360px; overflow: auto; font-size: 12px; margin: 0; }
</style>
