import { reactive, watch } from 'vue'

const STORAGE_KEY = 'mutter-panel-settings'

export const colorPresets = [
  { name: '拂晓蓝', value: '#409eff' },
  { name: '松石绿', value: '#13c2c2' },
  { name: '薄荷绿', value: '#67c23a' },
  { name: '暖橘', value: '#e6a23c' },
  { name: '珊瑚红', value: '#f56c6c' },
  { name: '罗兰紫', value: '#8b5cf6' }
]

const DEFAULTS = { primaryColor: '#409eff', darkMode: false, compact: false }

const load = () => {
  try {
    const saved = JSON.parse(localStorage.getItem(STORAGE_KEY) || '{}')
    return {
      primaryColor: /^#[0-9a-f]{6}$/i.test(saved.primaryColor || '') ? saved.primaryColor : DEFAULTS.primaryColor,
      darkMode: saved.darkMode === true,
      compact: saved.compact === true
    }
  } catch {
    return { ...DEFAULTS }
  }
}

export const panelSettings = reactive(load())

const parseHex = (hex) => {
  const value = hex.replace('#', '')
  return [0, 2, 4].map((i) => parseInt(value.slice(i, i + 2), 16))
}

const mix = (hex, target, weight) => {
  const [r1, g1, b1] = parseHex(hex)
  const [r2, g2, b2] = parseHex(target)
  const channel = (a, b) => Math.round(a * (1 - weight) + b * weight)
  return `rgb(${channel(r1, r2)}, ${channel(g1, g2)}, ${channel(b1, b2)})`
}

export const applyPanelSettings = () => {
  const root = document.documentElement
  const dark = panelSettings.darkMode
  const primary = panelSettings.primaryColor
  // 深色主题下 Element Plus 的浅色阶是与深色底混合，而不是与白色混合
  const base = dark ? '#141414' : '#ffffff'

  root.classList.toggle('dark', dark)
  root.classList.toggle('compact', panelSettings.compact)
  root.style.setProperty('--el-color-primary', primary)
  for (const step of [3, 5, 7, 8, 9]) {
    root.style.setProperty(`--el-color-primary-light-${step}`, mix(primary, base, step / 10))
  }
  root.style.setProperty('--el-color-primary-dark-2', mix(primary, dark ? '#ffffff' : '#000000', 0.2))
}

export const resetPanelSettings = () => Object.assign(panelSettings, DEFAULTS)

watch(
  panelSettings,
  () => {
    applyPanelSettings()
    try {
      localStorage.setItem(STORAGE_KEY, JSON.stringify({ ...panelSettings }))
    } catch {
      // 本地存储不可用时，设置只在当前会话生效
    }
  },
  { deep: true }
)

applyPanelSettings()
