<template>
  <div class="parameter-field">
    <div class="field-heading">
      <label :id="`${id}-label`" :for="field.type === 'number' ? id : undefined">{{ field.label }}</label>
      <el-tag v-if="global" size="small" type="info" effect="plain">全局默认</el-tag>
      <el-select v-else class="source-select" size="small" :model-value="inherited ? 'inherit' : 'custom'"
        :class="{ 'is-custom': !inherited }" :aria-label="`${field.label}的参数来源`" :disabled="disabled"
        @change="$emit('source', $event)">
        <el-option value="inherit" label="跟随全局" /><el-option value="custom" label="本群自定义" />
      </el-select>
    </div>
    <div class="field-control">
      <el-radio-group v-if="field.type === 'choice'" :model-value="modelValue" :disabled="locked"
        :aria-labelledby="`${id}-label`" @change="$emit('update:modelValue', $event)">
        <el-radio-button :value="0">克制</el-radio-button><el-radio-button :value="1">正常</el-radio-button><el-radio-button :value="2">主动</el-radio-button>
      </el-radio-group>
      <template v-else-if="field.type === 'quiet'">
        <el-select class="hour-select" :model-value="modelValue[0]" :disabled="locked" aria-label="静默开始时间"
          @change="$emit('update:modelValue', [$event, modelValue[1]])">
          <el-option v-for="hour in 24" :key="hour" :value="hour - 1" :label="hourText(hour - 1)" />
        </el-select><span class="field-unit">至</span>
        <el-select class="hour-select" :model-value="modelValue[1]" :disabled="locked" aria-label="静默结束时间"
          @change="$emit('update:modelValue', [modelValue[0], $event])">
          <el-option v-for="hour in 24" :key="hour" :value="hour - 1" :label="hourText(hour - 1)" />
        </el-select>
      </template>
      <template v-else-if="field.type === 'bool'">
        <el-switch :model-value="modelValue" :disabled="locked" :aria-label="field.label" @change="$emit('update:modelValue', $event)" />
        <span class="field-unit">{{ modelValue ? '已开启' : '已关闭' }}</span>
      </template>
      <template v-else>
        <!-- 当前组件库仅在挂载时写入输入框的 aria-disabled；切换继承状态时重建以同步可访问状态。 -->
        <el-input-number :key="locked ? 'locked' : 'editable'" :id="id" :model-value="modelValue" :disabled="locked" :min="field.min" :max="field.max"
          :step="field.step || 1" :precision="0" :controls="false" :aria-label="field.label"
          @change="$emit('update:modelValue', $event)" />
        <span class="field-unit">{{ field.unit }}</span>
      </template>
    </div>
    <p class="field-hint">{{ field.hint }}</p>
  </div>
</template>
<script setup>
import { computed, useId } from 'vue'
import { hourText } from '../proactiveParameters'
const props = defineProps({ field: { type: Object, required: true }, modelValue: [Number, Boolean, Array], global: Boolean, inherited: Boolean, disabled: Boolean })
defineEmits(['source', 'update:modelValue'])
const id = `proactive-field-${useId()}`
const locked = computed(() => props.disabled || (!props.global && props.inherited))
</script>
<style scoped>
.parameter-field { min-width: 0; padding: 18px 0; }
.parameter-field + .parameter-field { border-top: 1px solid var(--panel-border-soft); }
.field-heading { display: flex; justify-content: space-between; align-items: center; gap: 10px; margin-bottom: 10px; }
.field-heading label { font-size: 14px; font-weight: 500; color: var(--panel-text); }
.source-select { width: 136px; flex: none; }
.source-select.is-custom :deep(.el-select__wrapper) { background: var(--el-color-primary-light-9); box-shadow: none; }
.source-select.is-custom :deep(.el-select__selected-item) { color: var(--panel-accent-text); }
.field-control { display: flex; align-items: center; flex-wrap: wrap; gap: 8px; min-height: 36px; }
.field-control :deep(.el-input-number) { width: 116px; max-width: 100%; }
.field-control :deep(.el-input__inner) { text-align: left; font-variant-numeric: tabular-nums; }
.field-control :deep(input[type="number"]) { appearance: textfield; -moz-appearance: textfield; }
.field-control :deep(input::-webkit-inner-spin-button), .field-control :deep(input::-webkit-outer-spin-button) { -webkit-appearance: none; margin: 0; }
.field-control :deep(.el-radio-group) { display: flex; width: 100%; }
.field-control :deep(.el-radio-button) { flex: 1; }
.field-control :deep(.el-radio-button__inner) { width: 100%; padding-inline: 10px; }
.field-control :deep(.el-radio-button.is-disabled.is-active .el-radio-button__inner) { color: var(--panel-text-regular); background: var(--panel-raised); border-color: var(--panel-border); }
.hour-select { flex: 1; min-width: 0; width: 100px; }
.field-unit { color: var(--panel-text-muted); font-size: 13px; }
.field-hint { margin: 8px 0 0; color: var(--panel-text-muted); font-size: 12px; line-height: 1.6; }
@media (max-width: 380px) { .field-heading { gap: 5px; } .source-select { width: 126px; } }
</style>
