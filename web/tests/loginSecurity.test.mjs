import test from 'node:test'
import assert from 'node:assert/strict'
import { safeReturnPath } from '../public/login.js'

test('登录返回地址保留同源页面并拒绝网络路径、反斜杠和控制字符', () => {
  const origin = 'https://panel.example.test'
  for (const value of [null, '', 'https://evil.example.test', '//evil.example.test', '/\\evil.example.test',
    '/\t/evil.example.test', '/\n/evil.example.test', 'javascript:alert(1)', 'data:text/html,example',
    '/..//evil.example.test', '/a/..//evil.example.test', '/%2e%2e//evil.example.test',
    '/%2E%2E//evil.example.test', '/%2e//evil.example.test', '/.//evil.example.test']) {
    assert.equal(safeReturnPath(value, origin), '/', `reject ${JSON.stringify(value)}`)
  }
  assert.equal(safeReturnPath('/#/chat-history', origin), '/#/chat-history')
  assert.equal(safeReturnPath('/?view=chat#latest', origin), '/?view=chat#latest')
})

test('登录表单禁用脚本时不再以 GET 泄露口令', async () => {
  const { readFileSync } = await import('node:fs')
  const html = readFileSync(new URL('../public/login.html', import.meta.url), 'utf8')
  const forms = html.match(/<form[^>]*>/g) ?? []
  assert.ok(forms.length >= 2, 'login and change-password forms exist')
  for (const form of forms) {
    assert.match(form, /method="post"/i, `${form} posts instead of a URL-bearing GET`)
    assert.doesNotMatch(form, /method="get"/i, `${form} must not submit credentials into the URL`)
  }
  assert.match(html, /id="bootstrapToken"/, 'first-time rotation asks for the maintenance token')
  assert.match(html, /login\.js\?v=__APP_VERSION__/, 'the login script is cache-busted per release')
})
