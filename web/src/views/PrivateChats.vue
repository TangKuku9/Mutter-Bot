<template>
  <el-card shadow="never">
    <template #header>
      <div class="card-head">
        <b>私聊白名单</b>
        <div>
          <el-button size="small" @click="load">刷新</el-button>
          <el-button size="small" type="primary" @click="dialog = true">添加用户</el-button>
        </div>
      </div>
    </template>

    <el-alert type="info" :closable="false" show-icon style="margin-bottom: 12px"
      title="仅白名单内且启用的用户私聊会被响应；标记管理员后可在私聊中使用 / 开头的内置命令。" />

    <el-table :data="list" v-loading="loading" stripe>
      <el-table-column prop="nickname" label="昵称" width="160">
        <template #default="{ row }">{{ row.nickname || '（未知）' }}</template>
      </el-table-column>
      <el-table-column prop="userId" label="用户 OpenId" show-overflow-tooltip>
        <template #default="{ row }"><code>{{ row.userId }}</code></template>
      </el-table-column>
      <el-table-column label="启用" width="90">
        <template #default="{ row }">
          <el-switch v-model="row.isEnabled" @change="toggle(row)" />
        </template>
      </el-table-column>
      <el-table-column label="管理员" width="90">
        <template #default="{ row }">
          <el-switch v-model="row.isAdmin" @change="toggle(row)" />
        </template>
      </el-table-column>
      <el-table-column label="操作" width="90">
        <template #default="{ row }">
          <el-popconfirm :title="`确定移除 ${row.nickname || row.userId}？`" @confirm="remove(row)">
            <template #reference>
              <el-button size="small" type="danger" plain>移除</el-button>
            </template>
          </el-popconfirm>
        </template>
      </el-table-column>
    </el-table>

    <el-dialog v-model="dialog" title="添加白名单用户" width="460px">
      <el-form label-width="90px">
        <el-form-item label="用户 OpenId">
          <el-input v-model="newId" placeholder="从聊天记录中复制用户的 OpenId" />
        </el-form-item>
        <el-form-item label="设为管理员">
          <el-switch v-model="newAdmin" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="dialog = false">取消</el-button>
        <el-button type="primary" :loading="saving" @click="add">添加</el-button>
      </template>
    </el-dialog>
  </el-card>
</template>

<script setup>
import { ref, onMounted } from 'vue'
import { ElMessage } from 'element-plus'
import api from '../api'

const list = ref([])
const loading = ref(false)
const dialog = ref(false)
const saving = ref(false)
const newId = ref('')
const newAdmin = ref(false)

const load = async () => {
  loading.value = true
  try {
    const { data } = await api.get('/groups/private-chats')
    list.value = data
  } finally { loading.value = false }
}

const toggle = async (row) => {
  await api.put(`/groups/private-chats/${row.userId}`, { isEnabled: row.isEnabled, isAdmin: row.isAdmin })
  ElMessage.success('已保存')
}

const add = async () => {
  if (!newId.value.trim()) return ElMessage.warning('请填写用户 OpenId')
  saving.value = true
  try {
    await api.put(`/groups/private-chats/${newId.value.trim()}`, { isEnabled: true, isAdmin: newAdmin.value })
    ElMessage.success('已添加')
    dialog.value = false
    newId.value = ''
    newAdmin.value = false
    load()
  } finally { saving.value = false }
}

const remove = async (row) => {
  await api.delete(`/groups/private-chats/${row.userId}`)
  ElMessage.success('已移除')
  load()
}

onMounted(load)
</script>

<style scoped>
.card-head { display: flex; justify-content: space-between; align-items: center; }
</style>
