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
  flex: 0 0 auto; width: 16px; height: 16px; margin-left: 10px; padding: 0;
  display: inline-flex; align-items: center; justify-content: center;
  border: 1px solid #c0c4cc; border-radius: 50%; background: none;
  color: #a8abb2; font-family: inherit; font-size: 11px; line-height: 1;
  cursor: pointer; transition: color .15s, border-color .15s, background-color .15s;
}
.q:hover { color: #409eff; border-color: #409eff; background: #ecf5ff; }
.q:focus-visible { outline: 2px solid #a0cfff; outline-offset: 1px; }
</style>
