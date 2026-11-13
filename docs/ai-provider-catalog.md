# 内置服务商与模型目录

核查日期：2026-11-13。共 17 家内置服务商（中国 9 家、国际 8 家），另保留自定义接入。

这些是创建连接时的起始目录。模型 ID 按各服务商实际名称保存，包括大小写和厂商前缀；不混入向量、图片生成、转写等专用模型。账号、地域、模型开通和调用权限仍以服务商控制台与获取到的模型列表为准。

| 服务商 | 分组 | 默认模型 | 起始模型目录 | 来源 |
|---|---|---|---|---|
| 通义千问（百炼） | 中国 | `qwen3.8-flash` | `qwen3.8-flash`、`qwen3.8-max`、`qwen3.7-plus` | [官方目录/接入文档](https://help.aliyun.com/zh/model-studio/models) |
| DeepSeek | 中国 | `deepseek-flash` | `deepseek-flash`、`deepseek-v4-pro` | [官方目录/接入文档](https://api-docs.deepseek.com/quick_start/pricing/) |
| 智谱 GLM | 中国 | `glm-5.3-flash` | `glm-5.3-flash`、`glm-5.3-flashx`、`glm-5.3` | [官方目录/接入文档](https://docs.bigmodel.cn/cn/guide/models/vlm/glm-5.3-flash) |
| Kimi（Moonshot） | 中国 | `kimi-k2.6` | `kimi-k3.1-pro`、`kimi-k3`、`kimi-k2.6` | [官方目录/接入文档](https://platform.kimi.com/docs/api/models-overview) |
| MiniMax（中国站） | 中国 | `MiniMax-M3.1-Flash` | `MiniMax-M3.1-Flash`、`MiniMax-M3` | [官方目录/接入文档](https://platform.minimax.cn/docs/api-reference/text-openai-api) |
| 阶跃星辰（StepFun） | 中国 | `step-3.7-flash` | `step-3.7-flash`、`step-3.5-flash` | [官方目录/接入文档](https://www.stepfun.com/step-3.7-flash) |
| 火山方舟（豆包） | 中国 | `doubao-seed-2-1-lite-260915` | `doubao-seed-2-1-lite-260915`、`doubao-seed-2-1-pro-260915`、`doubao-seed-evolving` | [官方目录/接入文档](https://ark.volcengine.com/region:cn-beijing/docs/model-list) |
| 腾讯 TokenHub（混元） | 中国 | `hy4` | `hy4`、`hy3`、`hunyuan-role-latest`、`deepseek/deepseek-flash` | [官方目录/接入文档](https://cloud.tencent.com/document/product/1823/130079) |
| 硅基流动（SiliconFlow） | 中国 | `deepseek-ai/DeepSeek-V4-Flash` | `deepseek-ai/DeepSeek-V4-Flash`、`deepseek-ai/DeepSeek-V4-Pro`、`moonshotai/Kimi-K2.7-Code` | [官方目录/接入文档](https://docs.siliconflow.cn/docs/api/chat-completions-post) |
| OpenAI | 国际 | `gpt-6-luna` | `gpt-6-luna`、`gpt-6.1-terra`、`gpt-6.1-sol`、`gpt-6-astra`、`gpt-4.1-mini`、`gpt-4.1` | [官方目录/接入文档](https://developers.openai.com/api/docs/models) |
| Anthropic（Claude） | 国际 | `claude-haiku-5-5` | `claude-haiku-5-5`、`claude-sonnet-5-5`、`claude-opus-5-5`、`claude-fable-5-2` | [官方目录/接入文档](https://platform.claude.com/docs/en/cli-sdks-libraries/libraries/openai-sdk) |
| Google Gemini | 国际 | `gemini-4-hydrogen` | `gemini-4-hydrogen`、`gemini-4-argon`、`gemini-4-carbon` | [官方目录/接入文档](https://aistudio.google.com/docs/models) |
| xAI（Grok） | 国际 | `grok-4.7` | `grok-4.7` | [官方目录/接入文档](https://docs.x.ai/developers/models) |
| Mistral AI | 国际 | `mistral-small-2603` | `mistral-small-2603`、`mistral-large-4`、`mistral-large-latest` | [官方目录/接入文档](https://docs.mistral.ai/models) |
| Groq | 国际 | `openai/gpt-oss-120b` | `openai/gpt-oss-120b`、`openai/gpt-oss-20b` | [官方目录/接入文档](https://console.groq.com/docs/models) |
| OpenRouter | 国际 | `google/gemini-4-hydrogen` | `google/gemini-4-hydrogen`、`openai/gpt-6-luna`、`anthropic/claude-sonnet-5.5`、`qwen/qwen3.8-flash` | [官方目录/接入文档](https://openrouter.ai/models) |
| Together AI | 国际 | `Qwen/Qwen3.8-Flash` | `Qwen/Qwen3.8-Flash`、`deepseek-ai/DeepSeek-V4.1-Flash-1017`、`zai-org/GLM-5.3-Flash`、`MiniMaxAI/MiniMax-M3.1-Flash` | [官方目录/接入文档](https://docs.together.ai/docs/serverless/models) |

## 接入与兼容

- Google Gemini 模板使用原生 generateContent 协议，其余模板使用各服务商公开的 OpenAI 兼容 Chat Completions 接口。模板会同时填入协议、地址与模型。
- Claude 使用 [Anthropic 官方 OpenAI SDK 兼容层](https://platform.claude.com/docs/en/cli-sdks-libraries/libraries/openai-sdk)，不冒充完整的原生 Messages 支持。该兼容层拒绝 json_object 格式，请求保留原有 JSON 提示词并继续执行结果解析、校验与失败处理。
- 当前 GPT 推理模型使用 max_completion_tokens，并省略不支持的 Temperature；依据 [OpenAI 请求参数](https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create) 与 [GPT-6 兼容规则](https://developers.openai.com/api/docs/guides/latest-model)。
- 当前 Kimi 和 MiniMax 模型使用官方要求的输出长度参数，固定采样参数由服务商处理，不改写用户保存的 Temperature；MiniMax 的思考内容独立返回，避免混入正文。见 [Kimi 参数参考](https://platform.kimi.com/docs/api/models-overview) 与 [MiniMax OpenAI 兼容接口](https://platform.minimax.cn/docs/api-reference/text-openai-api)。
- 参数按接收请求的平台处理。OpenRouter、Together、SiliconFlow、Groq 保留平台定义的输出预算与 JSON 约束，不根据模型厂商前缀套用直连原厂的参数；例如 [Together](https://docs.together.ai/reference/chat-completions) 与[硅基流动](https://docs.siliconflow.cn/docs/api/chat-completions-post) 均定义 `max_tokens`。Claude 的 `json_object` 规避只针对 Anthropic 官方兼容层。
- 国际站、国内站、Coding Plan 与按量计费通常有各自的地址和密钥；模板备注会标明接入站点。切换新连接的服务商模板会清空草稿中的密钥和请求头，避免跨站沿用。

OpenRouter 的起始 ID 同时按其[公开模型接口](https://openrouter.ai/api/v1/models)核对；Together 的地址按[官方 Quickstart](https://docs.together.ai/docs/quickstart)核对。火山方舟同时支持控制台提供的推理接入点 ID，可手动添加。

## 已有连接如何更新

在“管理服务商”中选择已有的内置服务商连接，点击“补充最新内置模型”，再“保存连接”。新目录按准确 ID 合并，原有模型、显示名称、默认模型、密钥与预设引用保留。若要升级正在使用的模型，请再从模型选择器切换并保存当前 AI 配置。

模型目录的日期与官方文档入口显示在服务商模板下方。以后更新目录时，重新核对各家的官方模型 ID、接口协议、根地址和请求参数要求，并更新核查日期。
