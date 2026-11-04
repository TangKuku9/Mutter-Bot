export function tokenBudget(form) {
  const context = Number(form.contextWindowTokens ?? 32768)
  const output = Number(form.maxTokens ?? 2048)
  const input = Number(form.maxInputTokens ?? 16384)
  const reserve = 1024
  const availableInput = context - output - reserve
  let error = ''
  if (![context, output, input].every(Number.isInteger)) error = '请填写完整的整数预算。'
  else if (context < 4096 || context > 1048576) error = '模型上下文容量需在 4096～1048576 之间。'
  else if (output < 64 || output > 32768) error = '回复输出上限需在 64～32768 之间。'
  else if (input < 512) error = '输入预算至少为 512。'
  else if (input > availableInput) error = `输入与输出合计超出容量：当前最多可分配 ${Math.max(0, availableInput)} 给输入。`
  return { context, output, input, reserve, availableInput, error,
    percent: Math.min(100, Math.round((input + output + reserve) / context * 100)) }
}
