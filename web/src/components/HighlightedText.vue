<template><template v-for="(part, index) in parts" :key="index"><mark v-if="part.match" class="chat-match">{{ part.text }}</mark><template v-else>{{ part.text }}</template></template></template>
<script setup>
import { computed } from 'vue'
import { highlightParts } from '../chatHistory'
const props = defineProps({ text: { type: String, default: '' }, keyword: { type: String, default: '' } })
const parts = computed(() => highlightParts(props.text, props.keyword))
</script>
<style scoped>
.chat-match { background: var(--panel-accent-soft); color: var(--panel-accent-text); font-weight: 600; text-decoration: underline; text-underline-offset: 3px; padding: 0 1px; border-radius: 2px; }
</style>
