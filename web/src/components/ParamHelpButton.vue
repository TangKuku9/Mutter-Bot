<template>
  <button type="button" class="q" :aria-label="label" :title="label">?</button>
</template>

<script setup>
defineProps({
  label: { type: String, default: '说明' }
})
</script>

<style scoped>
.q {
  flex: 0 0 auto; width: 20px; height: 20px; margin-left: 8px; padding: 0;
  display: inline-flex; align-items: center; justify-content: center;
  border: 1px solid var(--el-border-color); border-radius: 50%; background: none;
  color: var(--el-text-color-secondary); font-family: inherit; font-size: 12px; line-height: 1;
  cursor: pointer; transition: color .15s, border-color .15s, background-color .15s;
}
.q:hover { color: var(--panel-accent-text); border-color: var(--panel-accent-text); background: var(--panel-accent-soft); }
.q:focus-visible { outline: 2px solid var(--panel-accent-text); outline-offset: 2px; }
</style>
