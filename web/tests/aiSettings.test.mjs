import test from 'node:test'
import assert from 'node:assert/strict'
import { aiSettingsError, normalizeAiSettings } from '../src/aiSettings.js'

test('older AI configurations receive defaults without replacing zero, false or masked keys', () => {
  const form = normalizeAiSettings({ temperature: 0, maxAttachedImages: 0, groupMemoryInjectionEnabled: false, apiKey: 'sk-****', externalSearch: { apiKey: 'search-****', enabled: true } })
  assert.equal(form.temperature, 0)
  assert.equal(form.maxAttachedImages, 0)
  assert.equal(form.groupMemoryInjectionEnabled, false)
  assert.equal(form.apiKey, 'sk-****')
  assert.equal(form.externalSearch.apiKey, 'search-****')
  assert.equal(form.externalSearch.maxResults, 5)
  assert.equal(form.externalSearch.enabled, true)
  assert.equal(form.maxInputTokens, 24576)
  assert.equal(aiSettingsError(form), '')
})

test('fresh defaults are independent, including nested search settings', () => {
  const first = normalizeAiSettings({ externalSearch: null })
  first.externalSearch.maxResults = 10
  assert.equal(normalizeAiSettings().externalSearch.maxResults, 5)
  assert.deepEqual(first.externalSearchStatus, { verifiedAt: null })
})

test('empty numeric controls must not silently save fallback values', () => {
  for (const key of ['temperature', 'maxTokens', 'maxContextMessages', 'maxAttachedImages', 'contextWindowTokens', 'maxInputTokens']) {
    for (const value of [undefined, null, NaN]) {
      const form = normalizeAiSettings({ [key]: value })
      assert.notEqual(aiSettingsError(form), '', `${key}: ${value}`)
    }
  }
})

test('integer limits and the server attachment ceiling are validated', () => {
  assert.match(aiSettingsError(normalizeAiSettings({ maxContextMessages: 2.5 })), /历史消息/)
  assert.match(aiSettingsError(normalizeAiSettings({ ceilingAttachedImages: 4, maxAttachedImages: 5 })), /附图/)
  assert.match(aiSettingsError(normalizeAiSettings({ maxTokens: 63 })), /回复输出/)
  assert.equal(aiSettingsError(normalizeAiSettings({ temperature: 2, maxAttachedImages: 10 })), '')
})

test('context allocations include output and safety reserve', () => {
  assert.match(aiSettingsError(normalizeAiSettings({ contextWindowTokens: 4096 })), /超出容量/)
  assert.equal(aiSettingsError(normalizeAiSettings({ contextWindowTokens: 4096, maxTokens: 2048, maxInputTokens: 1024 })), '')
})

test('search limits remain valid when changing the preferred search route', () => {
  for (const enabled of [false, true]) {
    assert.match(aiSettingsError(normalizeAiSettings({ externalSearch: { enabled, maxResults: null } })), /检索结果/)
    assert.match(aiSettingsError(normalizeAiSettings({ externalSearch: { enabled, timeoutSeconds: 31 } })), /检索超时/)
  }
})
