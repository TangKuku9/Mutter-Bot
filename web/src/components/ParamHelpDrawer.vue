<template>
  <el-drawer v-model="opened" :title="title" direction="rtl" size="400px" append-to-body>
    <div class="help">
      <div class="help-cur"><span>当前值</span><b>{{ current }}</b></div>
      <p v-for="(line, i) in lines" :key="i">{{ line }}</p>
      <div class="help-nav">
        <el-button size="small" :disabled="index <= 0" @click="$emit('step', -1)">上一项</el-button>
        <span class="help-pos">{{ index + 1 }} / {{ total }}</span>
        <el-button size="small" :disabled="index >= total - 1" @click="$emit('step', 1)">下一项</el-button>
      </div>
    </div>
  </el-drawer>
</template>

<script setup>
import { computed } from 'vue'

const props = defineProps({
  modelValue: { type: Boolean, default: false },
  title: { type: String, default: '' },
  lines: { type: Array, default: () => [] },
  current: { type: String, default: '' },
  index: { type: Number, default: 0 },
  total: { type: Number, default: 0 }
})

const emit = defineEmits(['update:modelValue', 'step'])

const opened = computed({
  get: () => props.modelValue,
  set: (v) => emit('update:modelValue', v)
})
</script>

<style scoped>
.help { font-size: 13px; color: #606266; line-height: 1.8; }
.help p { margin: 0 0 8px; }
.help-cur {
  display: flex; align-items: center; gap: 8px; margin-bottom: 14px; padding: 8px 12px;
  background: #f5f7fa; border-radius: 4px; font-size: 12px; color: #909399;
}
.help-cur b { color: #303133; font-size: 13px; }
.help-nav {
  display: flex; align-items: center; justify-content: space-between;
  margin-top: 20px; padding-top: 14px; border-top: 1px solid #ebeef5;
}
.help-pos { font-size: 12px; color: #a8abb2; }
</style>
