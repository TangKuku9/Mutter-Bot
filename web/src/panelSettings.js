import { reactive, watch } from 'vue'
import { createPanelTheme } from './panelTheme'

const STORAGE_KEY = 'mutter-panel-settings'

export const colorPresets = [
  { name: 'Mutter 靛紫', value: '#4f46e5' },
  { name: '拂晓蓝', value: '#409eff' },
  { name: '松石绿', value: '#13c2c2' },
  { name: '薄荷绿', value: '#67c23a' },
  { name: '暖橘', value: '#e6a23c' },
  { name: '珊瑚红', value: '#f56c6c' },
  { name: '罗兰紫', value: '#8b5cf6' }
]

const DEFAULTS = { primaryColor: '#4f46e5', darkMode: false, compact: false }

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

export const applyPanelSettings = () => {
  const root = document.documentElement
  const dark = panelSettings.darkMode
  const primary = panelSettings.primaryColor
  root.classList.toggle('dark', dark)
  root.classList.toggle('compact', panelSettings.compact)
  for (const [name, value] of Object.entries(createPanelTheme(primary, dark))) root.style.setProperty(name, value)
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
