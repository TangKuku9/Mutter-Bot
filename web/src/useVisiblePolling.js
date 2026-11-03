import { onMounted, onUnmounted } from 'vue'

// 上一次完成后再计时；后台标签暂停，回来立即补查。
export function useVisiblePolling(load, interval) {
  let timer = null
  let controller = null
  let stopped = true
  let running = false
  const refresh = async () => {
    clearTimeout(timer)
    if (stopped || document.hidden || running) return
    running = true
    controller = new AbortController()
    try { await load(controller.signal) } catch { /* 页面显示状态；自动刷新不叠加错误弹窗 */ }
    finally {
      running = false
      if (!stopped && !document.hidden) timer = setTimeout(refresh, interval)
    }
  }
  const visibilityChanged = () => {
    clearTimeout(timer)
    if (!document.hidden) refresh()
  }
  onMounted(() => {
    stopped = false
    document.addEventListener('visibilitychange', visibilityChanged)
    refresh()
  })
  onUnmounted(() => {
    stopped = true
    clearTimeout(timer)
    controller?.abort()
    document.removeEventListener('visibilitychange', visibilityChanged)
  })
  return refresh
}
