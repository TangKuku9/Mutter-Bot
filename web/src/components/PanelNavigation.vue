<template>
  <div class="panel-navigation">
    <div class="panel-brand-row">
      <router-link to="/dashboard" class="panel-brand" aria-label="Mutter 运行概览">
        <img :src="`/logo-lockup.svg?v=${appVersion}`" alt="Mutter" class="brand-lockup brand-lockup-light" width="152" height="38" />
        <img :src="`/logo-lockup-dark.svg?v=${appVersion}`" alt="Mutter" class="brand-lockup brand-lockup-dark" width="152" height="38" />
        <span class="panel-brand-caption">管理控制台</span>
      </router-link>
      <el-button v-if="dismissible" text circle :icon="Close" aria-label="关闭导航" @click="$emit('close')" />
    </div>
    <nav class="panel-nav" aria-label="主导航">
      <section v-for="section in panelSections" :key="section.title" class="panel-nav-section" :aria-label="section.title">
        <h2 class="panel-nav-label">{{ section.title }}</h2>
        <el-menu :default-active="route.path" router class="panel-menu">
          <el-menu-item v-for="page in section.pages" :key="page.path" :index="page.path" :aria-current="route.path === page.path ? 'page' : undefined">
            <el-icon><component :is="icons[page.icon]" /></el-icon>
            <span>{{ page.title }}</span>
          </el-menu-item>
        </el-menu>
      </section>
    </nav>
    <div class="panel-sidebar-footer">
      <div class="panel-connection"><span class="connection-dot" :class="connectionState" aria-hidden="true"></span><span>{{ connectionText }}</span></div>
      <router-link to="/about" class="panel-version">Mutter <span>v{{ appVersion }}</span></router-link>
    </div>
  </div>
</template>

<script setup>
import { computed } from 'vue'
import { useRoute } from 'vue-router'
import { Odometer, ChatDotRound, DataLine, Setting, User, ChatLineSquare, Document, Postcard, ChatDotSquare, InfoFilled, Close, Collection } from '@element-plus/icons-vue'
import { panelSections } from '../panelNavigation'
import { botStatus } from '../botStatus'
import { appVersion } from '../version'

defineProps({ dismissible: Boolean })
defineEmits(['close'])
const route = useRoute()
const icons = { Odometer, ChatDotRound, DataLine, Setting, User, ChatLineSquare, Document, Postcard, ChatDotSquare, InfoFilled, Collection }
const connectionState = computed(() => botStatus.stale ? 'unknown' : botStatus.data.isConnected ? 'online' : 'offline')
const connectionText = computed(() => botStatus.stale ? '状态待更新' : botStatus.data.isConnected ? 'QQ 网关已连接' : 'QQ 网关未连接')
</script>
