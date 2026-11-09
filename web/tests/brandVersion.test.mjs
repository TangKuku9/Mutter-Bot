import test from 'node:test'
import assert from 'node:assert/strict'
import { stampBrandHtml } from '../brandVersion.mjs'

test('登录页版本与所有品牌 URL 同时更新，不改变登录接口与回跳参数', () => {
  const html = `<p>v__APP_VERSION__</p><img src="/logo-lockup-mono-reverse.svg"><link href='/favicon-32.png'><link href="/favicon.ico"><img src="/apple-touch-icon.png"><form action="/api/auth/login?return=%2Fabout"></form>`
  const output = stampBrandHtml(html, '2.3.1')
  assert.match(output, /v2\.3\.1/)
  for (const asset of ['logo-lockup-mono-reverse.svg', 'favicon-32.png', 'favicon.ico', 'apple-touch-icon.png']) {
    assert.ok(output.includes(`/${asset}?v=2.3.1`))
  }
  assert.ok(output.includes('/api/auth/login?return=%2Fabout'))
  assert.ok(!output.includes('__APP_VERSION__'))
})

test('重复构建不叠加版本参数，并能替换上一版缓存键', () => {
  const once = stampBrandHtml('<img src="/logo-mark.svg?v=2.3.0">', '2.3.1')
  assert.equal(once, '<img src="/logo-mark.svg?v=2.3.1">')
  assert.equal(stampBrandHtml(once, '2.3.1'), once)
})
