import { createRouter, createWebHashHistory } from 'vue-router'
import { panelPages } from './panelNavigation'

// hash 路由：无需服务端 SPA fallback 配置
const components = {
  '/dashboard': () => import('./views/Dashboard.vue'),
  '/chat': () => import('./views/ChatHistory.vue'),
  '/ai-stats': () => import('./views/AiStats.vue'),
  '/ai-settings': () => import('./views/AiSettings.vue'),
  '/groups': () => import('./views/Groups.vue'),
  '/proactive': () => import('./views/Proactive.vue'),
  '/private-chats': () => import('./views/PrivateChats.vue'),
  '/logs': () => import('./views/Logs.vue'),
  '/account': () => import('./views/Account.vue'),
  '/about': () => import('./views/About.vue')
}

const router = createRouter({
  history: createWebHashHistory(),
  routes: [
    { path: '/', redirect: '/dashboard' },
    ...panelPages.map(page => ({ path: page.path, component: components[page.path], meta: page }))
  ]
})

router.afterEach((to) => {
  if (to.meta?.title) document.title = `${to.meta.title} · Mutter 管理面板`
})

export default router
