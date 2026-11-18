// README 面板截图使用的合成数据。
// 只包含虚构的群名、昵称与用量；不读取本机数据库、凭据或生产数据。
// 接口形状与当前面板保持一致，API 变动时这里要同步更新。

const now = new Date()
const minutesAgo = (minutes) => new Date(now.getTime() - minutes * 60_000).toISOString()
const daysAgo = (days) => new Date(now.getTime() - days * 86_400_000).toISOString().slice(0, 10)

const groups = [
  {
    groupOpenId: 'group-film',
    name: '周末放映室',
    addedAt: '2026-10-02T08:15:00Z',
    config: {
      isEnabled: true, triggerMode: 2, aiProactiveEnabled: true, botMentionOpenId: 'MUTTER_BOT',
      activeCharacter: 'mutter', customTemperature: null, customSystemPrompt: null, memoryInjectionEnabled: true
    }
  },
  {
    groupOpenId: 'group-dev',
    name: '独立开发者夜谈',
    addedAt: '2026-09-18T12:40:00Z',
    config: {
      isEnabled: true, triggerMode: 2, aiProactiveEnabled: true, botMentionOpenId: 'MUTTER_BOT',
      activeCharacter: 'analyst', customTemperature: 0.6, customSystemPrompt: null, memoryInjectionEnabled: true
    }
  },
  {
    groupOpenId: 'group-photo',
    name: '摄影同好会',
    addedAt: '2026-08-27T03:10:00Z',
    config: {
      isEnabled: true, triggerMode: 0, aiProactiveEnabled: false, botMentionOpenId: null,
      activeCharacter: null, customTemperature: null, customSystemPrompt: null, memoryInjectionEnabled: false
    }
  },
  {
    groupOpenId: 'group-product',
    name: '产品经理茶话会',
    addedAt: '2026-07-11T09:05:00Z',
    config: {
      isEnabled: false, triggerMode: 2, aiProactiveEnabled: false, botMentionOpenId: null,
      activeCharacter: null, customTemperature: null, customSystemPrompt: null, memoryInjectionEnabled: false
    }
  }
]

const characters = [
  {
    name: 'mutter', displayName: 'Mutter', isCustom: false,
    description: '自然、克制的群聊伙伴，接得住梗也看得懂气氛',
    systemPrompt: ''
  },
  {
    name: 'analyst', displayName: '冷静分析师', isCustom: true,
    description: '先给结论，再补依据，避免情绪化表达',
    systemPrompt: '你是一名冷静、务实的技术分析师。回答先给结论，再列关键依据，避免空泛和情绪化表达。'
  },
  {
    name: 'nightowl', displayName: '深夜搭子', isCustom: true,
    description: '轻松、简短，适合深夜闲聊',
    systemPrompt: '你是群里的深夜闲聊搭子，语气轻松，回复简短，不主动说教。'
  }
]

const connections = [
  {
    id: 'conn-faro', name: 'Faro 网关', templateId: 'custom', protocol: 'openai',
    apiBaseUrl: 'https://api.faro.example/v1', apiKey: 'sk-••••••••', hasApiKey: true, headers: {},
    timeoutSeconds: 60, defaultModel: 'gpt-6.1-terra',
    models: [
      { id: 'gpt-6.1-terra', name: 'GPT-6.1 Terra' },
      { id: 'gpt-6.1', name: 'GPT-6.1' },
      { id: 'claude-sonnet-4.6', name: 'Claude Sonnet 4.6' }
    ],
    isCurrent: true, presetCount: 2, updatedAt: '2026-11-17T09:12:00Z', revision: 'rev-faro-7'
  },
  {
    id: 'conn-gemini', name: 'Gemini 官方', templateId: 'gemini', protocol: 'gemini',
    apiBaseUrl: 'https://generativelanguage.googleapis.com/v1beta', apiKey: 'AIza••••••••', hasApiKey: true, headers: {},
    timeoutSeconds: 60, defaultModel: 'gemini-3-pro',
    models: [
      { id: 'gemini-3-pro', name: 'Gemini 3 Pro' },
      { id: 'gemini-3-flash', name: 'Gemini 3 Flash' }
    ],
    isCurrent: false, presetCount: 1, updatedAt: '2026-10-30T06:20:00Z', revision: 'rev-gemini-3'
  },
  {
    id: 'conn-bailian', name: '百炼 · 备用', templateId: 'qwen', protocol: 'openai',
    apiBaseUrl: 'https://dashscope.aliyuncs.com/compatible-mode/v1', apiKey: 'sk-••••••••', hasApiKey: true, headers: {},
    timeoutSeconds: 60, defaultModel: 'qwen3.5-max',
    models: [
      { id: 'qwen3.5-max', name: 'Qwen3.5 Max' },
      { id: 'qwen3.5-plus', name: 'Qwen3.5 Plus' }
    ],
    isCurrent: false, presetCount: 0, updatedAt: '2026-10-12T10:05:00Z', revision: 'rev-bailian-1'
  }
]

const providerTemplates = [
  {
    id: 'custom', name: '自定义（OpenAI 兼容）', category: 'custom', protocol: 'openai',
    apiBaseUrl: '', defaultModel: '', models: [], webSearchSupported: false, webSearchMode: 'none',
    description: '任意兼容 Chat Completions 的端点或中转站。', documentationUrl: '', keyHelpUrl: '', keyHelpText: ''
  },
  {
    id: 'openai', name: 'OpenAI', category: 'global', protocol: 'openai',
    apiBaseUrl: 'https://api.openai.com/v1', defaultModel: 'gpt-6.1', models: ['gpt-6.1', 'gpt-6.1-mini'],
    webSearchSupported: true, webSearchMode: 'web_search_options',
    description: 'OpenAI 官方接口，支持 web_search_options。', documentationUrl: 'https://platform.openai.com/docs', keyHelpUrl: '', keyHelpText: ''
  },
  {
    id: 'gemini', name: 'Google Gemini', category: 'global', protocol: 'gemini',
    apiBaseUrl: 'https://generativelanguage.googleapis.com/v1beta', defaultModel: 'gemini-3-pro',
    models: ['gemini-3-pro', 'gemini-3-flash'], webSearchSupported: true, webSearchMode: 'gemini_native',
    description: 'Gemini 原生 generateContent，支持 google_search 工具。', documentationUrl: 'https://ai.google.dev/gemini-api/docs', keyHelpUrl: '', keyHelpText: ''
  },
  {
    id: 'qwen', name: '阿里云百炼', category: 'china', protocol: 'openai',
    apiBaseUrl: 'https://dashscope.aliyuncs.com/compatible-mode/v1', defaultModel: 'qwen3.5-max',
    models: ['qwen3.5-max', 'qwen3.5-plus'], webSearchSupported: true, webSearchMode: 'enable_search',
    description: '通义千问兼容接口，enable_search 开启联网。', documentationUrl: '', keyHelpUrl: '', keyHelpText: ''
  }
]

const aiConfig = {
  providerConnectionId: 'conn-faro',
  providerConnectionVersion: 'rev-faro-7',
  apiBaseUrl: 'https://api.faro.example/v1',
  apiKey: '',
  modelName: 'gpt-6.1-terra',
  personality: 'mutter',
  systemPrompt: '',
  temperature: 0.8,
  maxTokens: 4096,
  maxContextMessages: 20,
  contextWindowTokens: 200_000,
  maxInputTokens: 180_000,
  ceilingAttachedImages: 10,
  maxAttachedImages: 3,
  groupMemoryInjectionEnabled: true,
  webSearchEnabled: true,
  webSearchEndpoint: 'openai',
  webSearchShape: 'web_search_options',
  webSearchImageMode: 'auto',
  webSearchVerifiedAt: minutesAgo(52),
  webSearchImageCapable: true,
  webSearchImageVerifiedAt: minutesAgo(50),
  externalSearch: {
    enabled: false, provider: 'searxng', baseUrl: '', apiKey: '', maxResults: 5, timeoutSeconds: 8, fallbackToBuiltin: true
  },
  externalSearchStatus: { verifiedAt: null },
  defaultSystemPrompt: '你是 Mutter，一个自然、克制的群聊伙伴。先理解群里的气氛，再决定要不要说话。',
  availableCharacters: characters,
  availableProviders: providerTemplates,
  connections
}

const presets = [
  {
    id: 'preset-daily', name: '群聊日常', description: 'Faro · GPT-6.1 Terra，适合大多数闲聊群',
    providerConnectionId: 'conn-faro', modelName: 'gpt-6.1-terra', personality: 'mutter', personalityMissing: false,
    includeApiKey: false, isValid: true, externalSearchEnabled: false, updatedAt: '2026-11-16T07:20:00Z'
  },
  {
    id: 'preset-deep', name: '深度讨论', description: 'Gemini 3 Pro，长上下文与原生联网',
    providerConnectionId: 'conn-gemini', modelName: 'gemini-3-pro', personality: 'analyst', personalityMissing: false,
    includeApiKey: false, isValid: true, externalSearchEnabled: false, updatedAt: '2026-11-10T11:45:00Z'
  }
]

const runtime = {
  queue: {
    running: 2, waiting: 3, parallelism: 3, capacity: 256, rejected: 4, expired: 1, coalesced: 17,
    tasks: [
      {
        id: '11111111-1111-4111-8111-111111111111', conversationId: 'g:group-film', description: '小林',
        background: false, state: 'running', enqueuedAt: minutesAgo(1)
      },
      {
        id: '22222222-2222-4222-8222-222222222222', conversationId: 'g:group-dev', description: '阿远',
        background: false, state: 'running', enqueuedAt: minutesAgo(2)
      },
      {
        id: '33333333-3333-4333-8333-333333333333', conversationId: 'g:group-film', description: '择机发言',
        background: true, state: 'queued', enqueuedAt: minutesAgo(3)
      },
      {
        id: '44444444-4444-4444-8444-444444444444', conversationId: 'g:group-dev', description: '择机发言',
        background: true, state: 'completed', outcome: 'sent_active', enqueuedAt: minutesAgo(8)
      },
      {
        id: '55555555-5555-4555-8555-555555555555', conversationId: 'g:group-photo', description: '阿青',
        background: false, state: 'completed', outcome: 'sent_passive', enqueuedAt: minutesAgo(12)
      }
    ]
  },
  calls: [
    { operationId: '11111111-1111-4111-8111-111111111111', model: 'gpt-6.1-terra', purpose: 'reply', elapsedMs: 2140, state: 'success', startedAt: minutesAgo(1) },
    { operationId: '22222222-2222-4222-8222-222222222222', model: 'gpt-6.1-terra', purpose: 'reply', elapsedMs: 1830, state: 'success', startedAt: minutesAgo(2) },
    { operationId: '33333333-3333-4333-8333-333333333333', model: 'gpt-6.1-terra', purpose: 'proactive', elapsedMs: 3420, state: 'success', startedAt: minutesAgo(3) },
    { operationId: '66666666-6666-4666-8666-666666666666', model: 'gemini-3-pro', purpose: 'proactive', elapsedMs: 4680, state: 'success', startedAt: minutesAgo(6) },
    { operationId: '77777777-7777-4777-8777-777777777777', model: 'gpt-6.1-terra', purpose: 'probe', elapsedMs: 920, state: 'success', startedAt: minutesAgo(9) },
    { operationId: '88888888-8888-4888-8888-888888888888', model: 'gpt-6.1-terra', purpose: 'reply', elapsedMs: 5510, state: 'failed', startedAt: minutesAgo(14) }
  ],
  upstream: { degraded: false, reason: '', windowSuccess: 196, windowFailure: 2, p50Ms: 1280, p95Ms: 3120 },
  groupNames: Object.fromEntries(groups.map(group => [group.groupOpenId, group.name])),
  sampleCount: 198,
  failureCount: 2,
  averageMs: 1640,
  p95Ms: 3120,
  imageCache: { bytes: 26_843_545, byteLimit: 67_108_864, entries: 36, downloads: 0 }
}

const chatItems = [
  {
    kind: 'decision', id: 912, createdAt: minutesAgo(3), chatType: 0, groupId: 'group-film', groupName: '周末放映室',
    decisionId: 912, outcome: 'skipped_ai_declined', topic: '周末观影',
    reason: '大家已经定下片目，当前没有需要补充的信息。'
  },
  {
    kind: 'message', id: 8801, createdAt: minutesAgo(4), chatType: 0, groupId: 'group-film', groupName: '周末放映室',
    userId: 'bot', userName: 'Bot', plainText: '那我把《周末大饭店》记下了。七点半开场，吃完正好赶上，散场还能聊两句。',
    hasImage: false, hasAtBot: false, isBotMessage: true, isProactive: true, decisionId: 911
  },
  {
    kind: 'message', id: 8800, createdAt: minutesAgo(5), chatType: 0, groupId: 'group-film', groupName: '周末放映室',
    userId: 'u-qing', userName: '阿青', plainText: '要不就这部吧，评分不低，也不是那种看完还要想半天的片子。',
    hasImage: false, hasAtBot: false, isBotMessage: false, isProactive: false
  },
  {
    kind: 'message', id: 8799, createdAt: minutesAgo(6), chatType: 0, groupId: 'group-film', groupName: '周末放映室',
    userId: 'u-lin', userName: '小林', plainText: '有没有轻松一点的喜剧？今天脑子已经转不动了。',
    hasImage: false, hasAtBot: false, isBotMessage: false, isProactive: false
  },
  {
    kind: 'message', id: 8798, createdAt: minutesAgo(7), chatType: 0, groupId: 'group-film', groupName: '周末放映室',
    userId: 'u-yuan', userName: '阿远', plainText: '我投《周末大饭店》一票，正好附近那家影院还有七点半的场。',
    hasImage: false, hasAtBot: false, isBotMessage: false, isProactive: false
  },
  {
    kind: 'message', id: 8797, createdAt: minutesAgo(9), chatType: 0, groupId: 'group-film', groupName: '周末放映室',
    userId: 'u-lin', userName: '小林', plainText: '今晚有人一起看电影吗？想找部轻松点的。',
    hasImage: false, hasAtBot: false, isBotMessage: false, isProactive: false
  },
  {
    kind: 'message', id: 8790, createdAt: minutesAgo(38), chatType: 0, groupId: 'group-film', groupName: '周末放映室',
    userId: 'bot', userName: 'Bot', plainText: '如果只是想放松，可以试试《心灵奇旅》。节奏温和，画面舒服，结尾也不会硬塞道理。',
    hasImage: false, hasAtBot: false, isBotMessage: true, isProactive: false, decisionId: null
  },
  {
    kind: 'message', id: 8789, createdAt: minutesAgo(39), chatType: 0, groupId: 'group-film', groupName: '周末放映室',
    userId: 'u-lin', userName: '小林', plainText: 'Mutter，最近有什么适合放松的电影？',
    hasImage: false, hasAtBot: true, isBotMessage: false, isProactive: false
  },
  {
    kind: 'message', id: 8788, createdAt: minutesAgo(40), chatType: 0, groupId: 'group-film', groupName: '周末放映室',
    userId: 'u-qing', userName: '阿青', plainText: '刚忙完，想找点轻松的，不想再烧脑了。',
    hasImage: false, hasAtBot: false, isBotMessage: false, isProactive: false
  },
  {
    kind: 'message', id: 8787, createdAt: minutesAgo(42), chatType: 0, groupId: 'group-film', groupName: '周末放映室',
    userId: 'u-yuan', userName: '阿远', plainText: '今天终于把版本发出去了，晚上可以放空一下。',
    hasImage: false, hasAtBot: false, isBotMessage: false, isProactive: false
  }
]

const usageDays = Array.from({ length: 7 }, (_, index) => {
  const date = daysAgo(6 - index)
  const totalTokens = [3_120_000, 4_480_000, 3_860_000, 5_240_000, 6_180_000, 4_920_000, 5_640_000][index]
  return { date, totalTokens, callCount: [196, 268, 231, 302, 356, 284, 318][index] }
})

const proactiveLogs = [
  ['sent_active', '周末放映室', '今晚的观影安排', '群友在讨论片目，顺着话头补了一个可执行的建议。', '那我把《周末大饭店》记下了。七点半开场，吃完正好赶上，散场还能聊两句。', 'gpt-6.1-terra', 1240, 96, '周末 喜剧 电影 推荐', true],
  ['sent_passive', '独立开发者夜谈', '版本发布后的收尾', '阿远刚发完版本，接住了他的疲惫感。', '先把版本放一放，今晚别急着复盘。明早再看告警，通常不会跑。', 'gpt-6.1-terra', 980, 72, null, false],
  ['skipped_cooldown', '周末放映室', '外卖选择', '距离上次发言不足冷却时间。', null, 'gpt-6.1-terra', 760, 0, null, false],
  ['skipped_ai_declined', '摄影同好会', '镜头选择', '成员正在自行讨论，当前没有需要补充的信息。', null, 'gpt-6.1-terra', 1120, 18, null, false],
  ['sent_active', '独立开发者夜谈', 'CI 偶发失败', '讨论卡在猜测阶段，补充了一个排查方向。', '先看失败任务是不是都落在同一个 runner 上，偶发失败经常是缓存目录串了。', 'gemini-3-pro', 1460, 128, 'GitHub Actions 偶发失败 排查', true],
  ['skipped_quiet', '周末放映室', '深夜闲聊', '处于静默时段。', null, 'gpt-6.1-terra', 0, 0, null, false],
  ['skipped_ai_declined', '独立开发者夜谈', '周末安排', '话题已经收束，没有合适的接话点。', null, 'gpt-6.1-terra', 890, 12, null, false],
  ['sent_passive', '周末放映室', '影院排片', '有人 @ 了 Mutter，按群聊语境给了简短回答。', '附近那家影院今晚还有 19:30 的场，散场大约 21:20。', 'gpt-6.1-terra', 1040, 84, '附近影院 排片', true]
].map(([outcome, groupName, topic, reason, line, model, promptTokens, completionTokens, searchQuery, searchUsed], index) => ({
  id: 900 - index,
  groupId: groups.find(group => group.name === groupName)?.groupOpenId || 'group-film',
  groupName,
  evaluatedAt: minutesAgo(6 + index * 9),
  messageCount: 8 + (index % 5),
  outcome,
  shouldSpeak: outcome.startsWith('sent_'),
  imageCount: index % 4 === 0 ? 1 : 0,
  memoryCount: index % 3 === 0 ? 3 : 0,
  memoryPromptChars: index % 3 === 0 ? 486 : 0,
  topic,
  line,
  reason,
  detail: null,
  model,
  promptTokens,
  completionTokens,
  searchQuery,
  searchUsed,
  speechPromptTokens: outcome.startsWith('sent_') ? 620 : 0,
  speechCompletionTokens: outcome.startsWith('sent_') ? 48 : 0
}))

const memoryItems = [
  {
    id: 31, content: '群里常约周五晚看电影，偏好轻松喜剧，通常选择 19:30 左右的场次。',
    tags: ['观影', '周五', '偏好'], lastSeenAt: minutesAgo(180), expiresAt: daysAgo(-120)
  },
  {
    id: 32, content: '阿远负责发版，版本发布后通常需要一点缓冲时间，不适合立刻追问细节。',
    tags: ['成员', '发版', '沟通习惯'], lastSeenAt: minutesAgo(320), expiresAt: daysAgo(-90)
  },
  {
    id: 33, content: '讨论技术方案时习惯先给结论再展开；对泛泛而谈的答复接受度较低。',
    tags: ['讨论风格', '技术'], lastSeenAt: minutesAgo(760), expiresAt: daysAgo(-60)
  }
]

const memoryConfig = {
  enabled: true,
  countEnabled: true,
  count: 30,
  intervalEnabled: true,
  intervalMinutes: 180,
  scheduleEnabled: true,
  schedules: [{ frequency: 'daily', time: '09:30', weekday: 1 }],
  providerConnectionId: 'conn-faro',
  modelName: 'gpt-6.1-terra',
  maxOutputTokens: 4096
}

const memoryAuto = {
  config: memoryConfig,
  revision: 12,
  limits: {
    capacity: 128, perGroupDailyCalls: 12, maxCount: 1000, maxIntervalMinutes: 10080,
    maxSchedules: 4, minOutputTokens: 256, maxOutputTokens: 8192
  },
  status: {
    pending: 18, pendingCapped: false, lastOutcome: 'success', lastCompletedAt: minutesAgo(46),
    lastProcessedRowId: 8797, nextAttemptAt: minutesAgo(-32), callsToday: 3, groupEnabled: true
  },
  items: memoryItems,
  runs: [
    { at: minutesAgo(46), reasons: ['新增记录'], outcome: 'success', processed: 30, added: 1, updated: 2, removed: 0, model: 'gpt-6.1-terra' },
    { at: minutesAgo(226), reasons: ['时间间隔'], outcome: 'success', processed: 18, added: 0, updated: 1, removed: 0, model: 'gpt-6.1-terra' }
  ]
}

const sessions = [
  {
    tokenPrefix: 'mt_7f3c', remoteIp: '127.0.0.1', createdAt: minutesAgo(86), lastSeenAt: minutesAgo(1), isCurrent: true
  },
  {
    tokenPrefix: 'mt_2a91', remoteIp: '192.168.1.24', createdAt: minutesAgo(1520), lastSeenAt: minutesAgo(420), isCurrent: false
  }
]

const update = {
  currentVersion: '261118', latestVersion: '261118', hasUpdate: false, canSelfUpdate: true,
  releaseUrl: 'https://github.com/TangKuku9/Mutter-Bot/releases/tag/v261118', checkedAt: minutesAgo(4),
  error: null, selfUpdateReason: null, assetName: null, assetSize: 0, assetKind: 'full', recommendedKind: 'app',
  appAssetName: 'mutter-bot-v261118-win-x64-app.zip', appAssetSize: 5_242_880,
  fullAssetName: 'mutter-bot-v261118-win-x64-self-contained.zip', fullAssetSize: 55_246_848,
  runtimeChanged: false, source: 'github', signatureStatus: 'valid', manifestSigned: true,
  staged: false, stagedVersion: null, stagedKind: ''
}

const status = {
  isConnected: true, version: '261118', uptime: '3 天 08:14:23', botQQ: '1000000000', botNickname: 'Mutter', webPanelPort: 5188
}

const probeReport = {
  reachable: true, authFailed: false, error: null, basePromptTokens: 1280,
  results: [
    {
      shape: 'web_search_options', description: 'OpenAI 兼容：顶层 web_search_options（搜索模型）',
      status: 'verified', groundingField: 'choices[0].message.annotations', note: '返回了网页搜索引用',
      evidence: 'web_results', imageStatus: 'verified', imageNote: '颜色顺序正确', imageEvidence: 'image_challenge'
    },
    {
      shape: 'web_search_tool', description: 'OpenAI 兼容：tools 声明 web_search（智谱 GLM）',
      status: 'verified', groundingField: 'choices[0].message.annotations', note: '返回了网页搜索引用',
      evidence: 'web_results', imageStatus: 'verified', imageNote: '颜色顺序正确', imageEvidence: 'image_challenge'
    },
    {
      shape: 'enable_search', description: 'OpenAI 兼容：顶层 enable_search:true（通义/百炼）',
      status: 'no_evidence', note: '请求成功，但没有返回可核验的网页结果', evidence: 'none',
      imageStatus: 'not_tested'
    },
    {
      shape: 'google_search_tool', description: 'OpenAI 兼容：tools 声明 function google_search',
      status: 'not_tested', note: '已找到可用的文字联网策略', imageStatus: 'not_tested'
    },
    {
      shape: 'builtin_web_search', description: 'OpenAI 兼容：tools 内联 google_search（Gemini 兼容层直传）',
      status: 'not_tested', note: '已找到可用的文字联网策略', imageStatus: 'not_tested'
    },
    {
      shape: 'gemini_native', description: 'Gemini 原生 :generateContent + tools:[{google_search}]',
      status: 'not_tested', note: '已找到可用的文字联网策略', imageStatus: 'not_tested'
    }
  ]
}

const probeStream = [
  {
    type: 'start',
    cases: probeReport.results.map(({ shape, description }) => ({ shape, description }))
  },
  ...probeReport.results.map(result => ({ type: 'case', result })),
  { type: 'done', report: probeReport }
]

export const fixtures = {
  '/status': status,
  '/auth/status': { authenticated: true, username: 'admin' },
  '/auth/sessions': sessions,
  '/config/qqbot': { appId: '1029••••••', apiBaseUrl: 'https://api.sgroup.qq.com' },
  '/config/network': { allowSystemProxy: false },
  '/config/ai': aiConfig,
  '/config/ai/connections': connections,
  '/config/ai/presets': presets,
  '/config/characters': characters,
  '/groups': groups,
  '/groups/quotas': [
    { key: 'group-film', scope: 'group', month: '2026-11', count: 42, rejected: 0, lastSentAt: minutesAgo(4) },
    { key: 'group-dev', scope: 'group', month: '2026-11', count: 28, rejected: 1, lastSentAt: minutesAgo(11) },
    { key: 'account', scope: 'account', month: '2026-11', count: 70, rejected: 1, lastSentAt: minutesAgo(4) }
  ],
  '/groups/private-chats': [
    { userId: 'u-yuan', nickname: '阿远', isEnabled: true, isAdmin: true },
    { userId: 'u-lin', nickname: '小林', isEnabled: true, isAdmin: false }
  ],
  '/groups/group-film/memory-auto': memoryAuto,
  '/runtime': runtime,
  '/chat/stats': {
    totalMessages: 328_416, todayMessages: 1284, aiCallCount: 21_846,
    totalTokensUsed: 48_320_115, unknownUsageCount: 12, legacyReplyCount: 0
  },
  '/chat/timeline/latest': {
    items: chatItems, totalCount: null, page: 1, pageSize: 50, hasMore: true, snapshot: '8801:912'
  },
  '/chat/timeline/count': { totalCount: 2846, snapshot: '8801:912' },
  '/chat/timeline': {
    items: chatItems, totalCount: 2846, page: 1, pageSize: 50, hasMore: true, snapshot: '8801:912'
  },
  '/chat/usage': {
    summary: { callCount: 1842, failedCount: 7, unknownUsageCount: 3, legacyCount: 0 },
    daily: usageDays,
    groups: [
      { groupId: 'group-film', groupName: '周末放映室', callCount: 642, totalTokens: 8_420_000 },
      { groupId: 'group-dev', groupName: '独立开发者夜谈', callCount: 518, totalTokens: 7_960_000 },
      { groupId: 'group-photo', groupName: '摄影同好会', callCount: 236, totalTokens: 2_840_000 },
      { groupId: 'group-product', groupName: '产品经理茶话会', callCount: 182, totalTokens: 1_920_000 }
    ],
    users: [
      { userName: '小林', userId: 'u-lin', callCount: 186, totalTokens: 2_840_000 },
      { userName: '阿远', userId: 'u-yuan', callCount: 142, totalTokens: 2_120_000 },
      { userName: '阿青', userId: 'u-qing', callCount: 118, totalTokens: 1_680_000 },
      { userName: '未记录昵称', userId: 'u-4821', callCount: 64, totalTokens: 920_000 }
    ]
  },
  '/proactive/config': {
    enabled: true, windowSeconds: 90, windowMaxMessages: 14, cooldownMinutes: 40,
    maxPerGroupPerDay: 4, maxGlobalPerDay: 15, quietStartHour: 23, quietEndHour: 8, liveliness: 1,
    maxAttachedImages: 3, historyMaxMessages: 20, evalMaxTokens: 1024, webSearchEnabled: true,
    allowActiveChannel: true, activePerGroupPerMinute: 3, activePerGroupPerDay: 20,
    activePerAccountPerMinute: 20, memoryInjectionEnabled: true, updatedAt: minutesAgo(320),
    ceilingGroupPerMinute: 5, ceilingGroupPerDay: 20, ceilingAccountPerMinute: 20, ceilingAttachedImages: 10
  },
  '/proactive/status': {
    gatewayEvents: { GROUP_MESSAGE_CREATE: 1482, GROUP_AT_MESSAGE_CREATE: 36 },
    fullModeArrived: 1482, atModeArrived: 36,
    deliveryHint: '全量群消息已到达 1482 条，围观链路正常',
    enabled: true, allowActiveChannel: true, memoryInjectionEnabled: true, webSearchEnabled: true,
    aiWebSearchEnabled: true, aiWebSearchConfigured: true, todayMemoryInjections: 18,
    participatingGroups: 3, todaySent: 14, todayEvaluations: 44, todayDecisions: 41,
    todayByOutcome: [
      { outcome: 'sent_active', count: 9 },
      { outcome: 'sent_passive', count: 5 },
      { outcome: 'skipped_ai_declined', count: 21 },
      { outcome: 'skipped_cooldown', count: 7 },
      { outcome: 'skipped_quiet', count: 2 }
    ],
    quotas: [
      { groupId: 'group-film', name: '周末放映室', usedToday: 9, quota: 20, sentLastMinute: 1, minuteQuota: 3, rejected: 0, lastSentAt: minutesAgo(4) },
      { groupId: 'group-dev', name: '独立开发者夜谈', usedToday: 4, quota: 20, sentLastMinute: 0, minuteQuota: 3, rejected: 1, lastSentAt: minutesAgo(11) },
      { groupId: 'group-photo', name: '摄影同好会', usedToday: 1, quota: 20, sentLastMinute: 0, minuteQuota: 3, rejected: 0, lastSentAt: minutesAgo(280) }
    ],
    pendingWindows: [
      { groupId: 'group-film', name: '周末放映室', pending: 7 },
      { groupId: 'group-dev', name: '独立开发者夜谈', pending: 3 }
    ]
  },
  '/proactive/logs': { items: proactiveLogs, totalCount: 86, page: 1, pageSize: 20 },
  '/update': update,
  '/update/progress': { phase: 'idle', active: false },
  '/update/settings': { source: 'github', mode: 'auto' }
}

export const probeStreamBody = probeStream.map(event => JSON.stringify(event)).join('\n') + '\n'

export { groups, chatItems, memoryItems }
