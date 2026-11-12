<template>
  <div class="ai-field">
    <div class="ai-field-heading">
      <label :for="controlId" :id="`${controlId}-label`" @click="focusControl">{{ label }}</label>
      <ParamHelpButton v-if="helpKey" :label="`${label}参数说明`" @click="$emit('help', helpKey)" />
    </div>
    <div class="ai-field-control" :aria-describedby="hintId || undefined"><slot /></div>
    <div v-if="hint || $slots.hint" :id="`${controlId}-hint`" class="ai-field-note">
      <slot name="hint">{{ hint }}</slot>
    </div>
  </div>
</template>
<script setup>
import { computed, useSlots } from 'vue'
import ParamHelpButton from './ParamHelpButton.vue'
const props = defineProps({
  label: { type: String, required: true },
  controlId: { type: String, required: true },
  helpKey: String,
  hint: String,
})
const slots = useSlots()
const hintId = computed(() => (props.hint || slots.hint ? `${props.controlId}-hint` : null))

// label[for] 只对原生可标记元素生效；radio-group / switch 这类组件的 id 落在容器上，
// 点击标签不会聚焦。这里主动把焦点交给控件本身或其内部第一个可聚焦元素，
// 保证键盘/读屏用户点标签即可到达控件。保留 for/id 关联（Playwright getByLabel 依赖它）。
const focusControl = () => {
  const el = typeof document !== 'undefined' ? document.getElementById(props.controlId) : null
  if (!el) return
  const target = el.matches('input, textarea, select, button, [tabindex]')
    ? el
    : el.querySelector('input, textarea, select, button, [tabindex]')
  target?.focus()
}
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
