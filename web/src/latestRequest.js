// 即使服务端来不及取消，也只允许最后一次查询更新视图。
export class LatestRequest {
  #sequence = 0
  #controller = null
  pending = false

  async run(fetch, apply) {
    this.#controller?.abort()
    const controller = new AbortController()
    this.#controller = controller
    const sequence = ++this.#sequence
    this.pending = true
    try {
      const result = await fetch(controller.signal)
      if (sequence === this.#sequence && !controller.signal.aborted) apply(result)
    } catch (error) {
      if (!controller.signal.aborted && sequence === this.#sequence) throw error
    } finally {
      if (sequence === this.#sequence) this.pending = false
    }
  }

  cancel() {
    ++this.#sequence
    this.#controller?.abort()
    this.pending = false
  }
}

export function matchesChatMessage(message, { groupId = '', source = '' } = {}) {
  if (groupId && message.groupId !== groupId) return false
  if (source === 'at') return message.chatType === 0 && !message.isBotMessage && message.hasAtBot
  if (source === 'observe') return message.chatType === 0 && !message.isBotMessage && !message.hasAtBot
  if (source === 'bot') return message.isBotMessage && !message.isProactive
  if (source === 'proactive') return message.isBotMessage && message.isProactive
  return true
}
