<template>
  <div class="about">
    <el-card shadow="never" class="hero-card">
      <div class="hero">
        <div class="hero-brand">
          <div class="hero-logo">🐾</div>
          <div>
            <div class="hero-title">
              Mutter
              <el-tag size="small" effect="plain" round>v{{ version }}</el-tag>
            </div>
            <div class="hero-sub">会看气氛的 QQ 群聊智能助手</div>
          </div>
        </div>
        <div class="hero-actions">
          <el-button type="primary" @click="openLink(repoUrl)">GitHub 仓库</el-button>
          <el-button @click="openLink(readmeUrl)">使用文档</el-button>
        </div>
      </div>
      <p class="hero-desc">
        基于 .NET 9 与 QQ 官方机器人 API 构建的群聊助手。支持 @ 回复、私聊与多模态图文理解，
        还能先观察群聊氛围，再决定要不要接话。
      </p>
      <div class="hero-tags">
        <span v-for="tag in heroTags" :key="tag" class="hero-tag"># {{ tag }}</span>
      </div>
    </el-card>

    <el-row :gutter="16">
      <el-col :span="14">
        <el-card shadow="never" class="fill">
          <template #header>
            <div class="card-head">
              <b>运行状态</b>
              <el-button size="small" text @click="loadStatus">刷新</el-button>
            </div>
          </template>
          <el-descriptions :column="2" border>
            <el-descriptions-item label="连接状态">
              <el-tag :type="status.isConnected ? 'success' : 'danger'" size="small" effect="plain">
                {{ status.isConnected ? '已连接' : '未连接' }}
              </el-tag>
            </el-descriptions-item>
            <el-descriptions-item label="运行时长">{{ status.uptime || '-' }}</el-descriptions-item>
            <el-descriptions-item label="服务版本">{{ status.version || '-' }}</el-descriptions-item>
            <el-descriptions-item label="Bot QQ">{{ status.botQQ || '-' }}</el-descriptions-item>
            <el-descriptions-item label="昵称">{{ status.botNickname || '-' }}</el-descriptions-item>
            <el-descriptions-item label="面板端口">{{ status.webPanelPort || '-' }}</el-descriptions-item>
            <el-descriptions-item label="数据来源"><span class="muted">/api/status</span></el-descriptions-item>
          </el-descriptions>

          <div class="quick-start">
            <div class="quick-steps">
              <div class="quick-title">快速上手</div>
              <div v-for="(step, index) in quickStart" :key="step" class="quick-step">
                <span class="quick-index">{{ index + 1 }}</span>
                <span>{{ step }}</span>
              </div>
            </div>
            <div class="update-check">
              <el-tooltip
                v-if="updateState.type === 'update'"
                :content="`将安装 v${update.latestVersion}，服务会自动重启`"
                placement="top"
              >
                <el-link type="success" class="update-action" @click="applyUpdate">
                  {{ updateState.text }}
                  <el-icon><Download /></el-icon>
                </el-link>
              </el-tooltip>
              <el-tooltip
                v-else-if="updateState.type === 'manual'"
                :content="update.selfUpdateReason || '当前环境请前往 GitHub 手动更新'"
                placement="top"
              >
                <el-link type="warning" class="update-action" @click="openLatestRelease">
                  {{ updateState.text }}
                  <el-icon><ArrowRight /></el-icon>
                </el-link>
              </el-tooltip>
              <span v-else class="update-feedback" :class="updateState.type">{{ updateState.text }}</span>
              <el-button
                :loading="update.checking"
                :disabled="update.applying || update.restarting"
                @click="checkUpdate"
              >
                检查更新
              </el-button>
            </div>
          </div>
        </el-card>
      </el-col>

      <el-col :span="10">
        <el-card shadow="never" class="fill">
          <template #header><b>面板设置</b></template>

          <div class="setting-row">
            <div class="setting-main">
              <div class="setting-name">主题色</div>
              <div class="setting-desc">按钮、链接与选中态</div>
            </div>
            <div class="color-options">
              <button
                v-for="color in colorPresets"
                :key="color.value"
                type="button"
                class="color-dot"
                :class="{ active: isActiveColor(color.value) }"
                :style="{ backgroundColor: color.value }"
                :title="color.name"
                :aria-label="`主题色：${color.name}`"
                @click="panelSettings.primaryColor = color.value"
              >
                <el-icon v-if="isActiveColor(color.value)"><Check /></el-icon>
              </button>
            </div>
          </div>

          <div class="setting-row">
            <div class="setting-main">
              <div class="setting-name">深色模式</div>
              <div class="setting-desc">切换面板明暗配色</div>
            </div>
            <el-switch v-model="panelSettings.darkMode" />
          </div>

          <div class="setting-row">
            <div class="setting-main">
              <div class="setting-name">紧凑模式</div>
              <div class="setting-desc">缩小菜单与卡片间距</div>
            </div>
            <el-switch v-model="panelSettings.compact" />
          </div>

          <div class="setting-preview">
            <span class="setting-desc">效果预览</span>
            <el-button type="primary" size="small">主按钮</el-button>
            <el-tag size="small">标签</el-tag>
            <el-link type="primary" :underline="false">链接</el-link>
          </div>

          <div class="setting-foot">
            <span class="setting-desc">设置保存在当前浏览器</span>
            <el-button size="small" text @click="resetPanelSettings">恢复默认</el-button>
          </div>
        </el-card>
      </el-col>
    </el-row>

    <el-card shadow="never">
      <template #header><b>核心能力</b></template>
      <div class="feature-grid">
        <div v-for="feature in features" :key="feature.title" class="feature">
          <el-icon class="feature-icon"><component :is="feature.icon" /></el-icon>
          <div>
            <div class="feature-title">{{ feature.title }}</div>
            <div class="feature-desc">{{ feature.desc }}</div>
          </div>
        </div>
      </div>
    </el-card>

    <el-card shadow="never">
      <template #header><b>项目与协议</b></template>
      <div class="footer-grid">
        <div class="footer-item">
          <div class="footer-label">源码仓库</div>
          <el-link type="primary" :href="repoUrl" target="_blank" rel="noopener">
            {{ repoUrl.replace('https://', '') }}
          </el-link>
        </div>
        <div class="footer-item">
          <div class="footer-label">开源协议</div>
          <el-link type="primary" :href="licenseUrl" target="_blank" rel="noopener">MIT License</el-link>
        </div>
        <div class="footer-item">
          <div class="footer-label">反馈问题</div>
          <el-link type="primary" :href="issuesUrl" target="_blank" rel="noopener">GitHub Issues</el-link>
        </div>
      </div>

      <div class="stack-block">
        <div class="footer-label">技术栈</div>
        <div class="stack-grid">
          <div v-for="item in stack" :key="item.name" class="stack-cell">
            <span class="stack-name">{{ item.name }}</span>
            <span class="stack-desc">{{ item.desc }}</span>
          </div>
        </div>
      </div>

      <el-alert class="disclaimer" type="info" :closable="false" show-icon
        title="本项目仅供学习交流与个人研究使用，运行与二次开发请遵守 QQ 机器人平台规则与相关法律法规。" />
    </el-card>
  </div>
</template>

<script setup>
import { computed, ref, onMounted, onUnmounted } from 'vue'
import { ArrowRight, ChatDotRound, Download, Opportunity, Picture, User, Search, Monitor, Check } from '@element-plus/icons-vue'
import { appVersion as version } from '../version'
import { panelSettings, colorPresets, resetPanelSettings } from '../panelSettings'
import api from '../api'

const repoUrl = 'https://github.com/TangKuku9/Mutter-Bot'
const readmeUrl = `${repoUrl}/blob/main/README.md`
const licenseUrl = `${repoUrl}/blob/main/LICENSE`
const issuesUrl = `${repoUrl}/issues`

const heroTags = ['群聊智能回复', '择机主动发言', '多模态识图', '群级人设', '联网搜索', '实时面板']

const quickStart = [
  '在「AI 设置」选择服务商预设，填写 API Key 与模型名。',
  '在「群管理」为需要互动的群指定人设与群专属提示词。',
  '按需打开「择机发言」，调整触发频率与静默时段。'
]

const stack = [
  { name: '.NET 9', desc: '宿主与 ASP.NET Core API' },
  { name: 'Vue 3', desc: '管理面板前端框架' },
  { name: 'Element Plus', desc: '面板组件库' },
  { name: 'Vite 6', desc: '前端构建与开发服务' },
  { name: 'SQLite + EF Core', desc: '本机数据持久化' },
  { name: 'SignalR', desc: '面板实时消息推送' },
  { name: 'QQ 官方机器人 API', desc: 'WebSocket 网关接入' }
]

const features = [
  { icon: ChatDotRound, title: '群聊智能回复', desc: '被 @ 时结合上下文、人设与附图生成回复。' },
  { icon: Opportunity, title: '择机主动发言', desc: '观察群聊氛围，判断话题是否值得接话。' },
  { icon: Picture, title: '多模态识图', desc: '自动解析群消息图片，交给视觉模型理解。' },
  { icon: User, title: '群级人设', desc: '为单个群指定专属人设与提示词，互不干扰。' },
  { icon: Search, title: '联网搜索', desc: '需要时先检索再回答，并可探测实际可用通道。' },
  { icon: Monitor, title: '实时管理面板', desc: '消息、日志、群配置与运行状态一屏掌握。' }
]

const isActiveColor = (value) => panelSettings.primaryColor.toLowerCase() === value.toLowerCase()

const status = ref({})
let timer = null

const loadStatus = async () => {
  try {
    const { data } = await api.get('/status')
    status.value = data
  } catch {
    // 网络异常时保留占位符，不打断关于页展示
  }
}

const openLink = (url) => window.open(url, '_blank', 'noopener,noreferrer')

const update = ref({
  checking: true,
  applying: false,
  restarting: false,
  currentVersion: '',
  latestVersion: '',
  hasUpdate: false,
  canSelfUpdate: false,
  selfUpdateReason: '',
  releaseUrl: '',
  error: '',
  applyError: ''
})
let updateTimer = null

const updateState = computed(() => {
  if (update.value.restarting) return { type: 'restarting', text: '更新完成，服务正在重启…' }
  if (update.value.applying) return { type: 'applying', text: '正在下载并安装更新…' }
  if (update.value.checking) return { type: 'checking', text: '正在检查…' }
  if (update.value.applyError) return { type: 'error', text: `更新失败：${update.value.applyError}` }
  if (update.value.error) return { type: 'error', text: `检查失败：${update.value.error}` }
  if (update.value.hasUpdate) {
    return update.value.canSelfUpdate
      ? { type: 'update', text: '立即更新' }
      : { type: 'manual', text: '手动更新' }
  }
  return { type: 'latest', text: '已是最新' }
})

const checkUpdate = async () => {
  if (update.value.applying || update.value.restarting) return
  update.value = { ...update.value, checking: true, error: '', applyError: '' }
  try {
    const { data } = await api.get('/update', { timeout: 15000, skipErrorMessage: true })
    update.value = {
      ...update.value,
      checking: false,
      currentVersion: data.currentVersion || '',
      latestVersion: data.latestVersion || '',
      hasUpdate: Boolean(data.hasUpdate),
      canSelfUpdate: Boolean(data.canSelfUpdate),
      selfUpdateReason: data.selfUpdateReason || '',
      releaseUrl: data.releaseUrl || `${repoUrl}/releases/latest`,
      error: data.error || ''
    }
  } catch (err) {
    update.value = {
      ...update.value,
      checking: false,
      error: err?.response?.data?.error || '无法检查更新，请稍后重试'
    }
  }
}

const openLatestRelease = () => {
  openLink(update.value.releaseUrl || `${repoUrl}/releases/latest`)
}

const waitForRestart = async () => {
  for (let i = 0; i < 90; i++) {
    await new Promise((resolve) => setTimeout(resolve, 2000))
    try {
      const { data } = await api.get('/auth/status', { timeout: 3000, skipErrorMessage: true })
      // 会话在内存中，重启后必然失效；仍为已登录说明旧进程还没退出，继续等。
      if (data?.authenticated) continue
      location.href = `/login.html?return=${encodeURIComponent('/#/about')}`
      return
    } catch {
      // 服务正在重启，继续等待
    }
  }
  update.value = { ...update.value, restarting: false, applyError: '服务重启超时，请手动刷新页面' }
}

const applyUpdate = async () => {
  if (update.value.applying || update.value.restarting || !update.value.hasUpdate) return
  update.value = { ...update.value, applying: true, error: '', applyError: '' }
  try {
    const { data } = await api.post('/update/apply', null, {
      timeout: 600000,
      skipErrorMessage: true
    })
    update.value = {
      ...update.value,
      applying: false,
      restarting: true,
      latestVersion: data.version || update.value.latestVersion
    }
    waitForRestart()
  } catch (err) {
    update.value = {
      ...update.value,
      applying: false,
      applyError: err?.response?.data?.error || '请稍后重试'
    }
  }
}

onMounted(() => {
  loadStatus()
  checkUpdate()
  timer = setInterval(loadStatus, 10000)
  updateTimer = setInterval(checkUpdate, 30 * 60 * 1000)
})
onUnmounted(() => {
  clearInterval(timer)
  clearInterval(updateTimer)
})
</script>

<style scoped>
.about { display: flex; flex-direction: column; gap: 16px; }
.hero-card {
  border: none;
  background: linear-gradient(135deg, #eef4ff 0%, #fdf2f8 55%, #ffffff 100%);
}
html.dark .hero-card {
  background: linear-gradient(135deg, #182338 0%, #2a1c2a 55%, #1d1e1f 100%);
}
.hero { display: flex; justify-content: space-between; align-items: center; gap: 16px; flex-wrap: wrap; }
.hero-brand { display: flex; align-items: center; gap: 14px; }
.hero-logo {
  width: 52px; height: 52px; border-radius: 14px; background: var(--el-bg-color-overlay);
  display: flex; align-items: center; justify-content: center; font-size: 28px;
  box-shadow: 0 6px 18px var(--el-color-primary-light-8);
}
.hero-title { display: flex; align-items: center; gap: 8px; font-size: 24px; font-weight: 700; color: var(--el-text-color-primary); }
.hero-sub { margin-top: 4px; font-size: 13px; color: var(--el-text-color-regular); }
.hero-actions { display: flex; gap: 8px; }
.hero-desc { margin: 16px 0 12px; font-size: 13px; line-height: 1.8; color: var(--el-text-color-regular); }
.hero-tags { display: flex; flex-wrap: wrap; gap: 8px; }
.hero-tag {
  padding: 4px 10px; border-radius: 999px; font-size: 12px;
  color: var(--el-color-primary); background: var(--el-color-primary-light-9);
}
.card-head { display: flex; justify-content: space-between; align-items: center; }
.fill { height: 100%; }
.muted { color: var(--el-text-color-secondary); }
.quick-start {
  display: flex; flex-wrap: wrap; justify-content: space-between; align-items: flex-end;
  gap: 16px 24px; margin-top: 18px; padding-top: 14px;
  border-top: 1px dashed var(--el-border-color-lighter);
}
.quick-steps { flex: 1 1 320px; min-width: 0; }
.quick-title { margin-bottom: 10px; font-size: 13px; font-weight: 600; color: var(--el-text-color-primary); }
.quick-step { display: flex; gap: 10px; margin-bottom: 8px; font-size: 13px; line-height: 1.6; color: var(--el-text-color-regular); }
.quick-step:last-child { margin-bottom: 0; }
.quick-index {
  flex: 0 0 20px; height: 20px; border-radius: 50%; line-height: 20px; text-align: center;
  font-size: 12px; color: var(--el-color-primary); background: var(--el-color-primary-light-9);
}
.update-check {
  display: flex; flex-direction: column; align-items: flex-end; gap: 8px;
  flex: 0 0 auto; margin-left: auto;
}
.update-feedback { font-size: 12px; line-height: 1.5; max-width: 280px; text-align: right; }
.update-feedback.checking, .update-feedback.latest { color: var(--el-text-color-secondary); }
.update-feedback.applying, .update-feedback.restarting { color: var(--el-color-primary); }
.update-feedback.error { color: var(--el-color-danger); }
.update-action { font-size: 13px; font-weight: 600; }
.update-action .el-icon { margin-left: 2px; }
.setting-row {
  display: flex; justify-content: space-between; align-items: center; gap: 12px;
  padding: 12px 0; border-bottom: 1px dashed var(--el-border-color-lighter);
}
.setting-row:first-child { padding-top: 2px; }
.setting-name { font-size: 13px; font-weight: 600; color: var(--el-text-color-primary); }
.setting-desc { margin-top: 2px; font-size: 12px; color: var(--el-text-color-secondary); }
.color-options { display: flex; gap: 8px; flex-shrink: 0; }
.color-dot {
  width: 24px; height: 24px; padding: 0; border: 2px solid transparent; border-radius: 50%;
  display: inline-flex; align-items: center; justify-content: center; cursor: pointer;
  color: #fff; transition: transform 0.15s, box-shadow 0.15s;
}
.color-dot:hover { transform: scale(1.1); }
.color-dot.active {
  box-shadow: 0 0 0 2px var(--el-bg-color-overlay), 0 0 0 4px var(--el-color-primary);
}
.color-dot .el-icon { font-size: 13px; }
.setting-preview {
  display: flex; align-items: center; gap: 10px; margin-top: 14px;
  padding: 10px 12px; border-radius: 8px; background: var(--el-fill-color-light);
}
.setting-preview .setting-desc { margin-top: 0; margin-right: auto; }
.setting-foot {
  display: flex; justify-content: space-between; align-items: center;
  margin-top: 12px; padding-top: 12px; border-top: 1px dashed var(--el-border-color-lighter);
}
.feature-grid { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 12px; }
.feature {
  display: flex; gap: 12px; padding: 14px; border-radius: 8px;
  background: var(--el-fill-color-lighter); border: 1px solid var(--el-border-color-lighter);
}
.feature-icon { margin-top: 2px; font-size: 22px; color: var(--el-color-primary); }
.feature-title { font-size: 14px; font-weight: 600; color: var(--el-text-color-primary); }
.feature-desc { margin-top: 4px; font-size: 12px; line-height: 1.6; color: var(--el-text-color-secondary); }
.footer-grid { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 12px; }
.footer-item { padding: 12px; border-radius: 8px; background: var(--el-fill-color-light); }
.footer-label { margin-bottom: 6px; font-size: 12px; color: var(--el-text-color-secondary); }
.stack-block { margin-top: 16px; padding-top: 14px; border-top: 1px dashed var(--el-border-color-lighter); }
.stack-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 8px 24px; }
.stack-cell { display: flex; justify-content: space-between; gap: 12px; font-size: 13px; }
.stack-name { font-weight: 600; color: var(--el-text-color-primary); white-space: nowrap; }
.stack-desc { color: var(--el-text-color-secondary); text-align: right; }
.disclaimer { margin-top: 16px; }
@media (max-width: 1100px) {
  .feature-grid { grid-template-columns: repeat(2, minmax(0, 1fr)); }
}
</style>
