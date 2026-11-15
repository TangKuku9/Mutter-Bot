import { test, expect } from '@playwright/test'

// 运行概览回归：表格视口高度、加载占位与单位、可访问命名、处理过程弹窗的静默轮询与 404 停止。
const groupNames = { 'g-1': '回归测试群', 'g-2': '闲聊群' }
const taskRow = (i, state) => ({
  id: `op-${i}`, conversationId: `g:${i % 2 ? 'g-1' : 'g-2'}`, description: `合成任务 ${i}：生成回复`,
  background: i % 3 === 0, state, enqueuedAt: '2026-11-14T08:00:00Z'
})
const callRow = (i) => ({
  operationId: `call-${i}`, model: '合成模型', purpose: i % 2 ? 'reply' : 'proactive',
  elapsedMs: 1500 + i * 37, state: 'success', startedAt: '2026-11-14T08:05:00Z'
})
const runtimePayload = ({ empty = false, samples = true } = {}) => ({
  queue: { parallelism: 3, running: 1, waiting: 4, capacity: 256, rejected: 2, expired: 1, coalesced: 5,
    tasks: empty ? [] : Array.from({ length: 12 }, (_, i) => taskRow(i, i < 1 ? 'running' : i < 5 ? 'queued' : 'completed')) },
  calls: empty ? [] : Array.from({ length: 12 }, (_, i) => callRow(i)),
  groupNames, imageCache: { bytes: 5 * 1048576, byteLimit: 512 * 1048576, entries: 34, downloads: 0 },
  upstream: { degraded: false, windowSuccess: 88, windowFailure: 2, p50Ms: 1200, p95Ms: 3400 },
  sampleCount: samples ? 12 : 0, failureCount: samples ? 3 : 0, averageMs: 1800, p95Ms: 2500
})
const operationDetail = (seq) => ({
  task: { id: 'op-0', state: 'running', steps: [
    { at: '2026-11-14T08:00:01Z', stage: 'context', detail: '收集会话最近消息' },
    { at: '2026-11-14T08:00:02Z', stage: 'generate', detail: '调用合成模型' },
    ...(seq > 0 ? [{ at: '2026-11-14T08:00:05Z', stage: 'sending', detail: '已投递到会话' }] : [])
  ] },
  calls: [{ model: '合成模型', stage: 'generate', elapsedMs: 1200, state: 'success', promptTokens: 128, completionTokens: 64, startedAt: '2026-11-14T08:00:02Z' }],
  deliveries: seq > 0 ? [{ id: 'd-1', state: 'sent_active', content: '合成回复内容' }] : []
})
const staticFixtures = {
  '/api/status': { isConnected: false },
  '/api/chat/stats': { totalMessages: 100, todayMessages: 10, aiCallCount: 88, totalTokensUsed: 5000 },
  '/api/config/qqbot': { botQQ: '10001', appId: 'app-1', apiBaseUrl: 'https://api.example.test', botNickname: '合成机器人' }
}
const gate = () => { let release; const promise = new Promise(resolve => { release = resolve }); return { promise, release } }
// useVisiblePolling 在 document 变为可见时立即补查，用它代替真实等待 5 秒轮询。
const nudgePolling = (page) => page.evaluate(() => document.dispatchEvent(new Event('visibilitychange')))

async function setup(page, { gateRuntime = false } = {}) {
  const state = {
    runtime: runtimePayload(), runtimeRequests: 0, runtimeHold: gateRuntime ? gate() : null,
    opRequests: 0, opServed: 0, opMode: 'ok', opGate: null
  }
  await page.route('**/hub/**', route => route.fulfill({ status: 503, body: '' }))
  await page.route('**/api/**', async route => {
    const pathname = new URL(route.request().url()).pathname
    if (pathname === '/api/runtime') {
      state.runtimeRequests++
      if (state.runtimeHold) await state.runtimeHold.promise
      return route.fulfill({ json: state.runtime })
    }
    if (pathname.startsWith('/api/runtime/operations/')) {
      state.opRequests++
      if (state.opMode === 'notfound') return route.fulfill({ status: 404, json: { error: '测试：该任务已不在近期记录中' } })
      if (state.opMode === 'fail') return route.fulfill({ status: 503, json: { error: '测试：上游读取失败' } })
      const hold = state.opGate
      if (hold) { state.opGate = null; await hold.promise }
      return route.fulfill({ json: operationDetail(state.opServed++) })
    }
    if (staticFixtures[pathname]) return route.fulfill({ json: staticFixtures[pathname] })
    return route.fulfill({ status: 404, json: { error: `测试：未处理的 API 请求 ${pathname}` } })
  })
  await page.goto('/#/dashboard')
  await expect(page.locator('.runtime-tabs')).toBeVisible()
  return state
}

const pageMetrics = (page) => page.evaluate(() => {
  const main = document.querySelector('.panel-main')
  const table = [...document.querySelectorAll('.runtime-tabs .el-table')].find(el => el.offsetHeight > 0)
  const wrap = table?.querySelector('.el-scrollbar__wrap')
  const header = document.querySelector('.runtime-diagnostics .el-collapse-item__header')
  return {
    dy: main.scrollHeight - main.clientHeight,
    dx: main.scrollWidth - main.clientWidth,
    tableHeight: table?.getBoundingClientRect().height ?? 0,
    bodyScroll: wrap ? wrap.scrollHeight - wrap.clientHeight : 0,
    diagHeader: header?.getBoundingClientRect().height ?? 0
  }
})

test('矮视口表格收缩且诊断收起后无纵向滚动，移动端保留可读下限', async ({ page }) => {
  await page.setViewportSize({ width: 1366, height: 768 })
  const state = await setup(page)
  await expect(page.locator('.runtime-tabs .el-table__empty-block')).toHaveCount(0)

  await expect.poll(async () => (await pageMetrics(page)).dy).toBeLessThanOrEqual(2)
  let metrics = await pageMetrics(page)
  expect(metrics.dx).toBeLessThanOrEqual(1)
  expect(metrics.diagHeader).toBeGreaterThanOrEqual(58)
  expect(metrics.diagHeader).toBeLessThanOrEqual(66)
  expect(metrics.tableHeight).toBeGreaterThanOrEqual(110)
  expect(metrics.tableHeight).toBeLessThanOrEqual(180)
  expect(metrics.bodyScroll).toBeGreaterThan(300)

  // 展开诊断必然产生内容滚动；重新收起后页面必须回到无纵向滚动。
  await page.locator('.runtime-diagnostics .el-collapse-item__header').click()
  await expect(page.locator('.runtime-diagnostics').getByText('成功调用 P95')).toBeVisible()
  await expect.poll(async () => (await pageMetrics(page)).dy).toBeGreaterThan(10)
  await page.locator('.runtime-diagnostics .el-collapse-item__header').click()
  await expect.poll(async () => (await pageMetrics(page)).dy).toBeLessThanOrEqual(2)

  // 移动端允许页面滚动，但表格高度保住可读下限。
  await page.setViewportSize({ width: 390, height: 844 })
  await expect.poll(async () => (await pageMetrics(page)).tableHeight).toBeGreaterThanOrEqual(239)

  // 无任务时也不应溢出：空表占位小于上限，页面保持无纵向滚动。
  await page.setViewportSize({ width: 1366, height: 768 })
  state.runtime = runtimePayload({ empty: true })
  await nudgePolling(page)
  await expect(page.getByText('暂无会话任务')).toBeVisible()
  await expect(page.getByText('暂无模型调用记录')).toBeHidden()
  await expect.poll(async () => (await pageMetrics(page)).dy).toBeLessThanOrEqual(2)
  expect((await pageMetrics(page)).tableHeight).toBeLessThanOrEqual(180)
})

test('运行数据未加载时 P95 与队列显示占位，加载后带单位；分组与标签页可访问命名', async ({ page }) => {
  await page.setViewportSize({ width: 1366, height: 768 })
  const state = await setup(page, { gateRuntime: true })
  const runtimeHold = state.runtimeHold

  const health = page.locator('.service-health')
  const p95 = health.locator('dl > div').filter({ hasText: '成功调用 P95' }).locator('dd')
  const running = page.locator('.queue-summary > div').nth(0).locator('strong')
  const waiting = page.locator('.queue-summary > div').nth(1).locator('strong')
  const capacity = page.locator('.queue-summary > div').nth(2).locator('strong')
  await expect(p95).toHaveText('—')
  await expect(running).toHaveText('—')
  await expect(waiting).toHaveText('—')
  await expect(capacity).toHaveText('—')

  await expect(page.getByRole('group', { name: '当前处理队列' })).toBeVisible()
  await expect(page.locator('[role="tablist"]')).toHaveAttribute('aria-label', '任务与模型请求')
  expect(await page.locator('.runtime-tabs').getAttribute('aria-label')).toBeNull()

  runtimeHold.release()
  await expect(p95).toHaveText('2.5 秒')
  await expect(running).toHaveText('1 / 3')
  await expect(waiting).toHaveText('4 条')
  await expect(capacity).toHaveText('256 条')

  // 已加载但没有任何成功样本时才提示暂无成功样本。
  state.runtime = { ...state.runtime, sampleCount: 0, failureCount: 0, averageMs: 0, p95Ms: 0 }
  await nudgePolling(page)
  await expect(p95).toHaveText('暂无成功样本')
})

test('处理过程弹窗：首载遮罩、静默轮询不闪遮罩、404 停止轮询且手动刷新可恢复', async ({ page }) => {
  await page.setViewportSize({ width: 1366, height: 768 })
  await page.clock.install({ time: new Date('2026-11-14T08:06:00') })
  const state = await setup(page)
  // 等首屏 loading 过渡结束再冻结时钟；否则即使还没打开弹窗，遮罩的 leave 动画也会停在半途。
  await expect(page.locator('.runtime-activity .el-loading-mask:visible')).toHaveCount(0)
  const now = await page.evaluate(() => Date.now())
  await page.clock.pauseAt(new Date(now + 1000))
  // 冻结后 CSS/RAF 过渡也暂停，用短时间推进让 loading 淡出完成；不足 5 秒不会触发轮询。
  const finishTransitions = () => page.clock.runFor(500)

  const viewButtons = page.getByRole('button', { name: /^查看 .+ 于 .+ 的处理过程$/ })
  await expect(viewButtons.first()).toBeVisible()
  expect(await viewButtons.count()).toBe(12)

  const dialog = page.getByRole('dialog', { name: '处理经过' })
  const mask = dialog.locator('.el-loading-mask')
  const firstLoad = gate()
  state.opGate = firstLoad
  await viewButtons.first().click()
  await expect(mask).toBeVisible()
  await expect.poll(() => state.opRequests).toBe(1)
  firstLoad.release()
  await expect(dialog.getByText('整理上下文')).toBeVisible()
  await finishTransitions()
  await expect(mask).toBeHidden()

  // 轮询带 signal：静默刷新期间不出现遮罩，完成后内容原地更新。
  const silentPoll = gate()
  state.opGate = silentPoll
  await nudgePolling(page)
  await expect.poll(() => state.opRequests).toBe(2)
  await expect(mask).toBeHidden()
  silentPoll.release()
  await expect(dialog.getByText('发送到 QQ')).toBeVisible()
  await expect(dialog.getByText('合成回复内容')).toBeVisible()
  await expect(mask).toBeHidden()

  // 其他错误保留重试：显示提示但不清空已加载内容，下一次轮询成功后恢复。
  state.opMode = 'fail'
  await nudgePolling(page)
  await expect(dialog.getByRole('alert')).toContainText('处理过程暂时无法读取')
  await expect(dialog.getByText('整理上下文')).toBeVisible()
  state.opMode = 'ok'
  await nudgePolling(page)
  await expect(dialog.getByRole('alert')).toBeHidden()

  // 404 透传后端提示并停止后续轮询；关闭弹窗清理状态后，手动刷新与新任务都不被禁用。
  state.opMode = 'notfound'
  await nudgePolling(page)
  await expect(dialog.getByRole('alert')).toContainText('该任务已不在近期记录中')
  const stopped = state.opRequests
  await nudgePolling(page)
  await page.waitForTimeout(300)
  await nudgePolling(page)
  await page.waitForTimeout(300)
  expect(state.opRequests).toBe(stopped)

  state.opMode = 'ok'
  await dialog.getByRole('button', { name: '刷新过程' }).click()
  await expect.poll(() => state.opRequests).toBe(stopped + 1)
  await expect(dialog.getByRole('alert')).toBeHidden()
  await expect(dialog.getByText('整理上下文')).toBeVisible()
  await finishTransitions()
  await expect(mask).toBeHidden()

  await dialog.getByRole('button', { name: '关闭', exact: true }).click()
  await finishTransitions()
  await expect(dialog).toBeHidden()
  await page.getByRole('tab', { name: '模型请求' }).click()
  const reopenLoad = gate()
  state.opGate = reopenLoad
  await viewButtons.first().click()
  await expect(mask).toBeVisible()
  await expect.poll(() => state.opRequests).toBe(stopped + 2)
  reopenLoad.release()
  await expect(dialog.getByText('整理上下文')).toBeVisible()
  await finishTransitions()
  await expect(mask).toBeHidden()
})
