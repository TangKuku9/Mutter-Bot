# Mutter 品牌资产

## 概念：接话

Mutter 是群聊里先观察气氛、再恰当地接话的伙伴。品牌主张是 **「懂群聊，也懂分寸。」**

现行标志采用选定的「02 · 接话」方向：两笔圆润的弧线错位呼应，在彼此之间留出空间；整体轮廓联想到小写 m 和轻声起伏。标志本身使用纯色，不依赖渐变、容器或内部文字识别。

字标沿用 **Manrope ExtraBold 800**，以轮廓路径提供，不依赖本机字体。图形与字标分别保存在 `source/turn-taking.svg` 和 `source/wordmark.svg`，其余资产统一由脚本导出。

## 文件

| 文件 | 用途 |
| --- | --- |
| `source/turn-taking.svg` | 两笔接话图形的矢量母版 |
| `source/wordmark.svg` | 已转路径的 Mutter 字标母版 |
| `logo-mark.svg` | 靛紫主标，透明背景 |
| `logo-mark-mono.svg` | 单色墨版，浅色底／印刷 |
| `logo-mark-mono-white.svg` | 单色白版，深色／彩色底 |
| `logo-lockup.svg` | 浅底横版：靛紫图形 + 墨色字标 |
| `logo-lockup-dark.svg` | 深底横版：靛紫图形 + 浅色字标 |
| `logo-lockup-mono.svg` | 墨色单色横版 |
| `logo-lockup-mono-reverse.svg` | 白色单色横版 |
| `app-icon.svg` | 靛紫磁贴 + 白色接话图形 |
| `social-preview.svg` | GitHub 社交预览卡，1280×640 |
| `brand-sheet.svg` / `.png` | 品牌视觉规范总览 v2.0 |
| `png/` | PNG 导出和包含 16～256px 帧的 Windows / 浏览器 ICO |

## 色彩与字体

| Token | 值 | 用途 |
| --- | --- | --- |
| Indigo | `#4F46E5` | 主标、应用图标底色、面板默认主色 |
| Violet | `#7C3AED` | 登录页等背景的辅助氛围 |
| Ink | `#0F172A` | 字标、单色标、正文 |
| Paper | `#F8FAFC` | 深底上的字标与文字 |

- 主标和应用图标使用实色；渐变仅用于背景。
- 字标：Manrope ExtraBold 800（SIL OFL 1.1）。
- 面板 UI：Inter / 系统 sans-serif；中文使用 PingFang SC / Microsoft YaHei / Noto Sans SC。
- 面板保留用户已保存的主题选择；新安装与恢复默认使用 Mutter 靛紫。

## 使用规则

- **最小尺寸**：图形 16px；横版建议不小于 120px 宽。32px 及以下的应用图标与 favicon 放大内部图形，减少小尺寸下的留白损失。
- **留白**：图形四周至少保留一笔的宽度。
- **背景**：浅底用主标；深底用深色横版；彩色／玻璃底用白色单色版。
- **形状**：保持两笔的错位、比例、圆角与间隙；不要拉伸、旋转、添加描边、阴影或外发光。
- **字标**：使用提供的路径，不要用普通文本重新排字替代。
- **应用图标**：已经包含圆角，不要再次包入另一层圆角磁贴。

## 生成与接入

```powershell
npm --prefix web ci
npm --prefix web run brand:generate
```

脚本 `web/scripts/generate-brand.mjs` 使用固定版本的 `@resvg/resvg-js`，从两份矢量母版导出所有 SVG、PNG 与 ICO，并同步 `web/public/` 中的副本。规范图和社交卡的中文文字需要系统中有相应中文字体。

- README：按系统深浅色切换横版。
- 面板侧栏、关于页：使用转路径横版，跟随面板深浅色设置。
- 登录页桌面品牌、移动端品牌与示例头像：使用同一套接话标志。
- 站点：SVG / 16px / 32px favicon、ICO 与 Apple Touch Icon 全部同步。
- Windows EXE：`Mutter.ConsoleHost.csproj` 的 `ApplicationIcon` 指向 `brand/png/favicon.ico`。
- 前端构建从 `Directory.Build.props` 注入登录页版本和品牌 URL 版本参数，更新后请求新资源。
- GitHub 社交预览：在仓库 Settings → General → Social preview 上传 `png/social-preview-1280x640.png`；Git 提交不会自动更新此设置。

字标字体许可：<https://github.com/sharanda/manrope>。品牌母版与全部派生资产采用本仓库的许可。
