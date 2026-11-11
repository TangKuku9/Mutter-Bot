<template>
  <div class="ai-field">
    <div class="ai-field-heading">
      <label :for="controlId" :id="`${controlId}-label`">{{ label }}</label>
      <ParamHelpButton v-if="helpKey" :label="`${label}参数说明`" @click="$emit('help', helpKey)" />
    </div>
    <div class="ai-field-control"><slot /></div>
    <div v-if="hint || $slots.hint" class="ai-field-note">
      <slot name="hint">{{ hint }}</slot>
    </div>
  </div>
</template>
<script setup>
import ParamHelpButton from './ParamHelpButton.vue'
defineProps({
  label: { type: String, required: true },
  controlId: { type: String, required: true },
  helpKey: String,
  hint: String,
})
defineEmits(['help'])
</script>
<style scoped>
.ai-field {
  min-width: 0;
}
.ai-field-heading {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
  margin-bottom: 8px;
  min-height: 24px;
}
.ai-field-heading label {
  font-size: 14px;
  font-weight: 500;
  color: var(--panel-text);
}
.ai-field-heading :deep(.q) {
  margin-left: 0;
  flex: none;
}
.ai-field-control {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
  min-width: 0;
}
.ai-field-control :deep(.el-input),
.ai-field-control :deep(.el-textarea),
.ai-field-control :deep(.el-select) {
  width: 100%;
  max-width: 100%;
}
.ai-field-control :deep(.el-input-number) {
  width: 120px;
  max-width: 100%;
}
.ai-field-control :deep(.el-input-number .el-input__inner) {
  text-align: left;
  font-variant-numeric: tabular-nums;
}
.ai-field-control :deep(input[type='number']) {
  appearance: textfield;
  -moz-appearance: textfield;
}
.ai-field-control :deep(input::-webkit-inner-spin-button),
.ai-field-control :deep(input::-webkit-outer-spin-button) {
  -webkit-appearance: none;
  margin: 0;
}
.ai-field-note {
  margin-top: 7px;
  font-size: 12px;
  line-height: 1.65;
  color: var(--panel-text-muted);
  overflow-wrap: anywhere;
}
.ai-field-note :deep(.el-button) {
  font-size: 12px;
}
</style>
