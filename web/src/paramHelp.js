import { ref, computed } from 'vue'

/**
 * 参数说明抽屉的共享状态。
 *
 * @param HELP   形如 { key: { title, lines, value(form) } }
 * @param ORDER  抽屉里「上一项 / 下一项」的翻页顺序，取面板上参数的出现顺序
 * @param values 返回当前表单快照的函数，抽屉里用它显示「当前值」
 */
export function useParamHelp(HELP, ORDER, values) {
  const visible = ref(false)
  const key = ref(ORDER[0])

  const index = computed(() => ORDER.indexOf(key.value))
  const title = computed(() => HELP[key.value].title)
  const lines = computed(() => HELP[key.value].lines)
  const current = computed(() => {
    const form = values()
    return form == null ? '—' : HELP[key.value].value(form)
  })

  const label = (k) => `${HELP[k].title} 说明`
  const open = (k) => { key.value = k; visible.value = true }
  const step = (d) => {
    const i = index.value + d
    if (i >= 0 && i < ORDER.length) key.value = ORDER[i]
  }

  return {
    visible, index, title, lines, current, label, open, step,
    total: ORDER.length
  }
}
