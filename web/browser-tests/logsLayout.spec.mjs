import { test, expect } from '@playwright/test'

const entry = (id, overrides = {}) => ({
  id, timestamp: '2026-11-12T08:00:00Z', level: 'Information',
  category: 'Mutter.Core.Services.AiChatService', message: `合成日志 ${id}：请求已完成。`, ...overrides
})
const samples = [
  entry(1),
  entry(2, { category: 'Mutter.Infrastructure.Services.SyntheticBackgroundWorker', message: '长消息换行后应与正文起点对齐。'.repeat(12) }),
  entry(3, { level: 'Error', message: '上游请求失败。', exception: 'SyntheticException: 合成异常\n   at SyntheticService.SendAsync()\n   at SyntheticWorker.RunAsync()' }),
  entry(4, { category: 'Synthetic.' + 'LongCategory'.repeat(12), message: 'https://example.test/' + 'unbroken-path'.repeat(32) }),
  entry(5, { message: '第一行\n  缩进的第二行\n第三行' })
]

async function openLogs(page, entries = samples) {
  let socket
  const send = (target, args) => socket.send(JSON.stringify({ type: 1, target, arguments: args }) + '\x1e')
  await page.route('**/api/**', route => route.fulfill({ json: { isConnected: false } }))
  await page.route('**/hub/botstatus/negotiate*', route => route.fulfill({ json: {
    negotiateVersion: 1, connectionId: 'synthetic-logs', connectionToken: 'synthetic-logs',
    availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text'] }]
  } }))
  await page.routeWebSocket('**/hub/botstatus*', ws => {
    socket = ws
    ws.onMessage(data => {
      for (const frame of String(data).split('\x1e').filter(Boolean)) {
        const message = JSON.parse(frame)
        if (message.protocol) ws.send('{}\x1e')
        if (message.type === 1) {
          if (message.target === 'SubscribeLogs') send('ReceiveLogs', [entries])
          ws.send(JSON.stringify({ type: 3, invocationId: message.invocationId }) + '\x1e')
        }
      }
    })
  })
  await page.goto('/#/logs')
  await expect(page.locator('.logs-card')).toContainText('实时流已连接')
  await expect(page.locator('.log-line')).toHaveCount(entries.length)
  return entries => send('ReceiveLogs', [entries])
}

for (const { width, height, darkMode = false, compact = false } of [
  { width: 1920, height: 1080 },
  { width: 1366, height: 768 },
  { width: 1093, height: 614 }, // 1366×768 at 125% browser zoom.
  { width: 768, height: 1024 },
  { width: 390, height: 844 },
  { width: 320, height: 568 },
  { width: 960, height: 540 },
  { width: 1366, height: 768, darkMode: true, compact: true },
  { width: 390, height: 844, darkMode: true }
]) {
  test(`日志字段与多行正文保持对齐：${width}×${height}${darkMode ? ' · 深色' : ''}${compact ? ' · 紧凑' : ''}`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height })
    await page.addInitScript(settings => localStorage.setItem('mutter-panel-settings', JSON.stringify(settings)), { darkMode, compact, primaryColor: '#4f46e5' })
    await openLogs(page)
    await page.getByRole('switch').locator('..').click()
    await page.locator('.log-box').evaluate(box => { box.scrollTop = 0 })
    await page.screenshot({ path: testInfo.outputPath('logs.png'), fullPage: true })
    await page.locator('.logs-card').screenshot({ path: testInfo.outputPath('logs-card.png') })

    const geometry = await page.locator('.logs-card').evaluate(card => {
      const box = card.querySelector('.log-box')
      const main = document.querySelector('.panel-main')
      const messages = [...card.querySelectorAll('.msg')]
      return {
        mainOverflow: main.scrollWidth - main.clientWidth,
        boxOverflow: box.scrollWidth - box.clientWidth,
        boxHeight: box.clientHeight,
        mainBottom: main.getBoundingClientRect().bottom,
        cardBottom: card.getBoundingClientRect().bottom,
        footerBottom: document.querySelector('.panel-page-footer').getBoundingClientRect().bottom,
        categories: [...card.querySelectorAll('.cat')].map(category => ({
          top: category.getBoundingClientRect().top,
          bottom: category.getBoundingClientRect().bottom,
          levelBottom: category.previousElementSibling.getBoundingClientRect().bottom,
          messageTop: category.nextElementSibling.getBoundingClientRect().top,
          hiddenWidth: category.scrollWidth - category.clientWidth,
          hiddenHeight: category.scrollHeight - category.clientHeight
        })),
        messageRects: messages.map(message => {
          const range = document.createRange()
          range.selectNodeContents(message)
          return [...range.getClientRects()].filter(rect => rect.width > 0).map(rect => ({ x: rect.x, right: rect.right }))
        }),
        boxRight: box.getBoundingClientRect().right
      }
    })
    expect(geometry.mainOverflow).toBeLessThanOrEqual(1)
    expect(geometry.boxOverflow).toBeLessThanOrEqual(1)
    expect(geometry.boxHeight).toBeGreaterThanOrEqual(150)
    expect(Math.abs(geometry.mainBottom - height)).toBeLessThanOrEqual(1)
    for (const category of geometry.categories) {
      expect(category.top).toBeGreaterThanOrEqual(category.levelBottom - 1)
      expect(category.messageTop).toBeGreaterThanOrEqual(category.bottom - 1)
      expect(category.hiddenWidth).toBeLessThanOrEqual(1)
      expect(category.hiddenHeight).toBeLessThanOrEqual(1)
    }
    // Short windows may scroll the page to keep the log area usable.
    if (height >= 614 && width >= 768) {
      expect(geometry.cardBottom).toBeLessThanOrEqual(geometry.mainBottom)
      expect(Math.abs(geometry.footerBottom - (geometry.mainBottom - 24))).toBeLessThanOrEqual(1)
    }
    for (const rects of geometry.messageRects) {
      const left = rects[0].x
      for (const rect of rects) {
        expect(rect.x).toBeGreaterThanOrEqual(left - 1)
        expect(rect.right).toBeLessThanOrEqual(geometry.boxRight)
      }
    }
  })
}

test('日志流滚动与筛选、清空和导出保持可用', async ({ page }) => {
  await page.setViewportSize({ width: 1366, height: 768 })
  const append = await openLogs(page, Array.from({ length: 80 }, (_, i) => entry(i)))
  const box = page.locator('.log-box')
  const bottomGap = () => box.evaluate(box => box.scrollHeight - box.clientHeight - box.scrollTop)
  await expect.poll(bottomGap).toBeLessThanOrEqual(1)
  await page.getByRole('switch').locator('..').click()
  await box.evaluate(box => { box.scrollTop = 0 })
  const latest = entry(81, { level: 'Error', category: samples[3].category, exception: samples[2].exception })
  append([latest])
  await expect(page.locator('.log-line')).toHaveCount(81)
  expect(await box.evaluate(box => box.scrollTop)).toBe(0)
  await page.getByRole('switch').locator('..').click()
  await expect.poll(bottomGap).toBeLessThanOrEqual(1)
  await page.getByPlaceholder('关键词过滤').fill('合成日志 81：')
  await page.locator('.level-filter').click()
  await page.getByRole('option', { name: 'Error', exact: true }).click()
  await expect(page.locator('.log-line')).toHaveCount(1)
  const download = page.waitForEvent('download')
  await page.getByRole('button', { name: '导出', exact: true }).click()
  const file = await download
  expect(file.suggestedFilename()).toMatch(/^mutter-logs-.*\.txt$/)
  const chunks = []
  for await (const chunk of await file.createReadStream()) chunks.push(chunk)
  const exported = Buffer.concat(chunks).toString('utf8')
  expect(exported).toContain(`[Error] [${latest.category}] ${latest.message}\n${latest.exception}`)
  await page.getByRole('button', { name: '清空视图', exact: true }).click()
  await expect(page.locator('.log-line')).toHaveCount(0)
  await expect(box).toHaveText('暂无日志')
})
