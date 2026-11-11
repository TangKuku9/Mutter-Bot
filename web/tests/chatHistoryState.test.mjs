import test from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { ref, computed, nextTick } from 'vue'
import { parse, compileScript } from 'vue/compiler-sfc'
import * as latestRequest from '../src/latestRequest.js'
import * as chatHistory from '../src/chatHistory.js'
import * as runtimeLabels from '../src/runtimeLabels.js'

// 编译实际页面的 setup，只替换网络、时钟和生命周期；不复制页面的状态逻辑。
const { descriptor } = parse(readFileSync(new URL('../src/views/ChatHistory.vue', import.meta.url), 'utf8'))
const script = compileScript(descriptor, { id: 'chat-history-state-test', genDefaultAs: 'TestComponent' })
const setupBody = script.content.slice(script.content.indexOf('const TestComponent ='))
const flush = () => new Promise(resolve => setImmediate(resolve))
const message = (id, extra = {}) => ({
  kind: 'message', id, groupId: 'GROUP', chatType: 0, plainText: `消息 ${id}`,
  createdAt: new Date(Date.UTC(2026, 10, 10) + id * 1000).toISOString(), ...extra
})

async function createPage(t) {
  const records = Array.from({ length: 150 }, (_, index) => message(index + 1))
  const requests = [], timers = new Map(), mounts = [], unmounts = [], failures = new Set()
  let now = 0, timerId = 0, handlers = {}
  const api = {
    get(url, { params = {}, signal } = {}) {
      if (failures.has(url)) return Promise.reject(new Error('模拟网络失败'))
      let resolve
      const promise = new Promise(r => { resolve = r })
      const cap = params.snapshot ? Number(params.snapshot.split(':')[0]) : records.at(-1).id
      const rows = records.filter(row => row.id <= cap && latestRequest.matchesChatMessage(row, params)).reverse()
      const page = params.page || 1, pageSize = params.pageSize || 50
      const data = url === '/groups' ? [{ groupOpenId: 'GROUP', name: '测试群' }]
        : url.endsWith('/count') ? { totalCount: rows.length, snapshot: `${cap}:0` }
          : { items: rows.slice((page - 1) * pageSize, page * pageSize), page,
              totalCount: url.endsWith('/latest') ? null : rows.length,
              hasMore: page * pageSize < rows.length, snapshot: `${cap}:0` }
      requests.push({ url, params, signal, data, answered: false, resolve })
      return promise
    }
  }
  const modules = {
    vue: { ref, computed, nextTick, onMounted: fn => mounts.push(fn), onUnmounted: fn => unmounts.push(fn) },
    '../latestRequest': latestRequest, '../chatHistory': chatHistory, '../runtimeLabels': runtimeLabels,
    '../api': { default: api },
    '../realtime': {
      realtimeState: { connected: true },
      subscribeRealtime(next) { handlers = next; return () => { handlers = {} } }
    }
  }
  const imports = Object.values(script.imports)
  const component = new Function(...imports.map(binding => binding.local), 'setTimeout', 'clearTimeout',
    `${setupBody}\nreturn TestComponent.setup({}, { expose() {} })`)(
    ...imports.map(binding => modules[binding.source]?.[binding.imported]),
    (fn, delay) => { const id = ++timerId; timers.set(id, { fn, due: now + delay }); return id },
    id => timers.delete(id)
  )
  t.after(() => unmounts.forEach(fn => fn()))
  const request = url => {
    // 被取消的请求不会从列表移除；要拿最新一个同 URL 的未应答请求。
    const pending = requests.findLast(entry => !entry.answered && entry.url === url)
    assert.ok(pending, `expected a pending ${url} request`)
    return pending
  }
  const reply = async entry => { entry.answered = true; entry.resolve({ data: entry.data }); await flush() }
  const tick = async elapsed => {
    const target = now + elapsed
    while (true) {
      const next = [...timers].filter(([, timer]) => timer.due <= target).sort((a, b) => a[1].due - b[1].due)[0]
      if (!next) break
      const [id, timer] = next
      now = timer.due; timers.delete(id); timer.fn(); await flush()
    }
    now = target
  }
  const receive = (id, { bot = false } = {}) => {
    const row = message(id, bot ? { isBotMessage: true } : {})
    records.push(row)
    if (bot) handlers.AiResponseCompleted?.(row)
    else handlers.ReceiveMessage?.(row)
  }
  component.groupId.value = 'GROUP'
  mounts.forEach(fn => fn())
  await reply(request('/groups'))
  await reply(request('/chat/timeline/latest'))
  return { component, records, requests, request, reply, tick, receive, failures, emit: event => handlers[event]?.() }
}

test('详情期间跳过消息后，后续推送不跨过缺口推进分页快照', async t => {
  const { component: page, request, reply, receive, requests } = await createPage(t)
  const firstPage = page.items.value.map(row => row.id)
  page.openDetails(page.items.value[0])
  receive(151)
  page.drawerVisible.value = false
  receive(152)
  assert.equal(page.hasNew.value, true)
  assert.equal(page.snapshot.value, '150:0')
  assert.deepEqual(page.items.value.map(row => row.id), firstPage)

  page.onPage(2)
  assert.equal(request('/chat/timeline').params.snapshot, '150:0')
  await reply(request('/chat/timeline'))
  assert.equal(page.hasNew.value, true, '翻旧页不能清除未读提醒')
  assert.equal(page.items.value.some(row => firstPage.includes(row.id)), false)

  page.refreshLatest()
  await reply(request('/chat/timeline/latest'))
  assert.deepEqual(page.items.value.slice(0, 2).map(row => row.id), [152, 151])
  assert.equal(page.hasNew.value, false)
  const latestRequests = requests.filter(entry => entry.url.endsWith('/latest')).length
  receive(153)
  assert.equal(page.snapshot.value, '153:0')
  assert.equal(page.items.value[0].id, 153)
  assert.equal(requests.filter(entry => entry.url.endsWith('/latest')).length, latestRequests)
})

test('慢校准排队新事件后上翻，保留正文与新记录提醒', async t => {
  const { component: page, request, reply, tick, receive } = await createPage(t)
  const firstPage = page.items.value.map(row => row.id)
  receive(151, { bot: true })
  await tick(1500)
  const slow = request('/chat/timeline/latest')
  receive(152, { bot: true })
  page.following.value = false
  await reply(slow)
  assert.deepEqual(page.items.value.map(row => row.id), firstPage)
  assert.equal(page.snapshot.value, '150:0')
  assert.equal(page.hasNew.value, true)

  page.following.value = true
  receive(153)
  assert.equal(page.snapshot.value, '150:0')
  await tick(1500)
  await reply(request('/chat/timeline/latest'))
  assert.deepEqual(page.items.value.slice(0, 3).map(row => row.id), [153, 152, 151])
  assert.equal(page.hasNew.value, false)
})

test('等待 Bot 校准时，普通消息也必须与缺失的 Bot 消息一起补齐', async t => {
  const { component: page, request, reply, tick, receive } = await createPage(t)
  receive(151, { bot: true })
  receive(152)
  assert.equal(page.snapshot.value, '150:0')
  await tick(1500)
  await reply(request('/chat/timeline/latest'))
  assert.deepEqual(page.items.value.slice(0, 2).map(row => row.id), [152, 151])
  assert.equal(page.snapshot.value, '152:0')
})

test('校准中只收到普通消息时，完成后增量补齐且不重复整页查询', async t => {
  const { component: page, request, reply, tick, receive, emit, requests } = await createPage(t)
  emit('Resync')
  await tick(1500)
  const slow = request('/chat/timeline/latest')
  receive(151)
  await reply(slow)
  assert.equal(slow.signal.aborted, false)
  assert.equal(page.items.value[0].id, 151)
  assert.equal(page.snapshot.value, '151:0')
  assert.equal(page.items.value.length, 50)
  await tick(1500)
  assert.equal(requests.filter(entry => entry.url.endsWith('/latest')).length, 2)
})

test('校准中同时缓冲普通消息和 Bot 事件时，不用部分消息推进快照', async t => {
  const { component: page, request, reply, tick, receive, emit } = await createPage(t)
  emit('Resync')
  await tick(1500)
  const slow = request('/chat/timeline/latest')
  receive(151)
  receive(152, { bot: true })
  await reply(slow)
  assert.equal(slow.signal.aborted, false)
  assert.equal(page.snapshot.value, '150:0')
  await tick(1500)
  await reply(request('/chat/timeline/latest'))
  assert.deepEqual(page.items.value.slice(0, 2).map(row => row.id), [152, 151])
})

test('请求期间暂时离开底部再回来，不能丢失期间跳过的消息', async t => {
  const { component: page, request, reply, tick, receive, emit } = await createPage(t)
  emit('Resync')
  await tick(1500)
  const slow = request('/chat/timeline/latest')
  page.following.value = false
  receive(151)
  page.following.value = true
  await reply(slow)
  receive(152)
  assert.equal(page.snapshot.value, '150:0')
  await tick(1500)
  await reply(request('/chat/timeline/latest'))
  assert.deepEqual(page.items.value.slice(0, 2).map(row => row.id), [152, 151])
})

test('暂停后复用已连接的实时通道，也先补齐暂停期间的记录', async t => {
  const { component: page, records, request, reply, tick, receive } = await createPage(t)
  page.live.value = false; page.toggleLive(false)
  records.push(message(151))
  page.live.value = true; page.toggleLive(true)
  receive(152)
  assert.equal(page.snapshot.value, '150:0')
  await tick(1500)
  await reply(request('/chat/timeline/latest'))
  assert.deepEqual(page.items.value.slice(0, 2).map(row => row.id), [152, 151])
})

test('翻页取消在途校准后返回第 1 页，改走最新页补回缺口', async t => {
  const { component: page, request, reply, tick, receive } = await createPage(t)
  const firstPage = page.items.value.map(row => row.id)
  receive(151, { bot: true })
  await tick(1500)
  const slow = request('/chat/timeline/latest')
  receive(152)
  assert.equal(page.snapshot.value, '150:0', '在途校准期间普通消息不能推进快照')

  page.onPage(2)
  assert.equal(slow.signal.aborted, true, '翻页会取消在途的静默校准')
  assert.equal(page.hasNew.value, true, '被取消的校准必须先记下未读提醒')
  await reply(request('/chat/timeline'))
  assert.equal(page.items.value.some(row => firstPage.includes(row.id)), false)
  assert.equal(page.snapshot.value, '150:0', '浏览第 2 页期间不推进快照')

  page.onPage(1)
  const fresh = request('/chat/timeline/latest')
  assert.equal(fresh.params.snapshot, undefined, '回第 1 页取最新页时不带旧快照')
  await reply(fresh)
  assert.deepEqual(page.items.value.slice(0, 2).map(row => row.id), [152, 151], '缺口里的 Bot 与普通消息一起补回')
  assert.equal(page.hasNew.value, false)
  assert.equal(page.page.value, 1)
})

test('周期性静默校准每 60 秒兜底一次，在途时不叠加请求', async t => {
  const { component: page, request, reply, tick, requests } = await createPage(t)
  const latests = () => requests.filter(entry => entry.url.endsWith('/latest'))
  const baseline = latests().length
  await tick(60000)
  assert.equal(latests().length, baseline + 1, '60 秒后发起一次静默校准')
  const first = latests().at(-1)
  await tick(60000)
  assert.equal(latests().length, baseline + 1, '校准在途时下一轮不再叠加请求')
  await reply(first)
  await tick(60000)
  assert.equal(latests().length, baseline + 2, '校准完成后继续周期调度')
  await reply(latests().at(-1))
})

test('连续实时消息期间总数保持精确，不再长期显示统计中', async t => {
  const { component: page, request, reply, tick, receive } = await createPage(t)
  await tick(300)
  await reply(request('/chat/timeline/count'))
  assert.equal(page.total.value, 150)
  receive(151)
  receive(152)
  assert.equal(page.total.value, 152, '实时消息直接增量补总数，不把 total 打回 null')
  await tick(650)
  await reply(request('/chat/timeline/count'))
  assert.equal(page.total.value, 152)
  assert.equal(page.countFailed.value, false)
})

test('总数统计失败时按 5s/10s 退避自动重试，重置视图后重新预算', async t => {
  const { component: page, request, reply, tick, failures } = await createPage(t)
  failures.add('/chat/timeline/count')
  await tick(300)
  assert.equal(page.countFailed.value, false, '第一次失败先进入退避重试')
  await tick(5000)
  assert.equal(page.countFailed.value, false)
  await tick(10000)
  assert.equal(page.countFailed.value, true, '两次重试仍失败才显示暂无法统计')

  failures.delete('/chat/timeline/count')
  page.refreshLatest()
  await reply(request('/chat/timeline/latest'))
  await tick(300)
  await reply(request('/chat/timeline/count'))
  assert.equal(page.total.value, 150)
  assert.equal(page.countFailed.value, false)
})

test('查找失败后不再停留“正在查找”，重试可恢复结果', async t => {
  const { component: page, request, reply, failures } = await createPage(t)
  page.searchInput.value = '讨论'
  failures.add('/chat/timeline')
  page.applySearch()
  await flush()
  assert.equal(page.loadError.value, true)
  assert.equal(page.total.value, null)

  failures.delete('/chat/timeline')
  page.loadCurrent()
  await reply(request('/chat/timeline'))
  assert.equal(page.loadError.value, false)
  assert.equal(page.items.value.length, 50)
  assert.equal(page.total.value, 150)
})
