<template>
  <div>
    <el-row :gutter="16">
      <el-col :span="10">
        <el-card shadow="never">
          <template #header><b>修改密码</b></template>
          <el-form :model="pwd" label-width="90px">
            <el-form-item label="当前密码">
              <el-input v-model="pwd.current" type="password" show-password />
            </el-form-item>
            <el-form-item label="新密码">
              <el-input v-model="pwd.next" type="password" show-password placeholder="至少 8 位" />
            </el-form-item>
            <el-form-item label="确认新密码">
              <el-input v-model="pwd.confirm" type="password" show-password />
            </el-form-item>
            <el-form-item>
              <el-button type="primary" :loading="saving" @click="changePwd">确认修改</el-button>
              <el-button @click="logout">退出登录</el-button>
            </el-form-item>
          </el-form>
          <el-alert type="info" :closable="false" show-icon
            title="修改成功后所有会话将被清除，需要用新密码重新登录。" />
        </el-card>
      </el-col>

      <el-col :span="14">
        <el-card shadow="never">
          <template #header>
            <div class="card-head">
              <b>活跃会话</b>
              <el-button size="small" @click="loadSessions">刷新</el-button>
            </div>
          </template>
          <el-table :data="sessions" size="small">
            <el-table-column label="会话" min-width="120">
              <template #default="{ row }">
                <code>{{ row.tokenPrefix }}…</code>
                <el-tag v-if="row.isCurrent" size="small" type="success" style="margin-left: 6px">当前</el-tag>
              </template>
            </el-table-column>
            <el-table-column prop="remoteIp" label="来源 IP" width="130" />
            <el-table-column label="创建时间" width="160">
              <template #default="{ row }">{{ fmt(row.createdAt) }}</template>
            </el-table-column>
            <el-table-column label="最后活跃" width="160">
              <template #default="{ row }">{{ fmt(row.lastSeenAt) }}</template>
            </el-table-column>
            <el-table-column label="操作" width="90">
              <template #default="{ row }">
                <el-button v-if="!row.isCurrent" size="small" type="danger" plain @click="revoke(row)">注销</el-button>
              </template>
            </el-table-column>
          </el-table>
        </el-card>
      </el-col>
    </el-row>
  </div>
</template>

<script setup>
import { ref, onMounted } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import api from '../api'

const pwd = ref({ current: '', next: '', confirm: '' })
const saving = ref(false)
const sessions = ref([])

const fmt = (s) => s ? new Date(s).toLocaleString('zh-CN', { hour12: false }) : '-'

const loadSessions = async () => {
  const { data } = await api.get('/auth/sessions')
  sessions.value = data
}

const changePwd = async () => {
  if (!pwd.value.current || !pwd.value.next) return ElMessage.warning('请填写完整')
  if (pwd.value.next !== pwd.value.confirm) return ElMessage.warning('两次输入的新密码不一致')
  saving.value = true
  try {
    const { data } = await api.post('/auth/change-password', {
      currentPassword: pwd.value.current,
      newPassword: pwd.value.next,
      confirmPassword: pwd.value.confirm
    })
    ElMessageBox.alert(data.message || '密码修改成功', '提示', { type: 'success' })
      .finally(() => { location.href = '/login.html' })
  } finally { saving.value = false }
}

const revoke = async (row) => {
  await api.post('/auth/sessions/revoke', { tokenPrefix: row.tokenPrefix })
  ElMessage.success('会话已注销')
  loadSessions()
}

const logout = async () => {
  await api.post('/auth/logout').catch(() => {})
  location.href = '/login.html'
}

onMounted(loadSessions)
</script>

<style scoped>
.card-head { display: flex; justify-content: space-between; align-items: center; }
</style>
