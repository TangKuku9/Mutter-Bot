// Keep the saved hue intact. Text accents and filled controls need different
// foregrounds, especially for custom colors close to white, black or yellow.
const channels = (hex) => [1, 3, 5].map(offset => parseInt(hex.slice(offset, offset + 2), 16))
const hexColor = (rgb) => `#${rgb.map(value => Math.round(value).toString(16).padStart(2, '0')).join('')}`

export const mixColor = (color, target, weight) => hexColor(channels(color).map((value, index) =>
  value * (1 - weight) + channels(target)[index] * weight))

const luminance = (color) => channels(color).map(value => {
  const channel = value / 255
  return channel <= 0.04045 ? channel / 12.92 : ((channel + 0.055) / 1.055) ** 2.4
}).reduce((sum, value, index) => sum + value * [0.2126, 0.7152, 0.0722][index], 0)

export const contrastRatio = (a, b) => {
  const values = [luminance(a), luminance(b)].sort((x, y) => y - x)
  return (values[0] + 0.05) / (values[1] + 0.05)
}

const foregroundFor = (background) => {
  if (contrastRatio('#ffffff', background) >= 4.5) return '#ffffff'
  return contrastRatio('#111827', background) >= 4.5 ? '#111827' : '#000000'
}

const readableAccent = (color, backgrounds) => {
  if (backgrounds.every(background => contrastRatio(color, background) >= 4.5)) return color
  const target = foregroundFor(backgrounds[0])
  for (let step = 1; step <= 100; step++) {
    const candidate = mixColor(color, target, step / 100)
    if (backgrounds.every(background => contrastRatio(candidate, background) >= 4.5)) return candidate
  }
  return target
}

export function createPanelTheme(primary, dark) {
  const surface = dark ? '#191d29' : '#ffffff'
  const soft = mixColor(primary, surface, 0.9)
  const hover = mixColor(primary, surface, 0.3)
  const active = mixColor(primary, dark ? '#ffffff' : '#000000', 0.2)
  const text = readableAccent(primary, [surface, soft, dark ? '#222837' : '#f1f3f8', dark ? '#121620' : '#f5f6fa'])
  const theme = {
    '--el-color-primary': primary,
    '--el-color-primary-rgb': channels(primary).join(', '),
    '--el-color-primary-dark-2': active,
    '--panel-accent-text': text,
    '--panel-on-accent': foregroundFor(text),
    '--panel-accent-soft': soft,
    '--panel-accent-border': mixColor(primary, surface, 0.7),
    '--panel-on-primary': foregroundFor(primary),
    '--panel-on-primary-hover': foregroundFor(hover),
    '--panel-on-primary-active': foregroundFor(active)
  }
  for (const step of [3, 5, 7, 8, 9]) theme[`--el-color-primary-light-${step}`] = mixColor(primary, surface, step / 10)
  const statusColors = dark
    ? { success: '#7dd9ae', warning: '#e9bb68', danger: '#fa9aa5', info: '#a6b1c6' }
    : { success: '#27845b', warning: '#98620c', danger: '#c84452', info: '#67758b' }
  for (const [name, color] of Object.entries(statusColors)) {
    theme[`--el-color-${name}`] = color
    theme[`--el-color-${name}-dark-2`] = mixColor(color, dark ? '#ffffff' : '#000000', 0.2)
    for (const step of [3, 5, 7, 8, 9]) theme[`--el-color-${name}-light-${step}`] = mixColor(color, surface, step / 10)
    theme[`--panel-on-${name}`] = foregroundFor(color)
    theme[`--panel-on-${name}-hover`] = foregroundFor(theme[`--el-color-${name}-light-3`])
    theme[`--panel-on-${name}-active`] = foregroundFor(theme[`--el-color-${name}-dark-2`])
  }
  return theme
}
