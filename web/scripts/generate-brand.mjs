import { readFileSync, writeFileSync, mkdirSync, copyFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { resolve } from 'node:path'
import { Resvg } from '@resvg/resvg-js'

// Source geometry is shared by every export; generated SVGs remain self-contained.
const root = fileURLToPath(new URL('../../', import.meta.url))
const brand = resolve(root, 'brand')
const pngDir = resolve(brand, 'png')
const publicDir = resolve(root, 'web/public')
const inner = name => readFileSync(resolve(brand, 'source', name), 'utf8')
  .replace(/^[\s\S]*?<svg[^>]*>/, '').replace(/<\/svg>[\s\S]*$/, '')
  .replace(/<(title|desc)>[\s\S]*?<\/\1>/g, '').trim()
const symbol = inner('turn-taking.svg')
const wordmark = inner('wordmark.svg')
const indigo = '#4F46E5'
const ink = '#0F172A'
const paper = '#F8FAFC'
const font = 'Segoe UI, Microsoft YaHei, Noto Sans CJK SC, sans-serif'
const mark = (x, y, width, color) => `<g transform="translate(${x} ${y}) scale(${width / 400})" color="${color}">${symbol}</g>`
const word = (x, y, width, color) => `<g transform="translate(${x} ${y}) scale(${width / 4415})" color="${color}">${wordmark}</g>`
const svg = (width, height, title, body) => `<svg xmlns="http://www.w3.org/2000/svg" width="${width}" height="${height}" viewBox="0 0 ${width} ${height}" role="img" aria-labelledby="title"><title id="title">${title}</title>${body}</svg>\n`
const text = (x, y, size, content, color = '#64748B', weight = 400, anchor = 'start') => `<text x="${x}" y="${y}" fill="${color}" font-family="${font}" font-size="${size}" font-weight="${weight}" text-anchor="${anchor}">${content}</text>`
const tile = (x, y, size, small = false) => `<g transform="translate(${x} ${y}) scale(${size / 1024})"><rect width="1024" height="1024" rx="224" fill="${indigo}"/>${mark(small ? 112 : 160, small ? 248 : 279.68, small ? 800 : 704, '#FFFFFF')}</g>`
const lockup = (color, markColor = indigo) => svg(640, 160, 'Mutter — 接话', mark(24, 33.8, 140, markColor) + word(204, 32, 412, color))
const render = (source, width) => new Resvg(source, { fitTo: { mode: 'width', value: width }, font: { loadSystemFonts: true, defaultFontFamily: 'Microsoft YaHei' } }).render().asPng()
const saveSvg = (name, source) => writeFileSync(resolve(brand, name), source)
const savePng = (name, source, width) => writeFileSync(resolve(pngDir, name), render(source, width))

mkdirSync(pngDir, { recursive: true })
const primary = svg(1024, 1024, 'Mutter 接话标志', mark(112, 248, 800, indigo))
const mono = svg(1024, 1024, 'Mutter 接话标志 — 单色墨版', mark(112, 248, 800, ink))
const white = svg(1024, 1024, 'Mutter 接话标志 — 单色白版', mark(112, 248, 800, '#FFFFFF'))
const app = svg(1024, 1024, 'Mutter 应用图标', tile(0, 0, 1024))
const favicon = svg(1024, 1024, 'Mutter 站点图标 — 小尺寸光学校正', tile(0, 0, 1024, true))
const assets = {
  'logo-mark.svg': primary,
  'logo-mark-mono.svg': mono,
  'logo-mark-mono-white.svg': white,
  'logo-lockup.svg': lockup(ink),
  'logo-lockup-dark.svg': lockup(paper),
  'logo-lockup-mono.svg': lockup(ink, ink),
  'logo-lockup-mono-reverse.svg': lockup('#FFFFFF', '#FFFFFF'),
  'app-icon.svg': app
}
for (const [name, source] of Object.entries(assets)) saveSvg(name, source)
for (const size of [16, 24, 32, 48, 64, 128, 180, 192, 256, 512, 1024]) savePng(`app-icon-${size}.png`, size <= 32 ? favicon : app, size)
for (const size of [16, 24, 32, 48, 512, 1024]) savePng(`logo-mark-${size}.png`, primary, size)
savePng('logo-mark-mono-1024.png', mono, 1024)
for (const name of ['logo-lockup', 'logo-lockup-dark', 'logo-lockup-mono', 'logo-lockup-mono-reverse']) savePng(`${name}-1200.png`, assets[`${name}.svg`], 1200)

// PNG-compressed ICO frames are supported by Windows and preserve each small-size export.
const iconSizes = [16, 24, 32, 48, 64, 128, 256]
const frames = iconSizes.map(size => readFileSync(resolve(pngDir, `app-icon-${size}.png`)))
const header = Buffer.alloc(6 + frames.length * 16)
header.writeUInt16LE(1, 2)
header.writeUInt16LE(frames.length, 4)
let offset = header.length
frames.forEach((frame, index) => {
  const pos = 6 + index * 16
  header[pos] = iconSizes[index] % 256
  header[pos + 1] = iconSizes[index] % 256
  header.writeUInt16LE(1, pos + 4)
  header.writeUInt16LE(32, pos + 6)
  header.writeUInt32LE(frame.length, pos + 8)
  header.writeUInt32LE(offset, pos + 12)
  offset += frame.length
})
writeFileSync(resolve(pngDir, 'favicon.ico'), Buffer.concat([header, ...frames]))

const social = svg(1280, 640, 'Mutter — 懂群聊，也懂分寸。', `
<defs><radialGradient id="ambience"><stop stop-color="#4F46E5" stop-opacity="0.28"/><stop offset="1" stop-color="#4F46E5" stop-opacity="0"/></radialGradient></defs>
<rect width="1280" height="640" fill="#0B1020"/>
<ellipse cx="1090" cy="130" rx="620" ry="540" fill="url(#ambience)"/>
<g opacity="0.035">${mark(770, -50, 720, '#FFFFFF')}</g>
${text(72, 76, 15, 'QQ 群聊智能助手', '#A5B4FC', 600)}
${mark(362, 161, 152, '#FFFFFF')}${word(554, 161, 364, '#FFFFFF')}
${text(640, 353, 43, '懂群聊，也懂分寸。', paper, 700, 'middle')}
${text(640, 406, 21, '先看气氛，再恰到好处地接话。', '#94A3B8', 400, 'middle')}
<path d="M72 548H1208" stroke="#25304A"/>
${text(72, 589, 16, 'github.com/TangKuku9/Mutter-Bot', '#A5B4FC')}
${text(1208, 589, 16, '多模态理解 · 择机发言 · 本地部署', '#94A3B8', 400, 'end')}`)
saveSvg('social-preview.svg', social)
savePng('social-preview-1280x640.png', social, 1280)

let sheet = `<rect width="1600" height="1200" fill="#F5F6FB"/>`
sheet += mark(64, 53, 44, indigo) + word(124, 53, 139, ink)
sheet += text(64, 112, 12, 'Brand guidelines · v2.0 / 接话', '#94A3B8')
sheet += text(1536, 76, 30, '品牌视觉规范', ink, 700, 'end')
sheet += text(1536, 108, 13, '懂群聊，也懂分寸。', '#64748B', 400, 'end')
sheet += `<rect x="64" y="144" width="1472" height="276" rx="24" fill="#FFFFFF"/><rect x="880" y="174" width="608" height="216" rx="20" fill="#0B1020"/>`
sheet += mark(120, 221, 168, indigo) + word(330, 230, 370, ink)
sheet += mark(930, 232, 132, '#FFFFFF') + word(1100, 237, 316, '#FFFFFF')
sheet += text(120, 387, 13, '主横版 · 浅底') + text(920, 375, 12, '单色白版 · 深底', '#94A3B8')
sheet += `<rect x="64" y="444" width="720" height="356" rx="24" fill="#FFFFFF"/><rect x="816" y="444" width="720" height="356" rx="24" fill="#FFFFFF"/>`
sheet += text(100, 490, 19, '图形系统', ink, 700)
sheet += mark(112, 553, 140, indigo) + mark(300, 553, 120, ink)
sheet += `<rect x="460" y="530" width="126" height="126" rx="24" fill="#0B1020"/>` + mark(478, 563, 90, '#FFFFFF') + tile(626, 516, 142)
for (const [x, label] of [[108, '主标 · 实色'], [292, '单色 · 浅底'], [460, '单色 · 深底'], [626, '应用图标']]) sheet += text(x, 698, 12, label)
sheet += text(100, 752, 14, '两笔错位，留出接话的空间；轮廓呼应 m 与轻声起伏。')
sheet += text(852, 490, 19, '色彩与字体', ink, 700)
for (const [index, [name, color, role]] of [['Indigo', indigo, '主标 / 主交互'], ['Violet', '#7C3AED', '辅助氛围'], ['Ink', ink, '正文 / 单色'], ['Paper', paper, '深底文字']].entries()) {
  const x = 852 + index * 164
  sheet += `<rect x="${x}" y="520" width="144" height="64" rx="12" fill="${color}" stroke="#E2E8F0"/>`
  sheet += text(x, 610, 13, name, ink, 600) + text(x, 632, 12, color) + text(x, 654, 12, role)
}
sheet += word(852, 688, 280, ink) + text(852, 777, 12, '字标 Manrope ExtraBold 800 · UI 系统字体 · 标志使用纯色')
sheet += `<rect x="64" y="824" width="720" height="296" rx="24" fill="#FFFFFF"/><rect x="816" y="824" width="720" height="296" rx="24" fill="#FFFFFF"/>`
sheet += text(100, 874, 19, '尺寸阶梯', ink, 700)
for (const [index, size] of [96, 64, 40, 24, 16].entries()) {
  const x = [108, 242, 344, 420, 482][index]
  sheet += `<g transform="translate(${x} 917) scale(${size / 1024})">${mark(112, 248, 800, indigo)}</g>` + text(x, 1041, 12, `${size}px`)
}
for (const [index, size] of [48, 32, 16].entries()) {
  const x = [572, 650, 714][index]
  sheet += tile(x, 941, size, size <= 32) + text(x, 1041, 12, `${size}px`)
}
sheet += text(108, 1085, 13, '图形最小 16px；32px 及以下的应用图标放大标志，保留间隙。')
sheet += text(852, 874, 19, '使用规则', ink, 700)
for (const [index, line] of ['留白：图形四周至少保留一笔的宽度。', '浅底用靛紫；深底 / 彩色底用单色白版。', '保持两笔的错位与间隙，不加描边、阴影或外发光。', '字标使用已转路径的 Manrope；不要重新排字替代。'].entries()) {
  const y = 923 + index * 43
  sheet += `<circle cx="859" cy="${y - 5}" r="3" fill="${indigo}"/>` + text(875, y, 13, line)
}
sheet += text(64, 1169, 12, 'brand/source 为矢量母版 · npm --prefix web run brand:generate 统一导出 · Manrope: SIL OFL 1.1', '#94A3B8')
const brandSheet = svg(1600, 1200, 'Mutter 接话品牌视觉规范 v2.0', sheet)
saveSvg('brand-sheet.svg', brandSheet)
writeFileSync(resolve(brand, 'brand-sheet.png'), render(brandSheet, 1600))

for (const name of ['logo-mark.svg', 'logo-mark-mono-white.svg', 'logo-lockup.svg', 'logo-lockup-dark.svg', 'logo-lockup-mono-reverse.svg']) copyFileSync(resolve(brand, name), resolve(publicDir, name))
writeFileSync(resolve(publicDir, 'favicon.svg'), favicon)
for (const [source, dest] of [['app-icon-16.png', 'favicon-16.png'], ['app-icon-32.png', 'favicon-32.png'], ['app-icon-180.png', 'apple-touch-icon.png'], ['favicon.ico', 'favicon.ico']]) copyFileSync(resolve(pngDir, source), resolve(publicDir, dest))
console.log('Regenerated all Mutter SVG / PNG / ICO brand assets and web/public mirrors.')
