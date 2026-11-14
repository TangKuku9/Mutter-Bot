import { test, expect } from '@playwright/test'
import { defaultProactiveConfig } from '../src/proactiveParameters.js'

// 各面板的“状态边界”：开关失败回弹、移动端列表失败恢复、来源弹窗竞态与留痕读取失败。

test('私聊开关保存成功才更新，失败回弹且同行在途禁止并发', async ({ page }) => {
  const state = { failToggle: true, hold: null, release: null, writes: [] }
  await page.route('**/hub/**', route => route.fulfill({ status: 503, body: '' }))
  await page.route('**/api/**', async route => {
    const request = route.request(), path = new URL(request.url()).pathname, method = request.method()
    if (path === '/api/groups/private-chats' && method === 'GET') {
      return route.fulfill({ json: [{ userId: 'user-a', nickname: '成员甲', isEnabled: false, isAdmin: false }] })
    }
    if (path === '/api/groups/private-chats/user-a' && method === 'PUT') {
      state.writes.push(request.postDataJSON())
      if (state.failToggle) return route.fulfill({ status: 503, json: { error: '合成保存失败' } })
      if (state.hold) await new Promise(resolve => { state.release = resolve })
      return route.fulfill({ json: {} })
    }
    if (path === '/api/status') return route.fulfill({ json: { isConnected: false } })
    return route.fulfill({ status: 404, json: { error: `未处理的合成请求 ${method} ${path}` } })
  })
  await page.goto('/#/private-chats')
  const row = page.locator('.el-table__row').filter({ hasText: '成员甲' })
  const enable = row.getByRole('switch', { name: '启用 成员甲' })
  const admin = row.getByRole('switch', { name: '管理员 成员甲' })
  const clickSwitch = locator => locator.locator('..').click()
  await expect(enable).toHaveAttribute('aria-checked', 'false')

  // 失败：开关回弹到原值，loading 解除，提示由 API 层弹出。
  await clickSwitch(enable)
  await expect(page.getByText('合成保存失败', { exact: true })).toBeVisible()
  await expect(enable).toHaveAttribute('aria-checked', 'false')
  await expect(enable).toBeEnabled()
  expect(state.writes[0]).toEqual({ isEnabled: true, isAdmin: false })

  // 在途：同一行两个开关都禁用，视觉态保持原值，不允许并发。
  state.failToggle = false
  state.hold = true
  await clickSwitch(enable)
  await expect(enable).toBeDisabled()
  await expect(admin).toBeDisabled()
  await expect(enable).toHaveAttribute('aria-checked', 'false')
  // 请求在途时手动刷新会替换行对象；PUT 成功后仍应更新当前列表里的同一行。
  await page.getByRole('button', { name: '刷新', exact: true }).click()
  await expect(enable).toBeDisabled()
  state.release()
  state.hold = false
  await expect(page.getByText('已保存', { exact: true })).toBeVisible()
  await expect(enable).toHaveAttribute('aria-checked', 'true')
  await expect(enable).toBeEnabled()

  // 成功后再切另一字段，PUT 仍同时携带两个字段。
  await clickSwitch(admin)
  await expect(admin).toHaveAttribute('aria-checked', 'true')
  expect(state.writes[1]).toEqual({ isEnabled: true, isAdmin: false })
  expect(state.writes[2]).toEqual({ isEnabled: true, isAdmin: true })
})

test('手机端群列表读取失败显示提示，重试后恢复群选择', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 })
  const state = { groupsFailed: true }
  await page.route('**/hub/**', route => route.fulfill({ status: 503, body: '' }))
  await page.route('**/api/**', async route => {
    const path = new URL(route.request().url()).pathname
    if (path === '/api/groups') {
      return state.groupsFailed ? route.fulfill({ status: 503, json: { error: '合成群列表失败' } }) : route.fulfill({ json: [{ groupOpenId: 'GROUP', name: '合成测试群' }] })
    }
    if (path === '/api/chat/timeline/latest') return route.fulfill({ json: { items: [], totalCount: 0, snapshot: 'snap-1', page: 1, hasMore: false } })
    if (path === '/api/chat/timeline/count') return route.fulfill({ json: { totalCount: 0 } })
    if (path === '/api/status') return route.fulfill({ json: { isConnected: false } })
    return route.fulfill({ status: 404, json: { error: `未处理的合成请求 ${route.request().method()} ${path}` } })
  })
  await page.goto('/#/chat')
  const errorBar = page.locator('.mobile-groups-error')
  await expect(errorBar).toContainText('暂时无法读取群列表')
  state.groupsFailed = false
  await errorBar.getByRole('button', { name: '重试', exact: true }).click()
  await expect(errorBar).toBeHidden()
  await page.locator('.mobile-group-picker .el-select').click()
  await expect(page.getByRole('option', { name: '合成测试群', exact: true })).toBeVisible()
})

async function setupGroupMemory(page, { failLoad = false } = {}) {
  const state = {
    config: { enabled: false, countEnabled: true, count: 50, intervalEnabled: true, intervalMinutes: 120, scheduleEnabled: false, schedules: [{ frequency: 'daily', time: '22:00', weekday: 1 }], providerConnectionId: '', modelName: '' },
    revision: 0, reads: 0, writes: [], failLoad,
    items: [
      { id: 1, content: '合成记忆一：成员甲喜欢喝茶。', tags: ['饮品'], lastSeenAt: '2026-11-14T12:00:00Z', expiresAt: '2027-01-01T00:00:00Z' },
      { id: 2, content: '合成记忆二：成员乙喜欢散步。', tags: ['活动'], lastSeenAt: '2026-11-14T12:00:00Z', expiresAt: '2027-01-01T00:00:00Z' }
    ],
    sources: {
      1: [{ id: 11, userName: '来源甲', plainText: '甲的来源消息。', createdAt: '2026-11-14T12:00:00Z' }],
      2: [{ id: 12, userName: '来源乙', plainText: '乙的来源消息。', createdAt: '2026-11-14T12:00:00Z' }]
    },
    failSources: false, source1Delay: 600, source1Done: false
  }
  const payload = () => ({ config: structuredClone(state.config), revision: state.revision, items: state.items, runs: [], status: { pending: 0, callsToday: 0, lastOutcome: state.revision ? 'baseline_set' : 'not_enabled', groupEnabled: true } })
  await page.route('**/hub/**', route => route.fulfill({ status: 503, body: '' }))
  await page.route('**/api/**', async route => {
    const request = route.request(), path = new URL(request.url()).pathname, method = request.method()
    if (path === '/api/groups') return route.fulfill({ json: [{ groupOpenId: 'GROUP', name: '合成测试群' }] })
    if (path === '/api/config/ai/connections') return route.fulfill({ json: [] })
    if (path === '/api/groups/GROUP/memory-auto') {
      if (method === 'GET') { state.reads++; return state.failLoad ? route.fulfill({ status: 503, json: { error: '合成记忆配置读取失败' } }) : route.fulfill({ json: payload() }) }
      state.writes.push({ body: request.postDataJSON() })
      Object.assign(state.config, request.postDataJSON().config)
      state.revision++
      return route.fulfill({ json: payload() })
    }
    if (path === '/api/groups/GROUP/memory-auto/1/sources') {
      if (state.source1Delay) await new Promise(resolve => setTimeout(resolve, state.source1Delay))
      state.source1Done = true
      try { return route.fulfill({ json: state.sources[1] }) } catch { return null } // 弹窗关闭已取消该请求
    }
    if (path === '/api/groups/GROUP/memory-auto/2/sources') {
      if (state.failSources) return route.fulfill({ status: 503, json: { error: '合成来源读取失败' } })
      return route.fulfill({ json: state.sources[2] })
    }
    if (path === '/api/status') return route.fulfill({ json: { isConnected: false } })
    return route.fulfill({ status: 404, json: { error: `未处理的合成请求 ${method} ${path}` } })
  })
  await page.goto('/#/group-memory?group=GROUP')
  if (!failLoad) await expect(page.getByRole('button', { name: '保存设置', exact: true })).toBeVisible()
  return state
}

test('记忆来源弹窗只显示最后一次点击的结果，失败有页内提示', async ({ page }) => {
  const state = await setupGroupMemory(page)
  await page.getByRole('tab', { name: '记忆条目 2', exact: true }).click()
  const itemButton = content => page.locator('.memory-item').filter({ hasText: content }).getByRole('button', { name: '查看来源' })
  const dialog = page.getByRole('dialog', { name: '记忆来源' })

  await itemButton('合成记忆一').click()
  await expect(dialog).toBeVisible()
  await expect(dialog.locator('.el-loading-mask')).toBeVisible()
  await page.keyboard.press('Escape')
  await expect(dialog).toBeHidden()

  // 关闭时取消了慢请求；改点第二条后，迟到的第一条结果不能覆盖弹窗内容。
  await itemButton('合成记忆二').click()
  await expect(dialog).toContainText('乙的来源消息。')
  await expect.poll(() => state.source1Done).toBe(true)
  await expect(dialog).toContainText('来源乙')
  await expect(dialog).not.toContainText('来源甲')

  // 读取失败：弹窗内出现明确提示，loading 解除，不残留上一次的内容。
  state.failSources = true
  await page.keyboard.press('Escape')
  await expect(dialog).toBeHidden()
  await itemButton('合成记忆二').click()
  await expect(dialog.getByText('来源消息暂时无法读取，请重试。', { exact: true })).toBeVisible()
  await expect(dialog.locator('.el-loading-mask')).toBeHidden()
  await expect(dialog.locator('.memory-item')).toHaveCount(0)

  state.failSources = false
  await dialog.getByRole('button', { name: '重试', exact: true }).click()
  await expect(dialog).toContainText('乙的来源消息。')
  await expect(dialog.getByRole('alert')).toHaveCount(0)
})

test('定时任务缺时刻保存被拦截，补全 HH:mm 后正常保存', async ({ page }) => {
  const state = await setupGroupMemory(page)
  await page.getByText('按定时任务', { exact: true }).click()
  const time = page.getByLabel('定时时刻 1', { exact: true })
  await time.fill('')
  await page.getByRole('button', { name: '保存设置', exact: true }).click()
  await expect(page.getByText('请为每个定时时刻填写有效的 HH:mm', { exact: false })).toBeVisible()
  expect(state.writes).toEqual([])
  await time.fill('09:30')
  await page.getByRole('button', { name: '保存设置', exact: true }).click()
  await expect(page.getByText('群记忆设置已保存', { exact: true })).toBeVisible()
  expect(state.writes).toHaveLength(1)
  expect(state.writes[0].body.config.scheduleEnabled).toBe(true)
  expect(state.writes[0].body.config.schedules[0].time).toBe('09:30')
})

test('记忆配置读取失败提供重试按钮并恢复加载', async ({ page }) => {
  const state = await setupGroupMemory(page, { failLoad: true })
  await expect(page.getByText('记忆配置读取失败', { exact: true })).toBeVisible()
  expect(state.writes).toEqual([])
  state.failLoad = false
  await page.getByRole('button', { name: '重试', exact: true }).click()
  await expect(page.getByRole('button', { name: '保存设置', exact: true })).toBeVisible()
  await expect(page.getByText('首次开启只学习开启后的新消息', { exact: false })).toBeVisible()
  expect(state.reads).toBeGreaterThanOrEqual(2)
})

test('决策留痕读取失败显示页内错误与重试，不把旧表当成新筛选', async ({ page }) => {
  const logRows = [
    { evaluatedAt: '2026-11-14T08:30:00Z', groupId: 'synthetic-group', groupName: '回归测试群', messageCount: 3, imageCount: 0, outcome: 'sent_passive', line: '合成：这里补充一句。', model: 'daily-model', promptTokens: 120, completionTokens: 30 },
    { evaluatedAt: '2026-11-14T07:30:00Z', groupId: 'synthetic-group', groupName: '回归测试群', messageCount: 1, outcome: 'skipped_quiet', reason: '静默时段不发言', model: 'daily-model', promptTokens: 90, completionTokens: 10 }
  ]
  const fixtures = {
    '/api/status': { isConnected: false },
    '/api/groups': [{ groupOpenId: 'synthetic-group', name: '回归测试群', addedAt: '2026-11-11T00:00:00Z', config: { isEnabled: true, aiProactiveEnabled: true } }],
    '/api/groups/quotas': [],
    '/api/config/characters': [],
    '/api/proactive/config': { ...defaultProactiveConfig(), enabled: true },
    '/api/proactive/status': { enabled: true, participatingGroups: 1, todaySent: 0, todayDecisions: 0, pendingWindows: [], quotas: [] },
    '/api/proactive/presets': [],
    '/api/proactive/groups/synthetic-group/config': { isEnabled: true, enabled: true, memoryAllowed: true, overrides: {} }
  }
  const state = { failLogs: false, logQueries: [] }
  await page.route('**/hub/**', route => route.fulfill({ status: 503, body: '' }))
  await page.route('**/api/**', async route => {
    const request = route.request(), url = new URL(request.url()), path = url.pathname
    if (path === '/api/proactive/logs') {
      state.logQueries.push(Object.fromEntries(url.searchParams))
      if (state.failLogs) return route.fulfill({ status: 503, json: { error: '合成留痕读取失败' } })
      const outcome = url.searchParams.get('outcome')
      const items = logRows.filter(row => !outcome || row.outcome === outcome)
      return route.fulfill({ json: { items, totalCount: items.length } })
    }
    const fixture = fixtures[path]
    if (!fixture) return route.fulfill({ status: 404, json: { error: `未处理的合成请求 ${request.method()} ${path}` } })
    return route.fulfill({ json: fixture })
  })
  await page.goto('/#/proactive')
  const rows = page.locator('.el-table__row')
  await expect(rows).toHaveCount(2)

  // 新筛选读取失败：旧表保留但必须出现与当前筛选一致的失败提示，且不能误报总数。
  state.failLogs = true
  await page.locator('.el-select').filter({ hasText: '全部结论' }).click()
  await page.getByRole('option', { name: '已插话（被动）', exact: true }).click()
  const error = page.locator('.logs-error')
  await expect(error).toContainText('决策留痕读取失败')
  await expect(error).toContainText('可能与当前筛选或页码不一致')
  await expect(rows).toHaveCount(2)
  await expect(rows.first()).toContainText('这里补充一句')

  // 重试沿用当前筛选与页码；成功后提示消失，表格只留下当前筛选结果。
  state.failLogs = false
  await error.getByRole('button', { name: '重试', exact: true }).click()
  await expect(error).toBeHidden()
  await expect(rows).toHaveCount(1)
  await expect(rows.first()).toContainText('这里补充一句')
  expect(state.logQueries.at(-1).outcome).toBe('sent_passive')
  expect(state.logQueries.at(-1).page).toBe('1')
})
