<template>
  <el-config-provider :locale="zhCn">
  <el-container class="layout">
    <el-aside width="200px" class="aside">
      <div class="logo">
        <img :src="`/logo-lockup.svg?v=${appVersion}`" alt="Mutter" class="brand-lockup brand-lockup-light" />
        <img :src="`/logo-lockup-dark.svg?v=${appVersion}`" alt="Mutter" class="brand-lockup brand-lockup-dark" />
        <span class="logo-caption">面板</span>
      </div>
      <el-menu :default-active="route.path" router class="menu">
        <el-menu-item index="/dashboard"><el-icon><Odometer /></el-icon>仪表盘</el-menu-item>
        <el-menu-item index="/chat"><el-icon><ChatDotRound /></el-icon>聊天记录</el-menu-item>
        <el-menu-item index="/ai-stats"><el-icon><DataLine /></el-icon>AI 统计</el-menu-item>
        <el-menu-item index="/ai-settings"><el-icon><Setting /></el-icon>AI 设置</el-menu-item>
        <el-menu-item index="/groups"><el-icon><User /></el-icon>群管理</el-menu-item>
        <el-menu-item index="/proactive"><el-icon><ChatDotSquare /></el-icon>择机发言</el-menu-item>
        <el-menu-item index="/private-chats"><el-icon><ChatLineSquare /></el-icon>私聊管理</el-menu-item>
        <el-menu-item index="/logs"><el-icon><Document /></el-icon>实时日志</el-menu-item>
        <el-menu-item index="/account"><el-icon><Postcard /></el-icon>账号中心</el-menu-item>
        <el-menu-item index="/about">
          <el-icon>
            <svg viewBox="0 0 1024 1024" xmlns="http://www.w3.org/2000/svg" aria-hidden="true">
              <path fill="currentColor" d="M512 896a384 384 0 1 0 0-768 384 384 0 0 0 0 768m0 64a448 448 0 1 1 0-896 448 448 0 0 1 0 896M512 320a32 32 0 0 1 32 32v256a32 32 0 1 1-64 0V352a32 32 0 0 1 32-32m0 368a48 48 0 1 1 0 96 48 48 0 0 1 0-96" />
            </svg>
          </el-icon>关于
        </el-menu-item>
      </el-menu>
    </el-aside>
    <el-container>
      <el-header class="header">
        <span>{{ route.meta.title }}</span>
        <el-tag :type="botStatus.stale ? 'info' : connected ? 'success' : 'danger'" size="small" effect="plain">
          {{ botStatus.stale ? '状态待更新' : connected ? 'Bot 在线' : 'Bot 离线' }}
        </el-tag>
      </el-header>
      <el-main class="main">
        <router-view />
      </el-main>
    </el-container>
  </el-container>
  </el-config-provider>
</template>

<script setup>
import { computed, onMounted, onUnmounted } from 'vue'
import zhCn from 'element-plus/es/locale/lang/zh-cn'
import { botStatus } from './botStatus'
import { useVisiblePolling } from './useVisiblePolling'
import { subscribeRealtime } from './realtime'
import { useRoute } from 'vue-router'
import {
  Odometer, ChatDotRound, DataLine, Setting, User,
  ChatLineSquare, Document, Postcard, ChatDotSquare
} from '@element-plus/icons-vue'
import api from './api'
import { appVersion } from './version'

const route = useRoute()
const connected = computed(() => !!botStatus.data.isConnected)
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
onMounted(() => { unsubscribe = subscribeRealtime({
  ConnectionStatusChanged: (connected) => { pushRevision++; botStatus.data.isConnected = connected; botStatus.stale = false },
  Resync: refreshStatus
}) })
onUnmounted(() => unsubscribe?.())
</script>

<style>
body { margin: 0; background: #f5f7fa; }
.layout { height: 100vh; }
.aside { background: #fff; border-right: 1px solid #e4e7ed; }
.logo { display: flex; align-items: center; gap: 9px; font-size: 17px; font-weight: 700; padding: 18px 16px; color: #303133; }
.brand-lockup { width: 132px; height: 33px; flex: none; display: block; }
.brand-lockup-dark { display: none; }
html.dark .brand-lockup-light { display: none; }
html.dark .brand-lockup-dark { display: block; }
.logo-caption { font-size: 12px; font-weight: 500; white-space: nowrap; }
.menu { border-right: none; }
.header { background: #fff; border-bottom: 1px solid #e4e7ed; display: flex; align-items: center; justify-content: space-between; font-weight: 600; }
.main { overflow-y: auto; }

/* 深色模式：外壳用深色底，Element Plus 组件由 dark/css-vars 接管 */
html.dark body { background: #0a0a0a; }
html.dark .aside { background: #141414; border-right-color: #414243; }
html.dark .logo { color: #e5eaf3; }
html.dark .header { background: #141414; border-bottom-color: #414243; }

/* 紧凑模式 */
html.compact .logo { padding: 12px 16px; }
html.compact .el-menu { --el-menu-item-height: 44px; }
html.compact .main { padding: 12px; }
html.compact .el-card { --el-card-padding: 14px; }
</style>
