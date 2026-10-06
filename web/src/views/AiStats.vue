<template>
  <div>
    <el-card shadow="never">
      <div class="toolbar">
        <span>统计范围：</span>
        <el-radio-group v-model="days" @change="load">
          <el-radio-button :value="1">1 天</el-radio-button>
          <el-radio-button :value="7">7 天</el-radio-button>
          <el-radio-button :value="30">30 天</el-radio-button>
          <el-radio-button :value="90">90 天</el-radio-button>
        </el-radio-group>
      </div>
    </el-card>

    <el-row :gutter="16" style="margin-top: 16px">
      <el-col :span="12">
        <el-card shadow="never">
          <template #header><b>每日 Token 用量</b></template>
          <el-table :data="daily" size="small" max-height="420">
            <el-table-column prop="date" label="日期" width="120" />
            <el-table-column prop="callCount" label="调用次数" width="100" />
            <el-table-column label="Token">
              <template #default="{ row }">{{ (row.totalTokens ?? 0).toLocaleString() }}</template>
            </el-table-column>
            <el-table-column label="占比">
              <template #default="{ row }">
                <el-progress :percentage="pct(row.totalTokens)" :show-text="false" :stroke-width="8" />
              </template>
            </el-table-column>
          </el-table>
        </el-card>
      </el-col>
      <el-col :span="12">
        <el-card shadow="never">
          <template #header><b>群 Token 排行（Top {{ groups.length }}）</b></template>
          <el-table :data="groups" size="small" max-height="420">
            <el-table-column prop="groupId" label="群" show-overflow-tooltip />
            <el-table-column prop="callCount" label="调用" width="80" />
            <el-table-column label="Token" width="120">
              <template #default="{ row }">{{ (row.totalTokens ?? 0).toLocaleString() }}</template>
            </el-table-column>
          </el-table>
        </el-card>
      </el-col>
    </el-row>

    <el-card shadow="never" style="margin-top: 16px">
      <template #header><b>用户 Token 排行（Top {{ users.length }}）</b></template>
      <el-table :data="users" size="small">
        <el-table-column prop="userName" label="用户" width="180" />
        <el-table-column prop="userId" label="OpenId" show-overflow-tooltip />
        <el-table-column prop="callCount" label="调用" width="80" />
        <el-table-column label="Token" width="120">
          <template #default="{ row }">{{ (row.totalTokens ?? 0).toLocaleString() }}</template>
        </el-table-column>
      </el-table>
    </el-card>
  </div>
</template>

<script setup>
import { ref, onMounted } from 'vue'
import api from '../api'

const days = ref(7)
const daily = ref([])
const groups = ref([])
const users = ref([])

const maxToken = () => Math.max(1, ...daily.value.map((d) => d.totalTokens ?? 0))
const pct = (v) => Math.round(((v ?? 0) / maxToken()) * 100)

const load = async () => {
  const [d, g, u] = await Promise.all([
    api.get('/chat/token-stats', { params: { days: days.value } }),
    api.get('/chat/token-stats/groups', { params: { days: days.value, top: 10 } }),
    api.get('/chat/token-stats/users', { params: { days: days.value, top: 10 } })
  ])
  daily.value = [...(d.data || [])].reverse()
  groups.value = g.data || []
  users.value = u.data || []
}

onMounted(() => load().catch(() => {}))
</script>

<style scoped>
.toolbar { display: flex; align-items: center; gap: 10px; }
</style>
