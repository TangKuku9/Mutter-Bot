import { reactive } from 'vue'
import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr'

// 整个面板共用一条连接，页面离开时只退订自己的事件。
export const realtimeState = reactive({ connected: false })
const listeners = new Map()
let connection = null
let starting = null
let retryTimer = null
let consumers = 0
let logConsumers = 0
let retry = 0
const events = ['ReceiveMessage', 'AiResponseStarted', 'AiResponseCompleted', 'ConnectionStatusChanged',
  'ProactiveEvaluated', 'ReceiveLogs', 'ReceiveLog']

function emit(event, ...args) {
  for (const listener of listeners.get(event) || []) {
    Promise.resolve().then(() => listener(...args)).catch(() => {})
  }
}

async function subscribeLogs() {
  if (logConsumers && realtimeState.connected) await connection.invoke('SubscribeLogs').catch(() => {})
}

async function start() {
  if (!consumers || starting || connection?.state === 'Connected' || connection?.state === 'Reconnecting') return
  clearTimeout(retryTimer)
  if (!connection) {
    connection = new HubConnectionBuilder().withUrl('/hub/botstatus')
      .configureLogging(LogLevel.None).withAutomaticReconnect().build()
    for (const event of events) connection.on(event, (...args) => emit(event, ...args))
    connection.onreconnecting(() => { realtimeState.connected = false })
    connection.onreconnected(async () => {
      realtimeState.connected = true
      retry = 0
      await subscribeLogs()
      emit('Resync')
    })
    connection.onclose(() => { realtimeState.connected = false; scheduleRetry() })
  }
  starting = connection.start()
  try {
    await starting
    if (!consumers) { await connection.stop(); return }
    realtimeState.connected = true
    retry = 0
    await subscribeLogs()
    emit('Resync')
  } catch { scheduleRetry() }
  finally { starting = null }
}

function scheduleRetry() {
  clearTimeout(retryTimer)
  if (consumers) retryTimer = setTimeout(start, Math.min(30000, 2000 * 2 ** Math.min(retry++, 4)))
}

export function subscribeRealtime(handlers, { logs = false } = {}) {
  consumers++
  if (logs) logConsumers++
  for (const [event, listener] of Object.entries(handlers)) {
    if (!listeners.has(event)) listeners.set(event, new Set())
    listeners.get(event).add(listener)
  }
  if (logs && logConsumers === 1 && realtimeState.connected) subscribeLogs()
  start()
  let disposed = false
  return () => {
    if (disposed) return
    disposed = true
    consumers--
    for (const [event, listener] of Object.entries(handlers)) listeners.get(event)?.delete(listener)
    if (logs && --logConsumers === 0 && realtimeState.connected) connection.invoke('UnsubscribeLogs').catch(() => {})
    if (!consumers) {
      clearTimeout(retryTimer)
      connection?.stop().catch(() => {})
      realtimeState.connected = false
    }
  }
}
