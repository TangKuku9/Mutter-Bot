import { test, expect } from '@playwright/test'

const csp = "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self' data:; connect-src 'self'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'; object-src 'none'"

async function secureLogin(page, documentPattern = '**/login.html*') {
  const violations = []
  page.on('console', message => {
    if (message.type() === 'error' && /content security policy|violates|refused to execute/i.test(message.text())) violations.push(message.text())
  })
  await page.route(documentPattern, async route => {
    const response = await route.fetch()
    await route.fulfill({ response, headers: { ...response.headers(), 'content-security-policy': csp } })
  })
  return violations
}

test('严格 CSP 下登录脚本和主题正常，恶意返回地址无法跳出站点', async ({ page, baseURL }) => {
  const violations = await secureLogin(page)
  let submitted = false
  await page.route('**/api/auth/login', async route => {
    expect(route.request().headers()['x-mutter-request']).toBe('1')
    expect(route.request().postDataJSON()).toEqual({ username: 'admin', password: 'synthetic-browser-password-2026!' })
    submitted = true
    await route.fulfill({ json: { username: 'admin' } })
  })
  await page.route(`${baseURL}/`, route => route.fulfill({ contentType: 'text/html', body: '<p>同源登录完成</p>' }))
  await page.goto('/login.html?return=' + encodeURIComponent('/a/..//attacker.example.test'))
  await page.locator('#username').fill('admin')
  await page.locator('#password').fill('synthetic-browser-password-2026!')
  await page.locator('#togglePassword').click()
  await expect(page.locator('#password')).toHaveAttribute('type', 'text')
  await page.locator('#submitBtn').click()
  await expect(page).toHaveURL(`${baseURL}/`)
  expect(submitted).toBe(true)
  expect(violations).toEqual([])
})

test('禁用脚本时提交表单不会把口令放进 URL', async ({ browser, baseURL }) => {
  const context = await browser.newContext({ javaScriptEnabled: false })
  const page = await context.newPage()
  await page.goto('/login.html')
  await page.locator('#username').fill('admin')
  await page.locator('#password').fill('synthetic-browser-password-2026!')
  await page.locator('#submitBtn').click()
  await page.waitForTimeout(300)
  const params = new URL(page.url()).searchParams
  expect(params.get('username')).toBeNull()
  expect(params.get('password')).toBeNull()
  await context.close()
})

test('严格 CSP 下聊天面板正常加载，恶意群消息保持纯文本', async ({ page, baseURL }) => {
  const violations = await secureLogin(page, `${baseURL}/`)
  const text = '<img src=x onerror="window.__injected=1"><script>window.__injected=1</script>'
  await page.route('**/hub/**', route => route.fulfill({ status: 503, body: '' }))
  await page.route('**/api/**', route => {
    const path = new URL(route.request().url()).pathname
    if (path === '/api/status') return route.fulfill({ json: { isConnected: false } })
    if (path === '/api/groups') return route.fulfill({ json: [{ groupOpenId: 'GROUP', name: '合成测试群' }] })
    if (path === '/api/chat/timeline/latest') return route.fulfill({ json: {
      items: [{ kind: 'message', id: 1, groupId: 'GROUP', userName: '合成用户', plainText: text, createdAt: '2026-11-17T08:00:00Z' }],
      totalCount: 1, snapshot: '1:0', page: 1, hasMore: false
    } })
    return route.fulfill({ json: {} })
  })
  await page.goto('/#/chat')
  await expect(page.locator('.message-text')).toHaveText(text)
  await expect(page.locator('.message-text img, .message-text script')).toHaveCount(0)
  expect(await page.evaluate(() => window.__injected)).toBeUndefined()
  expect(violations).toEqual([])
})

test('严格 CSP 下本机初始化界面携带防跨站请求头并校验长口令', async ({ page }) => {
  const violations = await secureLogin(page)
  await page.route('**/api/auth/login', route => route.fulfill({ status: 403, json: { mustChangePassword: true } }))
  let changed = false
  await page.route('**/api/auth/change-password', route => {
    expect(route.request().headers()['x-mutter-request']).toBe('1')
    expect(route.request().postDataJSON().newPassword).toBe('synthetic-new-browser-password-2026!')
    changed = true
    return route.fulfill({ json: { message: '密码修改成功' } })
  })
  await page.goto('/login.html')
  await page.locator('#username').fill('admin')
  await page.locator('#password').fill('CHANGE_ME_STRONG_PASSWORD')
  await page.locator('#submitBtn').click()
  await expect(page.locator('#changeForm')).toBeVisible()
  await page.locator('#bootstrapToken').fill('not-hex')
  await page.locator('#newPassword').fill('short-pass')
  await page.locator('#confirmPassword').fill('short-pass')
  await page.locator('#changeBtn').click()
  await expect(page.locator('#alertText')).toContainText('初始化码')
  expect(changed).toBe(false)
  await page.locator('#bootstrapToken').fill('0123456789abcdef0123456789abcdef')
  await page.locator('#newPassword').fill('synthetic-new-browser-password-2026!')
  await page.locator('#confirmPassword').fill('synthetic-new-browser-password-2026!')
  await page.locator('#changeBtn').click()
  await expect(page.locator('#loginForm')).toBeVisible()
  expect(changed).toBe(true)
  expect(violations).toEqual([])
})
