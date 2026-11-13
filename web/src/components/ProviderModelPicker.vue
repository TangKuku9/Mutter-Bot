<template>
  <div class="provider-model-picker">
    <el-form-item label="服务商" :for="`${idPrefix}-provider`">
      <el-select :id="`${idPrefix}-provider`" :model-value="connectionId" filterable
        :disabled="disabled" placeholder="选择已保存的服务商连接" @change="selectConnection">
        <el-option v-for="item in connections" :key="item.id" :value="item.id" :label="item.name">
          <span>{{ item.name }}</span><small class="picker-provider-meta">{{ item.models?.length || 0 }} 个模型</small>
        </el-option>
      </el-select>
    </el-form-item>
    <el-form-item label="模型名称" :for="`${idPrefix}-model`">
      <el-select :id="`${idPrefix}-model`" :model-value="modelName" filterable
        :disabled="disabled || !connection" placeholder="搜索或选择模型" @change="selectModel">
        <el-option-group v-for="group in groups" :key="group.label" :label="group.label">
          <el-option v-for="model in group.models" :key="model.id" :label="model.name || model.id" :value="model.id">
            <div class="picker-model-option">
              <span><strong>{{ model.name || model.id }}</strong><small>{{ model.id }}</small></span>
              <button type="button" class="picker-star" :class="{ active: isFavorite(model.id) }"
                :aria-label="`${isFavorite(model.id) ? '取消收藏' : '收藏'} ${model.id}`"
                @mousedown.prevent @click.stop.prevent="toggleFavorite(model.id)">{{ isFavorite(model.id) ? '★' : '☆' }}</button>
            </div>
          </el-option>
        </el-option-group>
      </el-select>
    </el-form-item>
    <p v-if="connection" class="picker-summary">
      <el-tag size="small" type="info">{{ connection.protocol === 'gemini' ? 'Gemini 原生' : 'OpenAI 兼容' }}</el-tag>
      <span>{{ connection.hasApiKey ? '密钥已配置' : '未设置 API Key' }}</span>
    </p>
    <p v-else class="picker-summary">添加服务商连接后，即可在配置和预设中重复选用。</p>
  </div>
</template>

<script setup>
import { computed, ref } from 'vue'
import { readModelPreferences, saveModelPreferences, rememberModel, preferredModel, groupConnectionModels } from '../aiConnections'

const props = defineProps({
  connections: { type: Array, default: () => [] }, connectionId: { type: String, default: '' },
  modelName: { type: String, default: '' }, idPrefix: { type: String, default: 'ai' }, disabled: Boolean
})
const emit = defineEmits(['update:connectionId', 'update:modelName', 'change'])
const preferences = ref(readModelPreferences())
const connection = computed(() => props.connections.find(item => item.id === props.connectionId))
const groups = computed(() => groupConnectionModels(connection.value, preferences.value))
const isFavorite = modelId => preferences.value.favorites.some(item => item.connectionId === props.connectionId && item.modelId === modelId)
const toggleFavorite = modelId => {
  const favorites = preferences.value.favorites.filter(item => !(item.connectionId === props.connectionId && item.modelId === modelId))
  if (!isFavorite(modelId)) favorites.push({ connectionId: props.connectionId, modelId })
  preferences.value = { ...preferences.value, favorites }
  saveModelPreferences(preferences.value)
}
const select = (selected, modelId) => {
  emit('update:connectionId', selected.id)
  emit('update:modelName', modelId)
  preferences.value = rememberModel(preferences.value, selected.id, modelId)
  saveModelPreferences(preferences.value)
  emit('change', { connection: selected, model: selected.models.find(model => model.id === modelId) })
}
const selectConnection = id => {
  preferences.value = readModelPreferences()
  const selected = props.connections.find(item => item.id === id)
  if (selected) select(selected, preferredModel(selected, preferences.value))
}
const selectModel = id => { if (connection.value) select(connection.value, id) }
</script>

<style scoped>
.provider-model-picker :deep(.el-select) { width: 100%; }
.picker-summary { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; margin: 0; color: var(--el-text-color-secondary); font-size: 12px; }
.picker-provider-meta { float: right; margin-left: 16px; color: var(--el-text-color-secondary); }
.picker-model-option { display: flex; align-items: center; justify-content: space-between; gap: 12px; }
.picker-model-option > span { display: flex; min-width: 0; align-items: baseline; gap: 10px; }
.picker-model-option strong { overflow: hidden; text-overflow: ellipsis; font-weight: 500; }
.picker-model-option small { color: var(--el-text-color-secondary); font-size: 11px; }
.picker-star { border: 0; padding: 0 4px; background: transparent; color: var(--el-text-color-secondary); cursor: pointer; font-size: 19px; }
.picker-star.active { color: var(--el-color-warning); }
</style>
