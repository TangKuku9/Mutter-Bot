<template>
  <el-dialog v-model="open" title="本群表情包" :width="narrow ? 'calc(100% - 24px)' : '680px'" append-to-body>
    <div v-loading="loading" class="sticker-list">
      <el-empty v-if="!loading && !failed && !rows.length" description="还没有收藏到静态表情包" />
      <el-alert v-if="failed" title="图库读取失败" type="error" :closable="false" />
      <div v-for="row in rows" :key="row.hash" class="sticker-row">
        <img :src="'/api' + endpoint + '/' + row.hash + '/image'" :alt="row.label || '未命名表情'" loading="lazy" />
        <div class="sticker-details">
          <el-input v-model="row.label" maxlength="60" placeholder="填写描述，例如：开心鼓掌" aria-label="表情描述" />
          <span>出现 {{ row.seenCount }} 次 · 已发送 {{ row.sentCount }} 次</span>
        </div>
        <div class="sticker-actions">
          <el-switch v-model="row.isEnabled" aria-label="允许 AI 选用此表情" active-text="可选用" />
          <el-button size="small" type="primary" plain :loading="row.busy" @click="save(row)">保存</el-button>
          <el-popconfirm title="从本群图库移除？下次收到时仍可自动收藏。" width="230" @confirm="remove(row)">
            <template #reference>
              <el-button size="small" type="danger" link :disabled="row.busy">删除</el-button>
            </template>
          </el-popconfirm>
        </div>
      </div>
    </div>
    <template #footer>
      <el-button :loading="loading" @click="load">刷新</el-button>
      <el-button @click="open = false">关闭</el-button>
    </template>
  </el-dialog>
</template>

<script setup>
import { computed, ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import api from '../api'
import { useMediaQuery } from '../useMediaQuery'

const open = defineModel({ type: Boolean, default: false })
const props = defineProps({ groupId: { type: String, required: true } })
const narrow = useMediaQuery('(max-width: 600px)')
const rows = ref([])
const loading = ref(false)
const failed = ref(false)
const endpoint = computed(() => `/groups/${encodeURIComponent(props.groupId)}/stickers`)
let requestNumber = 0

async function load() {
  if (!open.value || !props.groupId) return
  const current = ++requestNumber
  loading.value = true
  failed.value = false
  try {
    const { data } = await api.get(endpoint.value)
    if (current === requestNumber) rows.value = data
  } catch {
    if (current === requestNumber) failed.value = true
  } finally {
    if (current === requestNumber) loading.value = false
  }
}

async function save(row) {
  row.busy = true
  try {
    await api.put(`${endpoint.value}/${row.hash}`, { label: row.label, isEnabled: row.isEnabled })
    ElMessage.success('表情已保存')
  } catch {
    // API 已显示错误，保留编辑内容供重试。
  } finally {
    row.busy = false
  }
}

async function remove(row) {
  row.busy = true
  try {
    const path = endpoint.value
    await api.delete(`${path}/${row.hash}`)
    if (endpoint.value === path) rows.value = rows.value.filter(item => item.hash !== row.hash)
  } catch {
    // API 已显示错误。
  } finally {
    row.busy = false
  }
}

watch([open, () => props.groupId], () => {
  requestNumber++
  rows.value = []
  if (open.value) load()
})
</script>

<style scoped>
.sticker-list { max-height: 58vh; overflow-y: auto; min-height: 72px; }
.sticker-row {
  display: grid;
  grid-template-columns: 52px minmax(0, 1fr) auto;
  align-items: center;
  gap: 12px;
  padding: 10px 2px;
  border-bottom: 1px solid var(--el-border-color-lighter);
}
.sticker-row img { width: 52px; height: 52px; object-fit: contain; border-radius: 6px; background: var(--el-fill-color-light); }
.sticker-details { display: grid; gap: 5px; }
.sticker-details span { font-size: 12px; color: var(--el-text-color-secondary); }
.sticker-actions { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; }
.sticker-actions :deep(.el-button) { margin-left: 0; }
@media (max-width: 600px) {
  .sticker-row { grid-template-columns: 44px minmax(0, 1fr); gap: 8px; }
  .sticker-row img { width: 44px; height: 44px; }
  .sticker-actions { grid-column: 2; }
}
</style>
