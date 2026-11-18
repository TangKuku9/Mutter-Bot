# AI 回复来源链接排版

## 群内 @ 联网回复

打开面板「AI 设置」中的联网搜索，再到「群管理 → QQ Markdown」启用全部群或选择要启用的群。也可以从「AI 设置 → 联网与检索」直接打开这项设置。

群内 @ Mutter 触发检索后，有结果时会按 QQ Markdown 输出答案和检索来源。独立搜索使用 SearXNG、Tavily、博查或自定义搜索时可附一张检索图。SearXNG 实例需要启用 JSON 以及图片分类的搜索引擎。

服务商内建搜索有结构化出处时，Mutter 会尝试读取前两条出处的公开 HTTPS 网页，选择一张网页声明的预览图（例如 Open Graph 的 `og:image`），或使用出处直接指向的图片。明确请求看图时，内建搜索提示词会引导模型优先引用能展示该主体的来源页。已有的 Markdown 配置即可启用，配图步骤无需额外模型调用。出处缺失、网页不提供配图或读取失败时，继续发送可用的文字和来源。

自定义 JSON API 的响应可以在 `results` 旁添加可选 `images` 数组。条目可用 HTTPS 图片地址字符串，或 `{"url":"https://cdn.example.org/panda.png","source_url":"https://example.org/panda","width":640,"height":480}` 对象。图片应当可由 QQ 访问，平台会转存图片。正文、来源与配图合成一条被动回复。群聊搜索结果没有合格图片或 QQ 明确表示图片转存失败时，发送无图 Markdown。QQ 明确拒绝 Markdown 时退纯文字，结果未知时保留原发送意图。

## 回复本身带有来源链接

当 AI 回复本身带有联网搜索来源时，Mutter 会把合法链接整理成 QQ 原生 Markdown：

```text
正文内容……

来源：[gold.org](https://www.gold.org/…)
```

## 行为边界

- 只在回复确实包含合法 `http`/`https` 链接时启用，普通闲聊仍发送纯文本。
- 正文中的 `[域名](链接)` 和裸 URL 会移到末尾的来源行；相同 URL 去重，最多展示 5 个来源。
- 链接只作为展示内容处理，Mutter 不会访问或抓取它，也不会因此额外调用模型。
- 来源行使用 QQ 的 `msg_type=2` Markdown；平台没有 Markdown 权限或明确拒绝时，自动回退为原始纯文本。
- 网络错误、超时、5xx 或“发送结果未知”不会触发重发，保持原有投递幂等语义。
- 面板设置默认「沿用配置文件」，继续使用 `Mutter:RichUi:Enabled`、`Markdown` 和 `GroupAllowList`。从面板选定的范围保存后即时生效，也适用于群聊联网配图和表情包。

## 验证

`tools/ProactiveCheck` 的“AI 回复来源链接的 Markdown 排版与安全降级”覆盖链接提取、去重、非法协议、空引用包装、Markdown 被拒后的纯文本回退，以及回执未知时不重发。
