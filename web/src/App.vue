<template>
  <el-config-provider :locale="zhCn">
    <a href="#panel-main" class="skip-navigation" @click.prevent="focusMain">跳到页面内容</a>
    <div class="panel-layout">
      <aside class="panel-sidebar"><PanelNavigation /></aside>
      <div class="panel-workspace">
        <header class="panel-topbar">
          <div class="panel-topbar-start">
            <el-button class="mobile-menu-trigger" text :icon="Menu" aria-label="打开导航" aria-controls="mobile-navigation" :aria-expanded="mobileMenuOpen" @click="mobileMenuOpen = true" />
            <nav class="panel-breadcrumb" aria-label="当前位置"><span>{{ route.meta.section }}</span><span class="breadcrumb-divider" aria-hidden="true">/</span><span aria-current="page">{{ route.meta.title }}</span></nav>
          </div>
          <div class="panel-topbar-actions">
            <router-link to="/about" class="panel-settings-link"><el-icon><Brush /></el-icon><span>面板设置</span></router-link>
            <span class="panel-status" :class="connectionState" role="status"><span class="connection-dot" :class="connectionState" aria-hidden="true"></span>{{ connectionText }}</span>
          </div>
        </header>
        <main id="panel-main" class="panel-main" tabindex="-1">
          <div class="page-container">
            <header class="page-heading"><div><p class="page-section">{{ route.meta.section }}</p><h1>{{ route.meta.title }}</h1><p class="page-description">{{ route.meta.description }}</p></div></header>
            <router-view />
            <footer class="panel-page-footer"><span>懂群聊，也懂分寸。</span><router-link to="/about">Mutter · v{{ appVersion }}</router-link></footer>
          </div>
        </main>
      </div>
    </div>
    <el-drawer id="mobile-navigation" v-model="mobileMenuOpen" direction="ltr" size="min(300px, 88vw)" :with-header="false" class="panel-mobile-drawer" aria-label="面板导航">
      <PanelNavigation dismissible @close="mobileMenuOpen = false" />
    </el-drawer>
  </el-config-provider>
</template>

<script setup>
import { computed, ref, watch, onMounted, onUnmounted } from 'vue'
import zhCn from 'element-plus/es/locale/lang/zh-cn'
import { useRoute } from 'vue-router'
import { Menu, Brush } from '@element-plus/icons-vue'
import PanelNavigation from './components/PanelNavigation.vue'
import { botStatus } from './botStatus'
import { useVisiblePolling } from './useVisiblePolling'
import { subscribeRealtime } from './realtime'
import api from './api'
import { appVersion } from './version'

const route = useRoute()
const mobileMenuOpen = ref(false)
const focusMain = () => { document.getElementById('panel-main')?.focus() }
const connectionState = computed(() => botStatus.stale ? 'unknown' : botStatus.data.isConnected ? 'online' : 'offline')
const connectionText = computed(() => botStatus.stale ? '状态待更新' : botStatus.data.isConnected ? 'Bot 在线' : 'Bot 离线')
const mobileViewport = window.matchMedia('(max-width: 900px)')
const viewportChanged = () => { if (!mobileViewport.matches) mobileMenuOpen.value = false }
// Only close after successful navigation; an unsaved-changes guard may cancel it.
watch(() => route.fullPath, () => {
  mobileMenuOpen.value = false
  document.getElementById('panel-main')?.scrollTo({ top: 0 })
}, { flush: 'post' })
let unsubscribe = null
let pushRevision = 0

const poll = async (signal) => {
  const revision = pushRevision
  try {
    const { data } = await api.get('/status', { signal, skipErrorMessage: true })
    if (revision === pushRevision) { botStatus.data = data; botStatus.stale = false }
  } catch { if (!signal.aborted) botStatus.stale = true }
}

const refreshStatus = useVisiblePolling(poll, 10000)
onMounted(() => {
  mobileViewport.addEventListener('change', viewportChanged)
  unsubscribe = subscribeRealtime({
    ConnectionStatusChanged: (connected) => { pushRevision++; botStatus.data.isConnected = connected; botStatus.stale = false },
    Resync: refreshStatus
  })
})
onUnmounted(() => { unsubscribe?.(); mobileViewport.removeEventListener('change', viewportChanged) })
</script>
