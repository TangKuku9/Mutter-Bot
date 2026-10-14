<template>
  <el-container class="layout">
    <el-aside width="200px" class="aside">
      <div class="logo">🐾 QQBot 面板</div>
      <el-menu :default-active="route.path" router class="menu">
        <el-menu-item index="/dashboard"><el-icon><Odometer /></el-icon>仪表盘</el-menu-item>
        <el-menu-item index="/chat"><el-icon><ChatDotRound /></el-icon>聊天记录</el-menu-item>
        <el-menu-item index="/ai-stats"><el-icon><TrendCharts /></el-icon>AI 统计</el-menu-item>
        <el-menu-item index="/ai-settings"><el-icon><Setting /></el-icon>AI 设置</el-menu-item>
        <el-menu-item index="/groups"><el-icon><UserFilled /></el-icon>群管理</el-menu-item>
        <el-menu-item index="/proactive"><el-icon><Comment /></el-icon>择机发言</el-menu-item>
        <el-menu-item index="/private-chats"><el-icon><ChatLineSquare /></el-icon>私聊管理</el-menu-item>
        <el-menu-item index="/logs"><el-icon><Document /></el-icon>实时日志</el-menu-item>
        <el-menu-item index="/account"><el-icon><Avatar /></el-icon>账号中心</el-menu-item>
      </el-menu>
    </el-aside>
    <el-container>
      <el-header class="header">
        <span>{{ route.meta.title }}</span>
        <el-tag :type="connected ? 'success' : 'danger'" size="small" effect="plain">
          {{ connected ? 'Bot 在线' : 'Bot 离线' }}
        </el-tag>
      </el-header>
      <el-main class="main">
        <router-view />
      </el-main>
    </el-container>
  </el-container>
</template>

<script setup>
import { ref, onMounted, onUnmounted } from 'vue'
import { useRoute } from 'vue-router'
import {
  Odometer, ChatDotRound, TrendCharts, Setting, UserFilled,
  ChatLineSquare, Document, Avatar, Comment
} from '@element-plus/icons-vue'
import api from './api'

const route = useRoute()
const connected = ref(false)
let timer = null

const poll = async () => {
  try {
    const { data } = await api.get('/status')
    connected.value = !!data.isConnected
  } catch { /* 401 由拦截器处理 */ }
}

onMounted(() => { poll(); timer = setInterval(poll, 10000) })
onUnmounted(() => clearInterval(timer))
</script>

<style>
body { margin: 0; background: #f5f7fa; }
.layout { height: 100vh; }
.aside { background: #fff; border-right: 1px solid #e4e7ed; }
.logo { font-size: 17px; font-weight: 700; padding: 18px 16px; color: #303133; }
.menu { border-right: none; }
.header { background: #fff; border-bottom: 1px solid #e4e7ed; display: flex; align-items: center; justify-content: space-between; font-weight: 600; }
.main { overflow-y: auto; }
</style>
