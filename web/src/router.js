import { createRouter, createWebHashHistory } from 'vue-router'

// hash 路由：无需服务端 SPA fallback 配置
const routes = [
  { path: '/', redirect: '/dashboard' },
  { path: '/dashboard', component: () => import('./views/Dashboard.vue'), meta: { title: '仪表盘' } },
  { path: '/chat', component: () => import('./views/ChatHistory.vue'), meta: { title: '聊天记录' } },
  { path: '/ai-stats', component: () => import('./views/AiStats.vue'), meta: { title: 'AI 统计' } },
  { path: '/ai-settings', component: () => import('./views/AiSettings.vue'), meta: { title: 'AI 设置' } },
  { path: '/groups', component: () => import('./views/Groups.vue'), meta: { title: '群管理' } },
  { path: '/proactive', component: () => import('./views/Proactive.vue'), meta: { title: '择机发言' } },
  { path: '/private-chats', component: () => import('./views/PrivateChats.vue'), meta: { title: '私聊管理' } },
  { path: '/cultivation', component: () => import('./views/Cultivation.vue'), meta: { title: '修仙看板' } },
  { path: '/logs', component: () => import('./views/Logs.vue'), meta: { title: '实时日志' } },
  { path: '/account', component: () => import('./views/Account.vue'), meta: { title: '账号中心' } }
]

const router = createRouter({
  history: createWebHashHistory(),
  routes
})

router.afterEach((to) => {
  if (to.meta?.title) document.title = `${to.meta.title} · QQBot 管理面板`
})

export default router
