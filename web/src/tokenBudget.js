export const DEFAULT_INPUT_BUDGET = 24576

export function tokenBudget(form) {
  const context = Number(form.contextWindowTokens ?? 32768)
  const output = Number(form.maxTokens ?? 2048)
  const input = Number(form.maxInputTokens ?? DEFAULT_INPUT_BUDGET)
  const reserve = 1024
  const availableInput = context - output - reserve
  let error = ''
  if (![context, output, input].every(Number.isInteger)) error = '请填写完整的整数预算。'
  else if (context < 4096 || context > 1048576) error = '模型上下文容量需在 4096～1048576 之间。'
  else if (output < 64 || output > 32768) error = '回复输出上限需在 64～32768 之间。'
  // 容量扣掉输出与安全预留后不足最小输入预算时，调输入框无解（min=max=512）：
  // 必须提示用户动容量或输出，而不是给出与输入框 min=512 冲突的「最多可分配 N」。
  else if (availableInput < 512) error = `模型上下文容量不足：输出上限与安全预留后只剩 ${Math.max(0, availableInput)} Token，请提高上下文容量或降低回复输出上限。`
  else if (input < 512) error = '输入预算至少为 512。'
  else if (input > availableInput) error = `输入与输出合计超出容量：当前最多可分配 ${availableInput} 给输入。`
  return { context, output, input, reserve, availableInput, error,
    percent: Math.min(100, Math.round((input + output + reserve) / context * 100)) }
}
