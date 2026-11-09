import test from 'node:test'
import assert from 'node:assert/strict'
import { createPanelTheme, contrastRatio } from '../src/panelTheme.js'

test('预设与极端自定义主题色在深浅底上保留可读的文字和按钮', () => {
  const colors = ['#4f46e5', '#409eff', '#13c2c2', '#67c23a', '#e6a23c', '#f56c6c', '#8b5cf6', '#ffffff', '#000000', '#ffff00', '#777777']
  for (const dark of [false, true]) {
    const surface = dark ? '#191d29' : '#ffffff'
    for (const color of colors) {
      const theme = createPanelTheme(color, dark)
      assert.equal(theme['--el-color-primary'], color, '不能改写用户选择的主题色')
      for (const background of [surface, theme['--panel-accent-soft'], dark ? '#222837' : '#f1f3f8', dark ? '#121620' : '#f5f6fa']) {
        assert.ok(contrastRatio(theme['--panel-accent-text'], background) >= 4.5, `${color} 的文字在 ${background} 上应可读`)
      }
      for (const [background, foreground] of [
        [color, theme['--panel-on-primary']],
        [theme['--el-color-primary-light-3'], theme['--panel-on-primary-hover']],
        [theme['--el-color-primary-dark-2'], theme['--panel-on-primary-active']]
      ]) assert.ok(contrastRatio(foreground, background) >= 4.5, `${color} 的按钮状态应可读`)
      assert.ok(contrastRatio(theme['--panel-on-accent'], theme['--panel-accent-text']) >= 4.5)
      for (const name of ['success', 'warning', 'danger', 'info']) {
        for (const [background, foreground] of [
          [theme[`--el-color-${name}`], theme[`--panel-on-${name}`]],
          [theme[`--el-color-${name}-light-3`], theme[`--panel-on-${name}-hover`]],
          [theme[`--el-color-${name}-dark-2`], theme[`--panel-on-${name}-active`]]
        ]) assert.ok(contrastRatio(foreground, background) >= 4.5, `${name} 按钮在深浅主题均应可读`)
      }
    }
  }
})

test('明暗切换重新生成色阶，且只输出合法颜色', () => {
  const light = createPanelTheme('#13c2c2', false)
  const dark = createPanelTheme('#13c2c2', true)
  assert.notEqual(light['--panel-accent-soft'], dark['--panel-accent-soft'])
  assert.notEqual(light['--el-color-primary-light-3'], dark['--el-color-primary-light-3'])
  for (const theme of [light, dark]) {
    for (const [name, value] of Object.entries(theme)) {
      if (name !== '--el-color-primary-rgb') assert.match(value, /^#[0-9a-f]{6}$/i)
    }
  }
})
