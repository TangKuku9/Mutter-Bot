# Mutter 品牌资产

## 概念

Mutter 既是“喃喃低语”，也是群聊里那个先看气氛、再决定要不要开口的助手。标志把这两层意思收进同一个形状：

- **对话气泡**：群聊、发言、被 @ 与主动接话。
- **小写 m**：Mutter 的首字母；三个竖笔同时读作一段被压低的声音／声波。
- **圆角与短尾巴**：保留“轻声说话”的柔和感，避免机器人式的尖锐轮廓。

字标使用 **Manrope ExtraBold 800**，已转换为路径，不依赖查看者本机是否安装字体。

## 文件

| 文件 | 用途 |
| --- | --- |
| `logo-mark.svg` | 主标（渐变气泡 + 白色 m），透明背景；侧栏、关于页、文档 |
| `logo-mark-mono.svg` | 单色墨版，浅色底／印刷 |
| `logo-mark-mono-white.svg` | 单色白版，渐变、玻璃、深色底 |
| `logo-lockup.svg` | 横版组合，浅色背景 |
| `logo-lockup-dark.svg` | 横版组合，深色背景 |
| `logo-lockup-mono.svg` | 单色横版，浅色底 |
| `logo-lockup-mono-reverse.svg` | 单色横版，深色底 |
| `app-icon.svg` | 1024 应用图标（渐变磁贴 + 白色气泡） |
| `social-preview.svg` | GitHub 社交预览卡（1280×640）源文件 |
| `brand-sheet.svg` / `.png` | 品牌视觉规范总览 |
| `png/` | 各尺寸 PNG、`favicon.ico` 导出 |

应用图标是主标的“反白”用法：白色气泡 + 镂空 m，露出底层渐变。登录页的玻璃底和渐变底也使用白色单色标，保持同一套逻辑。

## 色彩

| Token | 值 | 用途 |
| --- | --- | --- |
| Indigo | `#4F46E5` | 主色、渐变起点 |
| Violet | `#7C3AED` | 辅色、渐变终点 |
| Deep Indigo | `#4338CA` | 应用图标渐变起点 |
| Ink | `#0F172A` | 字标、单色标、正文 |
| Paper | `#F8FAFC` | 深色底上的字标 |
| Cyan（辅助） | `#22D3EE` | 仅登录页／社交卡的极光氛围，不进入标志 |

主渐变方向为 135°（左上 → 右下）。标志本身只用 Indigo → Violet 两色。

## 字体

- 字标：Manrope ExtraBold 800（SIL OFL 1.1）。
- 面板 UI：Inter / 系统 sans-serif。
- 中文：PingFang SC / Microsoft YaHei / Noto Sans SC。
- 字标已转路径；不要把“Mutter”重新排成普通文本作为标志使用。

## 使用规则

- **最小尺寸**：图形 16px；横版建议不小于 120px 宽。16px 时优先用 `app-icon.svg`（白色气泡在彩色磁贴上更清楚）。
- **留白**：图形四周至少保留一个 m 的高度；横版右侧留出与左侧气泡外边距相当的空间。
- **背景**：浅色纯色底用主标；深色底用 `logo-lockup-dark.svg`；彩色／玻璃／照片底用白色单色标，必要时加暗色遮罩。
- **不要**：拉伸、描边、旋转、加投影或外发光；不要改变 m 与气泡的颜色关系；不要把 m 单独拿出来当标志；不要用其他字体重排字标。
- 应用图标已经包含圆角，不要在外部再套一层不同半径的圆角矩形。

## 在项目中的接入

- README 顶部：`<picture>` 根据 `prefers-color-scheme` 切换浅色／深色横版。
- 管理面板侧栏与关于页：`web/public/logo-mark.svg`。
- 登录页玻璃底：`web/public/logo-mark-mono-white.svg`。
- favicon：`web/public/favicon.svg`、`favicon-32.png`、`favicon.ico`、`apple-touch-icon.png`。
- Windows 可执行文件图标：`Mutter.ConsoleHost.csproj` 的 `<ApplicationIcon>` 指向 `brand/png/favicon.ico`。
- GitHub 仓库头像 / 社交预览：上传 `png/app-icon-1024.png` 与 `png/social-preview-1280x640.png`。

## 许可与维护

- 字标字体 Manrope 采用 SIL Open Font License 1.1：<https://github.com/sharanda/manrope>。
- 图形为手工矢量路径，画布 1024×1024；m 采用光学居中（相对气泡中心偏右 10、偏上 6 个画布单位）。修改时保持这两条约束，避免小尺寸下重心偏移。
- PNG 由 SVG 渲染导出；优先修改 SVG，再重新导出位图。
