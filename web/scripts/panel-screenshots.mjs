// 生成 README 用的管理面板截图。
//
// 用法：
//   npm run build            # 先构建当前前端（输出到 src/Mutter.Web/wwwroot）
//   npm run shots:readme     # 或 node scripts/panel-screenshots.mjs [输出目录]
//
// 默认输出到 artifacts/readme-shots/raw。截图使用 Playwright 启动的
// Vite preview 与内置合成数据，不连接真实后端，也不会读取本机数据库。
// 这里输出的是未加框的原图；README 中的 WebP 成品还会裁切、加圆角/阴影
// 并压缩，页面内容与数据以本脚本为准。
// 需要指定浏览器时设置 MUTTER_SHOT_BROWSER（例如 msedge）。
import { spawn, spawnSync } from 'node:child_process'
import { mkdirSync } from 'node:fs'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { chromium } from '@playwright/test'
import { fixtures, probeStreamBody } from './readme-fixtures.mjs'

const scriptDir = dirname(fileURLToPath(import.meta.url))
const webRoot = resolve(scriptDir, '..')
const outputDir = resolve(process.argv[2] || join(webRoot, '..', 'artifacts', 'readme-shots', 'raw'))
const baseUrl = 'http://127.0.0.1:4179'
const port = 4179

mkdirSync(outputDir, { recursive: true })

const preview = spawn(
  process.platform === 'win32' ? 'npm.cmd' : 'npm',
  ['run', 'preview', '--', '--host', '127.0.0.1', '--port', String(port), '--strictPort'],
  { cwd: webRoot, shell: process.platform === 'win32', stdio: ['ignore', 'pipe', 'pipe'] }
)
let previewLog = ''
preview.stdout.on('data', chunk => { previewLog += chunk.toString() })
preview.stderr.on('data', chunk => { previewLog += chunk.toString() })

const waitForServer = async () => {
  const deadline = Date.now() + 60_000
  while (Date.now() < deadline) {
    try {
      const response = await fetch(baseUrl)
      if (response.ok) return
    } catch { /* 尚未监听 */ }
    await new Promise(resolve => setTimeout(resolve, 300))
  }
  throw new Error(`预览服务未启动：\n${previewLog}`)
}

const stopPreview = () => {
  if (preview.exitCode !== null) return
  if (process.platform === 'win32') spawnSync('taskkill', ['/pid', String(preview.pid), '/T', '/F'], { stdio: 'ignore' })
  else preview.kill('SIGTERM')
}

const json = (route, body) => route.fulfill({ status: 200, contentType: 'application/json; charset=utf-8', body: JSON.stringify(body) })

const apiFixture = (pathname) => {
  if (fixtures[pathname]) return fixtures[pathname]
  if (/^\/groups\/[^/]+\/memory-auto$/.test(pathname)) return fixtures['/groups/group-film/memory-auto']
  if (/^\/proactive\/groups\/[^/]+\/memory$/.test(pathname)) {
    return { summary: '群里常约周五晚看电影，偏好轻松喜剧；讨论技术方案时先给结论。', updatedAt: '2026-11-16T02:00:00Z', version: 7 }
  }
  if (/^\/runtime\/operations\/[^/]+$/.test(pathname)) {
    return {
      task: {
        id: pathname.split('/').pop(), conversationId: 'g:group-film', description: '小林', state: 'running',
        enqueuedAt: new Date(Date.now() - 60_000).toISOString(),
        steps: [
          { stage: 'queued', at: new Date(Date.now() - 60_000).toISOString(), detail: '进入会话队列' },
          { stage: 'context', at: new Date(Date.now() - 55_000).toISOString(), detail: '已准备上下文、群记忆与 2 条相关消息' },
          { stage: 'model', at: new Date(Date.now() - 50_000).toISOString(), detail: '请求 GPT-6.1 Terra' }
        ]
      },
      calls: [
        { stage: 'reply', model: 'gpt-6.1-terra', elapsedMs: 2140, promptTokens: 1832, completionTokens: 96, state: 'success', httpStatus: 200 }
      ],
      deliveries: []
    }
  }
  if (/^\/chat\/timeline\/decisions\/\d+$/.test(pathname)) {
    return {
      id: 911, groupId: 'group-film', groupName: '周末放映室',
      evaluatedAt: new Date(Date.now() - 4 * 60_000).toISOString(), outcome: 'sent_active',
      topic: '今晚的观影安排', line: '那我把《周末大饭店》记下了。七点半开场，吃完正好赶上，散场还能聊两句。',
      reason: '群友在讨论片名和场次，顺着话头补了一个可执行的建议。',
      messageCount: 9, imageCount: 1, memoryCount: 3, memoryPromptChars: 486,
      searchUsed: true, searchQuery: '周末 喜剧 电影 推荐', model: 'gpt-6.1-terra',
      promptTokens: 1240, completionTokens: 96, speechPromptTokens: 620, speechCompletionTokens: 48,
      window: null
    }
  }
  if (pathname === '/config/ai/presets' || pathname.startsWith('/config/ai/presets/')) return fixtures['/config/ai/presets']
  return {}
}

async function installRoutes (context) {
  // SignalR：返回可用的 negotiate 结果，再用最小 JSON 协议完成握手，
  // 这样面板会显示「实时更新」而不是「连接中」。不发送任何业务事件。
  await context.route('**/hub/botstatus/negotiate**', route => json(route, {
    negotiateVersion: 1,
    connectionId: 'readme-demo-connection',
    connectionToken: 'readme-demo-token',
    availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text', 'Binary'] }]
  }))
  await context.routeWebSocket(/\/hub\/botstatus/, ws => {
    ws.onMessage(message => {
      const text = Buffer.isBuffer(message) ? message.toString('utf8') : String(message)
      if (text.includes('"protocol":"json"')) ws.send('{}\u001e')
    })
  })
  await context.route('**/api/**', route => {
    const request = route.request()
    const pathname = new URL(request.url()).pathname.replace(/^\/api/, '')
    if (pathname === '/config/ai/probe-websearch/stream' && request.method() === 'POST') {
      return route.fulfill({ status: 200, contentType: 'text/plain; charset=utf-8', body: probeStreamBody })
    }
    if (request.method() !== 'GET') return json(route, { message: '已保存' })
    return json(route, apiFixture(pathname))
  })
}

async function openRoute (page, route) {
  await page.evaluate(route => { location.hash = route }, route)
  await page.waitForFunction(route => location.hash === '#' + route, route)
  await page.locator('.page-heading h1').waitFor()
  await page.waitForTimeout(700)
}

async function settle (page, extra = 700) {
  await page.waitForTimeout(extra)
  await page.evaluate(() => document.activeElement?.blur?.())
}

async function shot (page, name, { selector, fullPage = false, padding = 0 } = {}) {
  await settle(page)
  const path = join(outputDir, `${name}.png`)
  if (selector) {
    const locator = page.locator(selector).first()
    await locator.scrollIntoViewIfNeeded()
    await locator.screenshot({ path, animations: 'disabled' })
  } else {
    await page.screenshot({ path, fullPage, animations: 'disabled' })
  }
  console.log(`  ${name}.png`)
}

async function addReadmeStyles (page) {
  await page.addStyleTag({
    content: `
      ::-webkit-scrollbar { width: 0 !important; height: 0 !important; }
      * { scrollbar-width: none !important; }
      *, *::before, *::after { transition: none !important; animation-duration: 0s !important; }
    `
  })
}

async function createPage (browser, dark, height = 1100) {
  const context = await browser.newContext({
    viewport: { width: 1600, height },
    deviceScaleFactor: 2,
    locale: 'zh-CN',
    timezoneId: 'Asia/Shanghai',
    colorScheme: dark ? 'dark' : 'light'
  })
  await context.addInitScript(({ dark }) => {
    localStorage.setItem('mutter-panel-settings', JSON.stringify({ primaryColor: '#4f46e5', darkMode: dark, compact: false }))
  }, { dark })
  await installRoutes(context)
  const page = await context.newPage()
  const errors = []
  page.on('pageerror', error => errors.push(error.message))
  page.on('console', message => {
    if (message.type() === 'error' && !message.text().includes('WebSocket')) errors.push(message.text())
  })
  await page.goto(`${baseUrl}/#/dashboard`)
  await page.locator('.page-heading h1').waitFor()
  await addReadmeStyles(page)
  return { context, page, errors }
}

async function captureDashboard (browser, dark) {
  const suffix = dark ? 'dark' : 'light'
  const { context, page, errors } = await createPage(browser, dark)
  console.log(`dashboard (${suffix})`)
  await page.getByText('328,416', { exact: true }).waitFor()
  await page.getByText('Bot 在线', { exact: true }).waitFor()
  await page.locator('.connection-details .el-collapse-item__header').click()
  await settle(page, 400)
  await shot(page, `dashboard-${suffix}`)
  if (!dark) await shot(page, 'dashboard-light-connection')
  await context.close()
  return errors
}

async function captureChat (browser) {
  console.log('chat')
  const { context, page, errors } = await createPage(browser, false)
  await openRoute(page, '/chat')
  await page.locator('.group-button').filter({ hasText: '周末放映室' }).click()
  await page.getByText('今晚有人一起看电影吗？想找部轻松点的。', { exact: true }).waitFor()
  await page.getByText('实时更新', { exact: true }).waitFor()
  await page.locator('.conversation-stream').evaluate(element => { element.scrollTop = element.scrollHeight })
  await shot(page, 'chat-card', { selector: '.chat-history-card' })
  await shot(page, 'chat-conversation', { selector: '.chat-conversation' })
  await context.close()
  return errors
}

async function captureAiSettings (browser) {
  console.log('ai-settings')
  const { context, page, errors } = await createPage(browser, false)
  await openRoute(page, '/ai-settings')
  await page.getByText('已读取配置', { exact: true }).waitFor()
  await page.getByText('API 网关', { exact: true }).first().waitFor()
  await page.evaluate(() => document.querySelector('.panel-main')?.scrollTo(0, 0))
  await shot(page, 'ai-settings-page')
  await shot(page, 'ai-settings-top', { selector: '.ai-primary-grid' })
  await shot(page, 'ai-settings-search', { selector: '.ai-search-card' })

  // 探测报告：复用面板真实的流式解析与结果表格
  const probeButton = page.getByRole('button', { name: '检测联网搜索', exact: true })
  await probeButton.scrollIntoViewIfNeeded()
  await probeButton.click()
  await page.getByText('2 个文字联网策略已验证', { exact: false }).waitFor()
  const dialog = page.locator('.el-dialog').filter({ hasText: '联网搜索探测报告' }).first()
  await shot(page, 'ai-probe-dialog', { selector: '.el-dialog:has-text("联网搜索探测报告")' })
  await page.getByRole('button', { name: '关闭', exact: true }).last().click()
  await settle(page, 400)

  // 服务商连接管理：用更高的视口让模型目录完整落在弹窗里
  await context.close()
  const tall = await createPage(browser, false, 1300)
  await openRoute(tall.page, '/ai-settings')
  await tall.page.getByText('已读取配置', { exact: true }).waitFor()
  await tall.page.getByRole('button', { name: '管理服务商', exact: true }).click()
  await tall.page.getByText('模型目录', { exact: false }).first().waitFor()
  await shot(tall.page, 'ai-connections-dialog', { selector: '.el-dialog:has-text("管理服务商")' })
  await tall.context.close()
  return [...errors, ...tall.errors]
}

async function captureProactive (browser) {
  console.log('proactive')
  const { context, page, errors } = await createPage(browser, false)
  await openRoute(page, '/proactive')
  await page.getByText('14', { exact: true }).first().waitFor()
  await shot(page, 'proactive-overview', { selector: '.proactive-overview' })
  const logsCard = page.locator('.el-card').filter({ hasText: '决策留痕' }).first()
  await logsCard.scrollIntoViewIfNeeded()
  await shot(page, 'proactive-logs', { selector: '.el-card:has-text("决策留痕")' })
  await context.close()
  return errors
}

async function captureMemory (browser) {
  console.log('group-memory')
  const { context, page, errors } = await createPage(browser, false)
  await openRoute(page, '/group-memory')
  await page.getByText('何时学习', { exact: true }).waitFor()
  await shot(page, 'memory-settings', { selector: '.memory-page > .el-card' })
  await page.getByRole('tab', { name: /记忆条目/ }).click()
  await page.getByText('群里常约周五晚看电影', { exact: false }).waitFor()
  await shot(page, 'memory-items', { selector: '.memory-page > .el-card' })
  await context.close()
  return errors
}

async function captureStats (browser) {
  console.log('ai-stats')
  const { context, page, errors } = await createPage(browser, false)
  await openRoute(page, '/ai-stats')
  await page.getByText('1842', { exact: true }).waitFor()
  await page.waitForTimeout(900)
  await shot(page, 'ai-stats')
  await context.close()
  return errors
}

async function captureGroups (browser) {
  console.log('groups')
  const { context, page, errors } = await createPage(browser, false)
  await openRoute(page, '/groups')
  await page.getByText('群列表与配置', { exact: true }).waitFor()
  await page.getByText('周末放映室', { exact: true }).first().waitFor()
  await shot(page, 'groups')
  await context.close()
  return errors
}

let browser
try {
  await waitForServer()
  browser = await chromium.launch({ channel: process.env.MUTTER_SHOT_BROWSER || undefined, headless: true })
  const errors = [
    ...await captureDashboard(browser, false),
    ...await captureDashboard(browser, true),
    ...await captureChat(browser),
    ...await captureAiSettings(browser),
    ...await captureProactive(browser),
    ...await captureMemory(browser),
    ...await captureStats(browser),
    ...await captureGroups(browser)
  ]
  if (errors.length) {
    console.error('页面错误：')
    for (const error of errors) console.error(' -', error)
    process.exitCode = 1
  } else {
    console.log(`\n完成：${outputDir}`)
  }
} finally {
  await browser?.close()
  stopPreview()
}
