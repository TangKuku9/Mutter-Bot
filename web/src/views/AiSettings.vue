<template>
  <div class="ai-settings" v-loading="loading">
    <el-alert
      v-if="loadError"
      type="warning"
      :closable="false"
      show-icon
      class="ai-load-error"
      title="暂时无法读取配置，当前内容已保留。"
    >
      <el-button link type="primary" @click="reloadConfig">重新读取</el-button>
    </el-alert>
    <el-card shadow="never" class="ai-preset-card">
      <div class="ai-heading">
        <div>
          <div class="ai-title">
            <h2>AI 配置</h2>
            <el-tag v-if="ready" size="small" :type="isDirty ? 'warning' : 'info'">
              {{ isDirty ? '有未保存修改' : '已读取配置' }}
            </el-tag>
          </div>
          <p>连接模型，调整回复风格与信息来源。</p>
        </div>
        <div class="ai-actions">
          <el-button :disabled="!ready || formBusy" @click="openHelp('configPreset')">参数说明</el-button>
          <el-button :icon="Collection" :disabled="!ready || formBusy || presetsLoading" @click="openManagePresets">
            预设库
          </el-button>
          <el-button :icon="FolderAdd" :disabled="!ready || formBusy || !!formError" @click="openSavePreset()">
            存为预设
          </el-button>
        </div>
      </div>
      <div class="ai-preset-toolbar">
        <label for="ai-config-preset">配置预设</label>
        <el-select
          id="ai-config-preset"
          v-model="selectedPresetId"
          clearable
          placeholder="选择已保存的预设"
          :loading="presetsLoading"
          :disabled="formBusy || presetsLoading"
          aria-label="配置预设"
        >
          <el-option v-for="preset in presets" :key="preset.id" :label="preset.name" :value="preset.id">
            <div class="ai-option">
              <span>{{ preset.name }}</span>
              <small>{{ preset.modelName || '未填模型' }}</small>
            </div>
          </el-option>
        </el-select>
        <el-button :disabled="!selectedPresetId || formBusy" :loading="applyingPreset" @click="applySelectedPreset()">
          应用并保存
          <el-icon class="ai-action-icon"><ArrowRight /></el-icon>
        </el-button>
      </div>
      <div v-if="selectedPreset" class="ai-preset-note">
        <el-tag size="small" type="info">{{ selectedPreset.hasApiKey ? '包含密钥' : '沿用当前密钥' }}</el-tag>
        <span>{{ selectedPreset.description || '应用后立即保存当前 AI 配置。' }}</span>
        <span v-if="selectedPreset.personalityMissing" class="ai-warning">人设「{{ selectedPreset.personality }}」已不存在</span>
      </div>
      <p class="ai-preset-footnote">AI 预设应用后立即生效；保存为预设只记录当前表单，不改变正在使用的配置。</p>
      <el-alert v-if="presetsError" type="warning" :closable="false" show-icon title="预设暂时无法读取">
        <el-button link type="primary" @click="loadPresets">重新读取预设</el-button>
      </el-alert>
    </el-card>

    <el-form :model="form" :disabled="formBusy" label-position="top" class="ai-settings-form">
      <div class="ai-primary-grid">
        <el-card shadow="never" class="ai-section" data-section="connection">
          <div class="ai-section-heading">
            <el-icon><Connection /></el-icon>
            <div>
              <h3>模型连接</h3>
              <p>设置服务商、接口和模型。</p>
            </div>
          </div>
          <div class="ai-fields">
            <AiSettingField
              label="服务商"
              control-id="ai-provider"
              help-key="presetId"
              :hint="presetHelp || '选择服务商会填入地址和默认模型，密钥保持当前值。'"
              @help="openHelp"
            >
              <el-select
                id="ai-provider"
                v-model="presetId"
                clearable
                placeholder="选择服务商，也可直接填写接口"
                @change="applyPreset"
              >
                <el-option
                  v-for="provider in form.availableProviders"
                  :key="provider.id"
                  :label="provider.name"
                  :value="provider.id"
                >
                  <div class="ai-option">
                    <span>{{ provider.name }}</span>
                    <small>{{ provider.defaultModel }}</small>
                  </div>
                </el-option>
              </el-select>
            </AiSettingField>
            <AiSettingField
              label="API 地址"
              control-id="ai-api-url"
              help-key="apiBaseUrl"
              hint="使用服务商提供的接口根地址。"
              @help="openHelp"
            >
              <el-input id="ai-api-url" v-model="form.apiBaseUrl" placeholder="https://api.example.com/v1" />
            </AiSettingField>
            <AiSettingField
              label="API Key"
              control-id="ai-api-key"
              help-key="apiKey"
              hint="留空或保持打码值表示不修改；填写新值会替换已保存的密钥。"
              @help="openHelp"
            >
              <el-input
                id="ai-api-key"
                v-model="form.apiKey"
                type="password"
                show-password
                autocomplete="new-password"
                placeholder="留空或保持打码值表示不修改"
              />
            </AiSettingField>
            <AiSettingField
              label="模型名称"
              control-id="ai-model"
              help-key="modelName"
              hint="使用服务商提供的准确模型标识。"
              @help="openHelp"
            >
              <el-input id="ai-model" v-model="form.modelName" placeholder="模型名" />
            </AiSettingField>
          </div>
        </el-card>
        <el-card shadow="never" class="ai-section" data-section="personality">
          <div class="ai-section-heading">
            <el-icon><User /></el-icon>
            <div>
              <h3>回复风格</h3>
              <p>决定怎么说，也决定说话的分寸。</p>
            </div>
            <el-button link type="primary" :disabled="formBusy" @click="manageDialog = true">管理人设</el-button>
          </div>
          <div class="ai-fields">
            <AiSettingField
              label="全局人设"
              control-id="ai-personality"
              help-key="personality"
              hint="群管理中指定的人设优先于这里的全局人设。"
              @help="openHelp"
            >
              <el-select id="ai-personality" v-model="form.personality" clearable placeholder="选择角色人设">
                <el-option
                  v-for="character in form.availableCharacters"
                  :key="character.name"
                  :label="character.displayName"
                  :value="character.name"
                >
                  <div class="ai-option">
                    <span>{{ character.displayName }}</span>
                    <small>{{ character.isCustom ? '自定义' : '内置' }}</small>
                  </div>
                </el-option>
              </el-select>
            </AiSettingField>
            <AiSettingField
              label="回复随机度"
              control-id="ai-temperature"
              help-key="temperature"
              hint="Temperature 越低越稳定，越高越灵活。"
              @help="openHelp"
            >
              <div class="ai-temperature">
                <el-slider v-model="form.temperature" :min="0" :max="2" :step="0.1" aria-label="回复随机度滑块" />
                <el-input-number
                  :key="String(formBusy)"
                  id="ai-temperature"
                  v-model="form.temperature"
                  :min="0"
                  :max="2"
                  :step="0.1"
                  :precision="1"
                  :controls="false"
                />
              </div>
            </AiSettingField>
            <AiSettingField label="系统提示词" control-id="ai-system-prompt" help-key="systemPrompt" @help="openHelp">
              <el-input
                id="ai-system-prompt"
                v-model="form.systemPrompt"
                type="textarea"
                :autosize="{ minRows: 4, maxRows: 10 }"
                placeholder="留空则使用内置默认提示词"
              />
              <template #hint>
                <span>{{ form.systemPrompt ? '自定义提示词会替换全局默认提示词。' : '当前使用内置默认提示词。' }}</span>
                <el-button link type="primary" :disabled="formBusy" @click="defaultPromptDialog = true">
                  查看内置默认提示词
                </el-button>
              </template>
            </AiSettingField>
          </div>
        </el-card>
      </div>

      <el-card shadow="never" class="ai-section ai-context-card" data-section="context">
        <div class="ai-section-heading">
          <el-icon><ChatDotRound /></el-icon>
          <div>
            <h3>上下文与记忆</h3>
            <p>控制普通 @ 回复与私聊参考多少内容。</p>
          </div>
        </div>
        <div class="ai-context-grid ai-fields">
          <AiSettingField
            label="回复输出上限"
            control-id="ai-output-tokens"
            help-key="maxTokens"
            hint="每次回复的输出预算；择机判定使用自己的设置。"
            @help="openHelp"
          >
            <el-input-number
              :key="String(formBusy)"
              id="ai-output-tokens"
              v-model="form.maxTokens"
              :min="64"
              :max="32768"
              :step="256"
              :precision="0"
              :controls="false"
            />
            <span class="ai-unit">Token</span>
          </AiSettingField>
          <AiSettingField
            label="历史消息条数"
            control-id="ai-history"
            help-key="maxContextMessages"
            hint="保留最近的历史消息帮助衔接对话。"
            @help="openHelp"
          >
            <el-input-number
              :key="String(formBusy)"
              id="ai-history"
              v-model="form.maxContextMessages"
              :min="2"
              :max="100"
              :precision="0"
              :controls="false"
            />
            <span class="ai-unit">条</span>
          </AiSettingField>
          <AiSettingField
            label="附图张数"
            control-id="ai-attached-images"
            help-key="maxAttachedImages"
            hint="0 表示不附图，实际附图需符合模型支持的格式。"
            @help="openHelp"
          >
            <el-input-number
              :key="String(formBusy)"
              id="ai-attached-images"
              v-model="form.maxAttachedImages"
              :min="0"
              :max="form.ceilingAttachedImages || 10"
              :precision="0"
              :controls="false"
            />
            <span class="ai-unit">张</span>
          </AiSettingField>
          <AiSettingField
            label="群记忆用于回复"
            control-id="ai-group-memory"
            help-key="groupMemoryInjectionEnabled"
            hint="参考群管理中维护的记忆；私聊不使用群记忆。"
            @help="openHelp"
          >
            <el-switch id="ai-group-memory" v-model="form.groupMemoryInjectionEnabled" aria-label="群记忆用于回复" />
            <span class="ai-unit">{{ form.groupMemoryInjectionEnabled ? '已开启' : '已关闭' }}</span>
          </AiSettingField>
        </div>
        <el-collapse v-model="advancedOpen" class="ai-budget-collapse">
          <el-collapse-item name="capacity">
            <template #title>
              <span>容量与输入预算</span>
              <small>{{ formatTokens(budget.input) }} / {{ formatTokens(budget.context) }} Token</small>
            </template>
            <div class="ai-context-grid ai-fields">
              <AiSettingField
                label="模型上下文容量"
                control-id="ai-context-capacity"
                help-key="contextWindowTokens"
                hint="按服务商公布的模型容量填写。"
                @help="openHelp"
              >
                <el-input-number
                  :key="String(formBusy)"
                  id="ai-context-capacity"
                  v-model="form.contextWindowTokens"
                  :min="4096"
                  :max="1048576"
                  :step="4096"
                  :precision="0"
                  :controls="false"
                />
                <span class="ai-unit">Token</span>
              </AiSettingField>
              <AiSettingField
                label="输入预算"
                control-id="ai-input-tokens"
                help-key="maxInputTokens"
                hint="历史、当前消息、系统提示词和图片共用这段预算。"
                @help="openHelp"
              >
                <el-input-number
                  :key="String(formBusy)"
                  id="ai-input-tokens"
                  v-model="form.maxInputTokens"
                  :min="512"
                  :max="Math.max(512, budget.availableInput)"
                  :step="512"
                  :precision="0"
                  :controls="false"
                />
                <span class="ai-unit">Token</span>
              </AiSettingField>
            </div>
            <div class="ai-budget-summary">
              <div>
                <span>已分配容量</span>
                <span>
                  {{ formatTokens(budget.input + budget.output + budget.reserve) }} / {{ formatTokens(budget.context) }} Token
                </span>
              </div>
              <el-progress
                :percentage="budget.percent"
                :status="budget.error ? 'exception' : undefined"
                :stroke-width="6"
                :show-text="false"
                aria-label="已分配模型容量"
              />
              <p>
                输入 {{ formatTokens(budget.input) }} · 输出 {{ formatTokens(budget.output) }} · 安全预留
                {{ formatTokens(budget.reserve) }}
              </p>
            </div>
          </el-collapse-item>
        </el-collapse>
      </el-card>

      <el-card shadow="never" class="ai-section ai-search-card" data-section="search">
        <div class="ai-section-heading">
          <el-icon><Search /></el-icon>
          <div>
            <h3>联网与检索</h3>
            <p>需要时效信息时，为回复补充资料。</p>
          </div>
          <div class="ai-search-master">
            <span>联网搜索</span>
            <ParamHelpButton :label="helpLabel('webSearchEnabled')" @click="openHelp('webSearchEnabled')" />
            <el-switch v-model="form.webSearchEnabled" aria-label="联网搜索总开关" />
          </div>
        </div>
        <div class="ai-search-content">
          <AiSettingField
            label="优先检索通道"
            control-id="ai-search-route"
            help-key="externalSearchEnabled"
            hint="独立服务先检索再交给模型；内建通道需要模型服务商支持。"
            @help="openHelp"
          >
            <el-radio-group
              id="ai-search-route"
              v-model="form.externalSearch.enabled"
              aria-labelledby="ai-search-route-label"
              class="ai-search-route"
            >
              <el-radio-button :value="false">模型内建搜索</el-radio-button>
              <el-radio-button :value="true">独立搜索服务</el-radio-button>
            </el-radio-group>
          </AiSettingField>
          <el-alert
            v-if="!form.webSearchEnabled"
            type="info"
            :closable="false"
            show-icon
            title="联网搜索已关闭。通道参数可先配置，开启总开关并保存后生效。"
          />
          <template v-if="form.externalSearch.enabled">
            <div class="ai-search-fields ai-fields">
              <AiSettingField
                label="搜索服务商"
                control-id="ai-search-provider"
                help-key="externalSearchProvider"
                :hint="esProviderHint"
                @help="openHelp"
              >
                <el-select id="ai-search-provider" v-model="form.externalSearch.provider">
                  <el-option v-for="provider in ES_PROVIDERS" :key="provider.id" :label="provider.name" :value="provider.id" />
                </el-select>
              </AiSettingField>
              <AiSettingField
                label="服务地址"
                control-id="ai-search-url"
                help-key="externalSearchBaseUrl"
                hint="自建和自定义通道需填写地址；其他服务商可留空使用默认端点。"
                @help="openHelp"
              >
                <el-input id="ai-search-url" v-model="form.externalSearch.baseUrl" :placeholder="esBaseUrlPlaceholder" />
              </AiSettingField>
              <AiSettingField
                label="搜索 API Key"
                control-id="ai-search-key"
                help-key="externalSearchApiKey"
                :hint="
                  form.externalSearch.provider === 'searxng'
                    ? 'SearXNG 不使用密钥。'
                    : '留空或保持打码值表示不修改；页面不回显明文。'
                "
                @help="openHelp"
              >
                <el-input
                  id="ai-search-key"
                  v-model="form.externalSearch.apiKey"
                  :disabled="form.externalSearch.provider === 'searxng'"
                  type="password"
                  show-password
                  autocomplete="new-password"
                  :placeholder="esApiKeyPlaceholder"
                />
              </AiSettingField>
              <div class="ai-search-test">
                <el-button :loading="esProbing" :disabled="formBusy" @click="probeExternalSearch">测试搜索</el-button>
                <ParamHelpButton :label="helpLabel('externalSearchTest')" @click="openHelp('externalSearchTest')" />
                <p>
                  最近验证：{{ form.externalSearchStatus.verifiedAt ? fmt(form.externalSearchStatus.verifiedAt) : '尚未验证' }}
                </p>
                <small>使用当前表单实测，不会保存配置。</small>
              </div>
            </div>
            <el-collapse v-model="searchAdvancedOpen" class="ai-budget-collapse">
              <el-collapse-item name="search-advanced">
                <template #title>
                  <span>检索与容错</span>
                  <small>结果条数、超时与失败兜底</small>
                </template>
                <div class="ai-context-grid ai-fields">
                  <AiSettingField
                    label="检索结果条数"
                    control-id="ai-search-count"
                    help-key="externalSearchLimits"
                    hint="更多资料会占用更多上下文。"
                    @help="openHelp"
                  >
                    <el-input-number
                      :key="String(formBusy)"
                      id="ai-search-count"
                      v-model="form.externalSearch.maxResults"
                      :min="1"
                      :max="10"
                      :precision="0"
                      :controls="false"
                    />
                    <span class="ai-unit">条</span>
                  </AiSettingField>
                  <AiSettingField
                    label="检索超时"
                    control-id="ai-search-timeout"
                    help-key="externalSearchLimits"
                    hint="超过等待时间按检索失败处理。"
                    @help="openHelp"
                  >
                    <el-input-number
                      :key="String(formBusy)"
                      id="ai-search-timeout"
                      v-model="form.externalSearch.timeoutSeconds"
                      :min="1"
                      :max="30"
                      :precision="0"
                      :controls="false"
                    />
                    <span class="ai-unit">秒</span>
                  </AiSettingField>
                  <AiSettingField
                    label="失败兜底内建通道"
                    control-id="ai-search-fallback"
                    help-key="externalSearchFallback"
                    hint="独立检索失败时再尝试内建通道；0 条结果不触发兜底。"
                    @help="openHelp"
                  >
                    <el-switch
                      id="ai-search-fallback"
                      v-model="form.externalSearch.fallbackToBuiltin"
                      aria-label="独立检索失败时回退内建搜索"
                    />
                  </AiSettingField>
                </div>
              </el-collapse-item>
            </el-collapse>
          </template>
          <div v-else class="ai-builtin-search">
            <div>
              <h4>模型内建搜索</h4>
              <p v-if="form.webSearchShape">当前策略：{{ shapeLabel(form.webSearchShape) }}</p>
              <p v-else>尚未选用实测策略，当前按 API 地址推断。</p>
              <small v-if="form.webSearchShape">
                文字已验证 ·
                {{
                  form.webSearchImageCapable === true
                    ? '带图已验证'
                    : form.webSearchImageCapable === false
                      ? '带图不支持'
                      : '带图未测'
                }}
              </small>
            </div>
            <el-button :loading="probing" :disabled="formBusy" @click="probeWebSearch">检测联网搜索</el-button>
          </div>
          <AiSettingField
            label="带图时联网"
            control-id="ai-image-search-mode"
            help-key="webSearchImageMode"
            hint="单次请求不能同时看图与搜索时，选择是否增加一次调用来确保检索。"
            @help="openHelp"
          >
            <el-select id="ai-image-search-mode" v-model="form.webSearchImageMode">
              <el-option label="跟随能力" value="auto" />
              <el-option label="确保联网" value="two_stage" />
              <el-option label="关闭带图联网" value="off" />
            </el-select>
          </AiSettingField>
        </div>
      </el-card>

      <el-alert v-if="ready && formError" :title="formError" type="error" :closable="false" show-icon class="ai-form-error" />
      <div class="ai-save-bar">
        <div class="ai-save-status" aria-live="polite">
          <span :class="{ 'is-dirty': isDirty }"></span>
          {{ !ready ? '等待读取配置' : isDirty ? '有修改未保存' : '当前设置已保存' }}
        </div>
        <div class="ai-actions">
          <el-button :disabled="formBusy" :icon="Refresh" @click="reloadConfig">重新加载</el-button>
          <el-button
            type="primary"
            :loading="saving"
            :disabled="formBusy || !isDirty || !!formError"
            :icon="Check"
            @click="save()"
          >
            保存配置
          </el-button>
        </div>
      </div>
    </el-form>

    <!-- 人设管理 -->
    <el-dialog :style="dialogStyle" append-to-body v-model="manageDialog" title="人设管理" width="720px">
      <el-table :data="form.availableCharacters" size="small">
        <el-table-column prop="displayName" label="名称" width="120">
          <template #default="{ row }">
            {{ row.displayName }}
            <el-tag v-if="row.isCustom" size="small" type="warning">自定义</el-tag>
            <el-tag v-else size="small" type="info">内置</el-tag>
          </template>
        </el-table-column>
        <el-table-column prop="name" label="标识" width="100" />
        <el-table-column prop="description" label="描述" min-width="160" show-overflow-tooltip />
        <el-table-column label="操作" width="130">
          <template #default="{ row }">
            <el-button size="small" link type="primary" @click="openEdit(row)">{{ row.isCustom ? '编辑' : '查看' }}</el-button>
            <el-popconfirm
              v-if="row.isCustom"
              title="删除后引用该人设的配置将回退为默认，确认删除？"
              @confirm="removeCharacter(row)"
            >
              <template #reference>
                <el-button size="small" link type="danger">删除</el-button>
              </template>
            </el-popconfirm>
          </template>
        </el-table-column>
      </el-table>
      <template #footer>
        <el-button type="primary" @click="openEdit(null)">新增自定义人设</el-button>
        <el-button @click="manageDialog = false">关闭</el-button>
      </template>
    </el-dialog>

    <!-- 人设编辑/查看 -->
    <el-dialog :style="dialogStyle" append-to-body v-model="editDialog" :title="editTitle" width="560px">
      <el-form :model="charForm" label-position="top" v-if="charForm" :disabled="charSaving">
        <el-form-item label="标识" required>
          <el-input
            v-model="charForm.name"
            :disabled="charReadonly || editing"
            placeholder="英文标识，如 waifu，保存后不可改"
            maxlength="32"
          />
        </el-form-item>
        <el-form-item label="名称" required>
          <el-input v-model="charForm.displayName" :disabled="charReadonly" placeholder="显示名称，如 温柔学姐" />
        </el-form-item>
        <el-form-item label="描述">
          <el-input v-model="charForm.description" :disabled="charReadonly" placeholder="一句话简介，供列表展示" />
        </el-form-item>
        <el-form-item label="角色提示词">
          <el-input
            v-model="charForm.systemPrompt"
            :disabled="charReadonly"
            type="textarea"
            :rows="6"
            placeholder="设定角色身份、语气与行为规则，选中该人设后追加到系统提示词"
          />
        </el-form-item>
      </el-form>
      <template #footer v-if="!charReadonly">
        <el-button @click="editDialog = false">取消</el-button>
        <el-button type="primary" :loading="charSaving" @click="saveCharacter">保存</el-button>
      </template>
    </el-dialog>

    <!-- 保存为预设 -->
    <el-dialog :style="dialogStyle" append-to-body v-model="savePresetDialog" title="保存为预设" width="520px">
      <el-form :model="presetForm" label-position="top" :disabled="presetSaving">
        <el-form-item label="名称" required>
          <el-input v-model="presetForm.name" maxlength="40" show-word-limit placeholder="例如：群聊日常、深度推理" />
        </el-form-item>
        <el-form-item label="备注">
          <el-input v-model="presetForm.description" maxlength="200" show-word-limit placeholder="可选，用来区分用途" />
        </el-form-item>
        <el-form-item label="API Key">
          <el-switch v-model="presetForm.includeApiKey" active-text="一并保存" />
          <div class="hint">关闭后预设不含密钥，应用时沿用当前配置的 Key。</div>
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="savePresetDialog = false">取消</el-button>
        <el-button type="primary" :loading="presetSaving" @click="savePreset">保存</el-button>
      </template>
    </el-dialog>

    <!-- 重命名预设 -->
    <el-dialog :style="dialogStyle" append-to-body v-model="editPresetDialog" title="编辑预设" width="520px">
      <el-form :model="presetEditForm" label-position="top" :disabled="presetSaving">
        <el-form-item label="名称" required>
          <el-input v-model="presetEditForm.name" maxlength="40" show-word-limit />
        </el-form-item>
        <el-form-item label="备注">
          <el-input v-model="presetEditForm.description" maxlength="200" show-word-limit />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="editPresetDialog = false">取消</el-button>
        <el-button type="primary" :loading="presetSaving" @click="savePresetMeta">保存</el-button>
      </template>
    </el-dialog>

    <!-- 预设管理 -->
    <el-dialog :style="dialogStyle" append-to-body v-model="managePresetDialog" title="AI 配置预设" width="760px">
      <p class="ai-library-intro">保存不同模型与用途的整套配置，需要时应用并保存。</p>
      <el-alert v-if="presetsError" type="warning" title="预设暂时无法读取" :closable="false">
        <el-button link type="primary" @click="loadPresets">重新读取</el-button>
      </el-alert>
      <el-empty v-else-if="!presets.length" description="还没有配置预设" :image-size="64" />
      <div v-else class="ai-preset-list" v-loading="presetsLoading || presetSaving || applyingPreset">
        <article v-for="row in presets" :key="row.id" class="ai-preset-item">
          <div class="ai-preset-item-title">
            <strong>{{ row.name }}</strong>
            <el-tag v-if="!row.isValid" size="small" type="danger">数据损坏</el-tag>
            <el-tag size="small" type="info">{{ row.hasApiKey ? '包含密钥' : '沿用当前密钥' }}</el-tag>
          </div>
          <p v-if="row.description" class="ai-preset-description">{{ row.description }}</p>
          <div class="ai-preset-details">
            <span>{{ row.modelName || '未填模型' }}</span>
            <span>{{ row.personalityDisplayName || '未指定人设' }}{{ row.personalityMissing ? '（已失效）' : '' }}</span>
            <span>
              {{ row.webSearchEnabled ? '联网开启' : '联网关闭'
              }}{{ row.webSearchEnabled && row.webSearchShape ? ` · ${shapeShortLabel(row.webSearchShape)}` : '' }}
            </span>
          </div>
          <p class="probe-desc ai-preset-address">{{ row.apiBaseUrl || '未填地址' }}</p>
          <div class="ai-preset-item-footer">
            <small>更新于 {{ fmt(row.updatedAt) }}</small>
            <div class="ai-preset-item-actions">
              <el-button
                size="small"
                type="primary"
                plain
                :disabled="formBusy || !row.isValid"
                @click="applyPresetById(row.id, true)"
              >
                应用并保存
              </el-button>
              <el-dropdown trigger="click" :disabled="formBusy" @command="(command) => presetCommand(command, row)">
                <el-button size="small" :disabled="formBusy" :aria-label="`${row.name}的更多操作`">
                  更多操作
                  <el-icon class="ai-action-icon"><ArrowDown /></el-icon>
                </el-button>
                <template #dropdown>
                  <el-dropdown-menu>
                    <el-dropdown-item command="rename">编辑名称与备注</el-dropdown-item>
                    <el-dropdown-item command="overwrite" :disabled="!!formError">用当前表单覆盖</el-dropdown-item>
                    <el-dropdown-item command="duplicate">复制预设</el-dropdown-item>
                    <el-dropdown-item command="remove" divided>删除预设</el-dropdown-item>
                  </el-dropdown-menu>
                </template>
              </el-dropdown>
            </div>
          </div>
        </article>
      </div>
      <div class="probe-footnote">覆盖只更新预设；应用会立即覆盖并保存当前配置。</div>
      <template #footer>
        <el-button type="primary" :disabled="formBusy || !!formError" @click="openSavePreset(true)">从当前表单新建</el-button>
        <el-button @click="managePresetDialog = false">关闭</el-button>
      </template>
    </el-dialog>

    <!-- 内置默认系统提示词 -->
    <el-dialog :style="dialogStyle" append-to-body v-model="defaultPromptDialog" title="内置默认系统提示词" width="760px">
      <el-input :model-value="form.defaultSystemPrompt || '未获取到默认提示词'" type="textarea" :rows="14" readonly />
      <div class="probe-footnote">系统提示词留空时使用这段内容；选中「人设」后，角色提示词会在此基础上追加。</div>
      <template #footer>
        <el-button @click="defaultPromptDialog = false">关闭</el-button>
      </template>
    </el-dialog>

    <!-- 联网搜索探测：流式逐项进度 → 完整报告 → 每个已验证形状可一键选用 -->
    <el-dialog
      :style="dialogStyle"
      append-to-body
      v-model="probeDialog"
      title="联网搜索探测报告"
      width="760px"
      @close="cancelProbe"
    >
      <el-alert
        v-if="probeReport && !probeReport.reachable"
        type="error"
        :closable="false"
        show-icon
        :title="probeReport.error || '上游不可达'"
        style="margin-bottom: 12px"
      />
      <el-alert
        v-else-if="probeReport && probeReport.authFailed"
        type="error"
        :closable="false"
        show-icon
        :title="probeReport.error || '鉴权失败'"
        style="margin-bottom: 12px"
      />

      <template v-else-if="probePhase === 'running'">
        <div class="probe-summary">正在逐个形状实测当前填写的地址与模型，请稍候……</div>
        <div class="probe-progress">
          <div v-if="probeProgress.length === 0" class="probe-progress-item">
            <el-icon class="is-loading"><Loading /></el-icon>
            <div class="probe-progress-main">正在连接上游…</div>
          </div>
          <div v-for="item in probeProgress" :key="item.shape" class="probe-progress-item">
            <el-icon v-if="!item.done" class="is-loading"><Loading /></el-icon>
            <el-icon v-else :color="item.status === 'verified' ? 'var(--el-color-success)' : 'var(--el-text-color-secondary)'">
              <CircleCheck />
            </el-icon>
            <div class="probe-progress-main">
              <code>{{ item.shape }}</code>
              <div class="probe-desc">{{ item.description }}</div>
            </div>
            <el-tag v-if="item.done" size="small" :type="verdictType(item.status)">{{ verdictLabel(item.status) }}</el-tag>
          </div>
        </div>
      </template>

      <template v-else-if="probeReport">
        <div class="probe-summary">
          以「现在北京时间几点？」实测当前填写的地址与模型（基线 {{ probeReport.basePromptTokens ?? '?' }} prompt tokens）：
          <b>{{ verifiedCount }}</b>
          种形状已验证、{{ ignoredCount }} 种被静默忽略。 对已验证的形状点右侧「选用」，即时写进配置并打开联网搜索。
        </div>
        <el-table :data="probeReport.results" size="small">
          <el-table-column label="请求形状" min-width="190">
            <template #default="{ row }">
              <code style="font-size: 12px">{{ row.shape }}</code>
              <div class="probe-desc">{{ row.description }}</div>
            </template>
          </el-table-column>
          <el-table-column label="判定" width="120">
            <template #default="{ row }">
              <el-tag size="small" :type="verdictType(row.status)">{{ verdictLabel(row.status) }}</el-tag>
            </template>
          </el-table-column>
          <el-table-column label="带图" width="110">
            <template #default="{ row }">
              <el-tag size="small" :type="imageVerdictType(row.imageStatus)">{{ imageVerdictLabel(row.imageStatus) }}</el-tag>
              <div v-if="row.imageNote" class="probe-desc">{{ row.imageNote }}</div>
            </template>
          </el-table-column>
          <el-table-column label="依据" min-width="220">
            <template #default="{ row }">
              <span>{{ row.note || '-' }}</span>
              <div v-if="row.groundingField" class="probe-desc">证据字段：{{ row.groundingField }}</div>
              <div v-if="row.evidence" class="probe-desc">证据类型：{{ evidenceLabel(row.evidence) }}</div>
            </template>
          </el-table-column>
          <el-table-column label="操作" width="88" fixed="right">
            <template #default="{ row }">
              <el-button v-if="form.webSearchShape === row.shape" size="small" type="success" plain disabled>已选用</el-button>
              <el-tooltip v-else :disabled="row.status === 'verified'" content="该形状未通过实测，不能选用" placement="top">
                <span>
                  <el-button size="small" type="primary" :disabled="row.status !== 'verified'" @click="applyProbe(row)">
                    选用
                  </el-button>
                </span>
              </el-tooltip>
            </template>
          </el-table-column>
        </el-table>
        <div class="probe-footnote">
          「已验证」= 响应携带检索证据字段，或带搜索比基线多出大量 prompt token（搜索结果被注入），
          或回答给出了与当前北京时间吻合的时刻而基线给不出（部分中转会抹掉前两种证据）。 「被静默忽略」= 请求 2xx
          但无任何检索证据——这正是开关打开却毫无效果的情形。
          「选用」会把形状写入配置并打开联网搜索；换地址或模型后旧策略会自动清空，需要重新检测。
          「带图」列需要文字联网先通过才会测试；「看不到图」表示上游接受了请求但静默丢弃了图片。
        </div>
      </template>

      <el-alert v-else type="info" :closable="false" show-icon title="尚未收到探测结果" />

      <template #footer>
        <el-button @click="probeDialog = false">关闭</el-button>
      </template>
    </el-dialog>

    <!-- 独立搜索服务测试：用表单当前值实测一次检索，不需要先保存 -->
    <el-dialog
      :style="dialogStyle"
      append-to-body
      v-model="esProbeDialog"
      title="测试搜索 · 独立搜索服务"
      width="640px"
      @close="cancelEsProbe"
    >
      <template v-if="esProbing">
        <div class="probe-summary">正在用表单当前值实测一次检索（不经缓存、不会保存配置），请稍候……</div>
        <div class="probe-progress">
          <div class="probe-progress-item">
            <el-icon class="is-loading"><Loading /></el-icon>
            <div class="probe-progress-main">正在请求 {{ esProviderName(form.externalSearch.provider) }}……</div>
          </div>
        </div>
      </template>

      <template v-else-if="esProbeError">
        <el-alert type="error" :closable="false" show-icon :title="esProbeError" />
        <div class="probe-footnote">请修正后重试；测试不保存任何配置。</div>
      </template>

      <template v-else-if="esProbeResult">
        <div class="es-verdict">
          <el-tag :type="esVerdictType(esProbeResult.verdict)">{{ esVerdictLabel(esProbeResult.verdict) }}</el-tag>
          <span>{{ esProbeResult.detail }}</span>
        </div>
        <div v-if="(esProbeResult.hits || []).length" class="es-hits">
          <a v-for="h in esProbeResult.hits" :key="h.url" class="es-hit" :href="h.url" target="_blank" rel="noopener noreferrer">
            <span class="es-hit-title">{{ h.title || h.url }}</span>
            <span class="es-hit-url">{{ esHost(h.url) }}</span>
          </a>
        </div>
        <div class="probe-footnote">
          测试使用表单当前值（含未保存的修改），Key 为打码值时按已保存的 Key 实测。
          验证通过与当前保存一致时，「最近验证」时间会自动更新；服务商、地址或 Key 变更后需重新测试。
        </div>
      </template>

      <el-alert v-else type="info" :closable="false" show-icon title="尚未收到测试结果" />

      <template #footer>
        <el-button @click="esProbeDialog = false">关闭</el-button>
      </template>
    </el-dialog>

    <ParamHelpDrawer
      v-model="helpVisible"
      :title="helpTitle"
      :lines="helpLines"
      :current="helpCurrent"
      :index="helpIndex"
      :total="helpTotal"
      @step="stepHelp"
    />
  </div>
</template>

<script setup>
import { ref, computed, watch, onMounted, onBeforeUnmount } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Loading, CircleCheck, Collection, FolderAdd, ArrowRight, ArrowDown, Connection, User, ChatDotRound, Search, Refresh, Check } from '@element-plus/icons-vue'
import api from '../api'
import { tokenBudget, DEFAULT_INPUT_BUDGET } from '../tokenBudget'
import { useUnsavedChanges } from '../useUnsavedChanges'
import { useParamHelp } from '../paramHelp'
import ParamHelpButton from '../components/ParamHelpButton.vue'
import ParamHelpDrawer from '../components/ParamHelpDrawer.vue'
import AiSettingField from '../components/AiSettingField.vue'
import { normalizeAiSettings, aiSettingsError } from '../aiSettings'

const withExternalDefaults = normalizeAiSettings

const form = ref(withExternalDefaults({ availableProviders: [], availableCharacters: [] }))
const presetId = ref('')
const loading = ref(false)
const saving = ref(false)
const loadError = ref(false), presetsError = ref(false), presetsLoading = ref(false)
const advancedOpen = ref([]), searchAdvancedOpen = ref([])

// 配置预设：当前选中项、已保存列表与管理弹窗状态
const presets = ref([])
const selectedPresetId = ref(null)
const applyingPreset = ref(false)
const savePresetDialog = ref(false)
const editPresetDialog = ref(false)
const managePresetDialog = ref(false)
const presetSaving = ref(false)
const presetForm = ref({ name: '', description: '', includeApiKey: true })
const presetEditForm = ref({ id: null, name: '', description: '' })
const defaultPromptDialog = ref(false)
const dialogStyle = { maxWidth: 'calc(100vw - 32px)' }
// 上次读取/应用/保存后的表单快照，用来判断有没有未保存修改
const baseline = ref('')
const ready = computed(() => !!baseline.value)
const formBusy = computed(() => loading.value || !ready.value || saving.value || applyingPreset.value || presetSaving.value)
const formError = computed(() => aiSettingsError(form.value))
const formatTokens = value => Number.isFinite(value) ? value.toLocaleString('zh-CN') : '—'

// 只取服务端可写字段；预设接口和 PUT /ai 共用同一份契约
const configPayload = () => ({
  apiBaseUrl: form.value.apiBaseUrl || '',
  apiKey: form.value.apiKey || '',
  modelName: form.value.modelName || '',
  systemPrompt: form.value.systemPrompt || '',
  personality: form.value.personality || '',
  temperature: form.value.temperature ?? 0.7,
  maxTokens: form.value.maxTokens ?? 2048,
  maxContextMessages: form.value.maxContextMessages ?? 20,
  contextWindowTokens: form.value.contextWindowTokens ?? 32768,
  maxInputTokens: form.value.maxInputTokens ?? DEFAULT_INPUT_BUDGET,
  groupMemoryInjectionEnabled: form.value.groupMemoryInjectionEnabled ?? true,
  maxAttachedImages: form.value.maxAttachedImages ?? 3,
  webSearchEnabled: !!form.value.webSearchEnabled,
  webSearchEndpoint: form.value.webSearchEndpoint || '',
  webSearchShape: form.value.webSearchShape || '',
  webSearchImageMode: form.value.webSearchImageMode || 'auto',
  // 独立搜索服务整卡随表单一起提交（Key 打码值=不修改），预设快照继承同一字段
  externalSearch: {
    enabled: !!form.value.externalSearch?.enabled,
    provider: form.value.externalSearch?.provider || 'searxng',
    baseUrl: form.value.externalSearch?.baseUrl || '',
    apiKey: form.value.externalSearch?.apiKey || '',
    maxResults: form.value.externalSearch?.maxResults ?? 5,
    timeoutSeconds: form.value.externalSearch?.timeoutSeconds ?? 8,
    fallbackToBuiltin: !!form.value.externalSearch?.fallbackToBuiltin
  }
})

const isDirty = computed(() =>
  !!baseline.value && JSON.stringify(configPayload()) !== baseline.value)
const budget = computed(() => tokenBudget(form.value))
watch(() => budget.value.error, error => { if (error && !advancedOpen.value.includes('capacity')) advancedOpen.value.push('capacity') })
watch(formError, error => { if (error.includes('检索') && !searchAdvancedOpen.value.includes('search-advanced')) searchAdvancedOpen.value.push('search-advanced') })
const confirmDiscard = useUnsavedChanges(isDirty)
const reloadConfig = async () => { if (await confirmDiscard()) await load() }

const selectedPreset = computed(() =>
  presets.value.find((p) => p.id === selectedPresetId.value) || null)

// 联网搜索探测：流式逐项进度；全部完成后展示报告，可对已验证形状“选用”
const probing = ref(false)
const probeDialog = ref(false)
const probeReport = ref(null)
const probePhase = ref('running') // running | done
const probeProgress = ref([])
let probeAbort = null
const verifiedCount = computed(() => (probeReport.value?.results || []).filter((r) => r.status === 'verified').length)
const ignoredCount = computed(() => (probeReport.value?.results || []).filter((r) => r.status === 'no_evidence').length)
const verdictLabel = (s) => ({
  verified: '已验证', no_evidence: '被静默忽略', rejected: '被上游拒绝',
  auth_failed: '鉴权失败', unreachable: '不可达', upstream_error: '上游错误',
  no_native_endpoint: '无原生端点'
}[s] || s)
const verdictType = (s) => ({
  verified: 'success', no_evidence: 'warning', rejected: 'info',
  auth_failed: 'danger', unreachable: 'danger', upstream_error: 'danger',
  no_native_endpoint: 'info'
}[s] || 'info')
const evidenceLabel = (e) => ({
  grounding_metadata: '检索元数据',
  prompt_token_delta: 'Prompt token 差分',
  content_time: '当前时间比对'
}[e] || e)
const imageVerdictLabel = (s) => ({
  verified: '已验证', image_rejected: '看不到图', no_evidence: '无检索证据',
  rejected: '被拒绝', unreachable: '不可达', upstream_error: '上游错误',
  no_native_endpoint: '无原生端点', not_tested: '未测'
}[s] || s)
const imageVerdictType = (s) => ({
  verified: 'success', image_rejected: 'warning', no_evidence: 'warning',
  rejected: 'info', unreachable: 'danger', upstream_error: 'danger',
  no_native_endpoint: 'info', not_tested: 'info'
}[s] || 'info')
const shapeLabel = (s) => ({
  enable_search: 'enable_search（通义/百炼）',
  web_search_tool: 'web_search 工具（智谱）',
  google_search_tool: 'google_search 函数工具',
  web_search_options: 'web_search_options',
  builtin_web_search: '内联 google_search',
  gemini_native: 'Gemini 原生 google_search'
}[s] || s)
// 管理表格空间有限，只展示形状标识，完整说明留给探测报告
const shapeShortLabel = (s) => ({
  enable_search: 'enable_search',
  web_search_tool: 'web_search',
  google_search_tool: 'google_search',
  web_search_options: 'web_search_options',
  builtin_web_search: '内联搜索',
  gemini_native: 'Gemini 原生'
}[s] || s)
const fmt = (s) => (s ? new Date(s + (s.endsWith('Z') ? '' : 'Z')).toLocaleString('zh-CN', { hour12: false }) : '-')

const handleProbeEvent = (ev) => {
  if (ev.type === 'start') {
    probeProgress.value = (ev.cases || []).map((c) => ({ ...c, done: false, status: '' }))
    return
  }
  if (ev.type === 'case' && ev.result) {
    const item = probeProgress.value.find((i) => i.shape === ev.result.shape)
    if (item) {
      item.done = true
      item.status = ev.result.status
    } else {
      probeProgress.value.push({ ...ev.result, done: true })
    }
    return
  }
  if (ev.type === 'done') {
    probeReport.value = ev.report
    probePhase.value = 'done'
    return
  }
  if (ev.type === 'error') {
    probeReport.value = {
      reachable: ev.reachable ?? false,
      authFailed: ev.authFailed ?? false,
      error: ev.error,
      results: []
    }
    probePhase.value = 'done'
  }
}

const probeWebSearch = async () => {
  if (!form.value.apiBaseUrl || !form.value.modelName) {
    ElMessage.warning('探测前先填写 API 地址与模型名')
    return
  }
  probeAbort?.abort()
  const aborter = new AbortController()
  probeAbort = aborter
  probing.value = true
  probeDialog.value = true
  probeReport.value = null
  probeProgress.value = []
  probePhase.value = 'running'

  try {
    const res = await fetch('/api/config/ai/probe-websearch/stream', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        apiBaseUrl: form.value.apiBaseUrl,
        apiKey: form.value.apiKey,
        modelName: form.value.modelName
      }),
      credentials: 'same-origin',
      signal: aborter.signal
    })
    if (res.status === 401) {
      location.href = `/login.html?return=${encodeURIComponent(location.pathname + location.hash)}`
      return
    }
    if (!res.ok) {
      const text = await res.text()
      throw new Error(text || `HTTP ${res.status}`)
    }
    if (!res.body) throw new Error('当前浏览器不支持流式响应')

    const reader = res.body.getReader()
    const decoder = new TextDecoder()
    let buffer = ''
    while (true) {
      const { value, done } = await reader.read()
      if (done) break
      buffer += decoder.decode(value, { stream: true })
      let idx
      while ((idx = buffer.indexOf('\n')) >= 0) {
        const line = buffer.slice(0, idx).trim()
        buffer = buffer.slice(idx + 1)
        if (line) handleProbeEvent(JSON.parse(line))
      }
    }
    if (buffer.trim()) handleProbeEvent(JSON.parse(buffer.trim()))
    if (probePhase.value === 'running') {
      probeReport.value = { reachable: false, error: '探测流意外结束，未收到完整报告', results: [] }
      probePhase.value = 'done'
    }
  } catch (err) {
    if (err.name === 'AbortError') return
    probeReport.value = { reachable: false, error: `探测请求失败：${err.message || err}`, results: [] }
    probePhase.value = 'done'
  } finally {
    if (probeAbort === aborter) {
      probeAbort = null
      probing.value = false
    }
  }
}

const cancelProbe = () => {
  probeAbort?.abort()
  probeAbort = null
  probing.value = false
}

const applyProbe = async (row) => {
  if (row.status !== 'verified') return
  form.value.webSearchEndpoint = row.shape === 'gemini_native' ? 'gemini' : 'openai'
  form.value.webSearchShape = row.shape
  form.value.webSearchEnabled = true
  const imageNote = row.imageStatus === 'verified'
    ? '，带图联网已验证'
    : row.imageStatus === 'not_tested' || !row.imageStatus
      ? '，带图联网未测'
      : '，但带图联网未通过'
  try {
    await save(`已选用「${shapeLabel(row.shape)}」${imageNote}；联网搜索已开启`)
  } catch {
    // 保存失败由 axios 拦截器提示，表单保留当前选择，方便重试
  }
}

onBeforeUnmount(cancelProbe)

// ── 独立搜索服务（预检索注入）───────────────────────────────────────────────
// 服务商白名单与后端 ExternalSearchConfig.ProviderIds 一致；placeholder 对齐各官方端点
const ES_PROVIDERS = [
  { id: 'searxng', name: 'SearXNG（自建，免 Key）' },
  { id: 'tavily', name: 'Tavily' },
  { id: 'bocha', name: '博查 Bocha' },
  { id: 'brave', name: 'Brave Search' },
  { id: 'deepseek', name: 'DeepSeek 搜索' },
  { id: 'custom', name: '自定义（JSON API）' }
]
const esProviderName = (id) => ES_PROVIDERS.find((p) => p.id === id)?.name || id
// 与后端 ExternalSearchConfig.RequiresBaseUrl / RequiresApiKey 保持一致
const esNeedsBaseUrl = (id) => id === 'searxng' || id === 'custom'
const esNeedsApiKey = (id) => id !== 'searxng' && id !== 'custom'
const esBaseUrlPlaceholder = computed(() => ({
  searxng: 'https://searx.example.com（实例根地址）',
  tavily: '留空 = 官方端点 https://api.tavily.com',
  bocha: '留空 = 官方端点 https://api.bochaai.com/v1/web-search',
  brave: '留空 = 官方端点 https://api.search.brave.com/res/v1/web/search',
  deepseek: '留空 = 官方端点 https://api.deepseek.com/v1/tools/search',
  custom: '必填：完整搜索端点，如 https://api.example.com/search'
}[form.value.externalSearch?.provider] || ''))
const esApiKeyPlaceholder = computed(() => ({
  searxng: 'SearXNG 免 Key',
  custom: '选填；填写后以 Bearer 发送'
}[form.value.externalSearch?.provider] || '留空或保持打码值表示不修改'))
const esProviderHint = computed(() => ({
  searxng: '免 Key。填写实例根地址即可；实例需在 settings.yml 的 search.formats 中启用 json 输出。',
  tavily: 'Key 必填；服务地址留空使用官方端点，也可指向自建网关。',
  bocha: 'Key 必填；服务地址留空使用官方端点。',
  brave: 'Key 必填；服务地址留空使用官方端点。',
  deepseek: 'Key 必填（DeepSeek API Key）；服务地址可留空或指向兼容网关。',
  custom: '按固定契约接入：POST JSON {query,count}，返回 results[]（title/url/snippet）；Key 选填，填了则以 Bearer 发送。'
}[form.value.externalSearch?.provider] || ''))

const esVerdictLabel = (v) => ({
  verified: '验证通过', no_results: '连通但 0 条结果', auth_failed: '鉴权失败', failed: '测试失败'
}[v] || v)
const esVerdictType = (v) => ({
  verified: 'success', no_results: 'warning', auth_failed: 'danger', failed: 'danger'
}[v] || 'info')
const esHost = (u) => { try { return new URL(u).host } catch { return u } }

// 独立搜索测试：不落库、不依赖保存，直接测表单当前值（Key 打码值由后端按已存 Key 解析）
const esProbing = ref(false)
const esProbeDialog = ref(false)
const esProbeResult = ref(null)
const esProbeError = ref('')
let esProbeAbort = null

const probeExternalSearch = async () => {
  const es = form.value.externalSearch
  // 与后端校验同判据的前置检查：地址必填项与 Key 必填项按服务商能力判断
  let missing = ''
  if (esNeedsBaseUrl(es.provider) && !es.baseUrl.trim())
    missing = es.provider === 'custom' ? '自定义通道需要填写服务地址' : 'SearXNG 需要填写实例地址'
  else if (esNeedsApiKey(es.provider) && !es.apiKey.trim())
    missing = `${esProviderName(es.provider)} 需要填写 API Key`
  if (missing) {
    ElMessage.warning(missing)
    return
  }
  esProbeAbort?.abort()
  const aborter = new AbortController()
  esProbeAbort = aborter
  esProbing.value = true
  esProbeDialog.value = true
  esProbeResult.value = null
  esProbeError.value = ''
  try {
    const { data } = await api.post('/config/ai/probe-search', {
      enabled: true,
      provider: es.provider,
      baseUrl: es.baseUrl,
      apiKey: es.apiKey,
      maxResults: es.maxResults,
      timeoutSeconds: es.timeoutSeconds,
      fallbackToBuiltin: false
    }, { signal: aborter.signal, timeout: 45000 })
    esProbeResult.value = data
    if (data?.verdict === 'verified') await refreshExternalStatus()
  } catch (err) {
    if (err.name === 'AbortError' || err.code === 'ERR_CANCELED') return
    esProbeError.value = err.response?.data?.error || err.message || '测试请求失败'
  } finally {
    if (esProbeAbort === aborter) {
      esProbeAbort = null
      esProbing.value = false
    }
  }
}

const cancelEsProbe = () => {
  esProbeAbort?.abort()
  esProbeAbort = null
  esProbing.value = false
}

// verified 后只回读验证时间，不整表刷新，避免覆盖未保存的表单修改
const refreshExternalStatus = async () => {
  try {
    const { data } = await api.get('/config/ai')
    form.value.externalSearchStatus = data?.externalSearchStatus || { verifiedAt: null }
  } catch { /* 状态刷新失败不影响测试结论 */ }
}

onBeforeUnmount(cancelEsProbe)

// 人设管理状态
const manageDialog = ref(false)
const editDialog = ref(false)
const charForm = ref(null)
const charReadonly = ref(false)
const editing = ref(false)
const charSaving = ref(false)

const editTitle = computed(() => {
  if (charReadonly.value) return '查看内置人设'
  return editing.value ? `编辑人设：${charForm.value?.name}` : '新增自定义人设'
})

const presetHelp = computed(() => {
  const p = (form.value.availableProviders || []).find((x) => x.id === presetId.value)
  return p?.keyHelpText || ''
})

const HELP_ORDER = [
  'configPreset', 'presetId', 'apiBaseUrl', 'apiKey', 'modelName', 'personality', 'temperature', 'systemPrompt',
  'maxTokens', 'maxContextMessages', 'maxAttachedImages', 'groupMemoryInjectionEnabled', 'contextWindowTokens', 'maxInputTokens',
  'webSearchEnabled', 'webSearchImageMode',
  'externalSearchEnabled', 'externalSearchProvider', 'externalSearchBaseUrl', 'externalSearchApiKey',
  'externalSearchLimits', 'externalSearchFallback', 'externalSearchTest'
]

const HELP = {
  configPreset: {
    title: '配置预设',
    value: () => selectedPreset.value?.name || '未选择',
    lines: [
      '把当前整套 AI 配置保存成快照，之后可以一键切换。',
      '「服务商」只填地址和模型；配置预设还会包含人设、提示词、参数和联网设置。',
      '「保存为预设」保存的是当前表单，包括还没点「保存配置」的修改。',
      '应用预设会立即覆盖并保存当前配置；之后对表单的修改不会自动同步回预设。'
    ]
  },
  presetId: {
    title: '服务商',
    value: () => (form.value.availableProviders || []).find((p) => p.id === presetId.value)?.name || '未选择',
    lines: [
      '选一个服务商会填好 API 地址和默认模型，API Key 仍要自己填。',
      '没有合适的选「自定义」，地址和模型名自己写。',
      '它只负责填一次，和上面的「配置预设」不是一回事。'
    ]
  },
  apiBaseUrl: {
    title: 'API 地址',
    value: (f) => f.apiBaseUrl || '未填写',
    lines: [
      'OpenAI 兼容接口的根地址，一般以 /v1 结尾。',
      '选了预设会自动填好，只有自建或中转才需要改。'
    ]
  },
  apiKey: {
    title: 'API Key',
    value: (f) => (f.apiKey ? '已配置' : '未配置'),
    lines: [
      '只存在本机数据库，页面不回显明文。',
      '留空或保持打码值表示不修改；填新值会直接覆盖。'
    ]
  },
  modelName: {
    title: '模型',
    value: (f) => f.modelName || '未填写',
    lines: [
      '填服务商文档里的模型名，例如 deepseek-chat、qwen-plus。',
      '选了预设会带一个默认值，可以改。'
    ]
  },
  personality: {
    title: '人设',
    value: (f) => (f.availableCharacters || []).find((c) => c.name === f.personality)?.displayName || '未指定',
    lines: [
      '全局人设，作用于 @ 回复与私聊。',
      '「群管理」里为单个群指定的人设优先级更高。',
      '内置人设只能查看，自定义人设可以增删改。'
    ]
  },
  systemPrompt: {
    title: '系统提示词',
    value: (f) => (f.systemPrompt ? `${f.systemPrompt.length} 字` : '内置默认'),
    lines: [
      '留空则使用内置默认提示词，可点下方「查看内置默认提示词」看全文。',
      '「群管理」里的「群专属提示词」会整段替换掉这里的全局提示词，是替换不是追加。'
    ]
  },
  temperature: {
    title: 'Temperature',
    value: (f) => `${f.temperature ?? '-'}`,
    lines: [
      '越高越随机，越低越守着说。',
      '默认 0.7；想让它稳一点就调到 0.3 上下。'
    ]
  },
  maxTokens: {
    title: '回复输出上限',
    value: (f) => `${f.maxTokens}`,
    lines: [
      '单次回复的输出上限，默认 2048。',
      '与「择机发言」的「判定输出上限」互不影响，各管各的。'
    ]
  },
  contextWindowTokens: {
    title: '模型上下文容量',
    value: (f) => `${f.contextWindowTokens ?? 32768}`,
    lines: ['按服务商公布的模型上下文容量填写，默认 32768。', '输入、输出和安全余量共同占用这段容量；切换模型时请核对。']
  },
  maxInputTokens: {
    title: '输入预算',
    value: (f) => `${f.maxInputTokens ?? DEFAULT_INPUT_BUDGET}`,
    lines: [`历史、当前消息、系统提示与图片共用的本地输入预算，新配置默认 ${DEFAULT_INPUT_BUDGET}，已有配置保持原值。`, '文本和图片使用保守估算，并额外预留输出和安全余量；超过预算时先裁掉旧历史。', '当前消息或附件本身过大时会提示缩短内容，不会静默删掉当前问题。']
  },
  groupMemoryInjectionEnabled: {
    title: '群记忆用于回复',
    value: (f) => f.groupMemoryInjectionEnabled ? '开启' : '关闭',
    lines: ['普通 @ 回复使用管理员维护的群记忆，避免主动说话与被询问时记忆不一致。', '本群设置为关闭时不注入；择机发言仍受它自己的全局开关控制。']
  },
  maxContextMessages: {
    title: '上下文条数',
    value: (f) => `${f.maxContextMessages} 条`,
    lines: [
      '@ 回复与私聊带多少条历史消息，默认 20。',
      '「择机发言」有自己的「背景历史条数」，不受这里影响。'
    ]
  },
  maxAttachedImages: {
    title: '附图张数',
    value: (f) => (f.maxAttachedImages > 0 ? `${f.maxAttachedImages} 张` : '不看图'),
    lines: [
      '@ 回复与私聊最多带几张图，取消息里最靠前的几张；0 = 不看图。',
      '表情包和动图不会附给模型（GIF 会把请求打成 500），实际附上的可能少于这个数。',
      '「择机发言」有自己的「附图张数」，在那边单独设置。'
    ]
  },
  webSearchEnabled: {
    title: '联网搜索',
    value: (f) => (f.webSearchEnabled ? '开启' : '关闭'),
    lines: [
      '打开后，@ 回复与私聊可以先联网检索再回答。',
      '优先检索通道选「独立搜索服务」时，填写搜索参数，再点「测试搜索」检查连接。',
      '选「模型内建搜索」时，先点「检测联网搜索」，在报告里选用已验证的策略；未选用时按 API 地址推断。',
      '内建通道需要服务商支持；检索会增加等待时间与调用费用。'
    ]
  },
  webSearchImageMode: {
    title: '带图时联网',
    value: (f) => ({
      auto: '跟随能力', two_stage: '确保联网', off: '关闭'
    }[f.webSearchImageMode] || '跟随能力'),
    lines: [
      '服务商支持“图片 + 搜索”同请求时，会一次完成。',
      '跟随能力：单次不支持时本次只回答不检索，回复速度不变。',
      '确保联网：单次不支持时先看图生成检索词再联网回答，多一次调用、慢 2~5 秒。',
      '关闭：带图消息一律不检索。',
      '表情包和动图仍不会附图。'
    ]
  },
  externalSearchEnabled: {
    title: '独立搜索服务',
    value: (f) => (f.externalSearch?.enabled ? '开启' : '关闭'),
    lines: [
      '由本程序先检索、再把结果作为资料注入对话（预检索注入），不依赖模型服务商支持搜索。',
      '适合中转站与不带内建搜索的模型，在「优先检索通道」中选择。',
      '总开关仍是「联网搜索」：两者同时开启才会真正联网检索。',
      '配置默认关闭，保存后才会生效。'
    ]
  },
  externalSearchProvider: {
    title: '搜索服务商',
    value: (f) => ({
      searxng: 'SearXNG', tavily: 'Tavily', bocha: '博查 Bocha', brave: 'Brave Search',
      deepseek: 'DeepSeek 搜索', custom: '自定义（JSON API）'
    }[f.externalSearch?.provider] || '未选择'),
    lines: [
      'SearXNG：自建元搜索引擎，免 Key，需一个开了 JSON format 的实例。',
      'Tavily / 博查 / Brave / DeepSeek：商用搜索 API，Key 必填，服务地址可留空用官方端点。',
      '自定义：填完整搜索端点，按固定 JSON 契约（POST {query,count} → results[]）接入；Key 选填。',
      '换服务商后需重新「测试搜索」获得新的验证时间。'
    ]
  },
  externalSearchBaseUrl: {
    title: '服务地址',
    value: (f) => f.externalSearch?.baseUrl || '未填写',
    lines: [
      'SearXNG 必填：实例根地址（不含 /search 路径），例如 https://searx.example.com。',
      '实例需在 settings.yml 的 search.formats 中加上 json，否则测试会提示未开启 JSON 输出。',
      '自定义通道必填：完整搜索端点；客户端按 POST JSON 契约请求。',
      '其余服务商留空即用官方默认端点；填了则指向自建网关，必须是 http/https 开头的完整 URL。'
    ]
  },
  externalSearchApiKey: {
    title: '搜索服务 API Key',
    value: (f) => {
      const provider = f.externalSearch?.provider
      if (provider === 'searxng') return '免 Key'
      if (provider === 'custom') return f.externalSearch?.apiKey ? '已配置（选填）' : '未填写（选填）'
      return f.externalSearch?.apiKey ? '已配置' : '未配置'
    },
    lines: [
      'SearXNG 免 Key，输入框禁用。',
      'Tavily / 博查 / Brave / DeepSeek 必填；自定义通道选填，填了则以 Bearer 发送。',
      '只存在本机数据库，页面不回显明文。',
      '留空或保持打码值表示不修改；填新值会直接覆盖。'
    ]
  },
  externalSearchLimits: {
    title: '检索参数',
    value: (f) => `${f.externalSearch?.maxResults ?? 5} 条 / ${f.externalSearch?.timeoutSeconds ?? 8} 秒`,
    lines: [
      '结果条数：单次检索最多取回并注入多少条，1~10。',
      '超时：单次检索的最长等待秒数，1~30；超时按失败处理。',
      '条数越多资料越全，但占用更多上下文、回复更慢。'
    ]
  },
  externalSearchFallback: {
    title: '失败兜底内建通道',
    value: (f) => (f.externalSearch?.fallbackToBuiltin ? '开启' : '关闭'),
    lines: [
      '外搜失败（超时、5xx、鉴权等）时，回退到服务商内建联网通道再试一次。',
      '默认关闭：失败就按没有联网资料回答，回复速度不受影响。',
      '0 条结果视为无资料，不会触发内建检索，避免同一条消息双份检索费用。',
      '只在上方「联网搜索」也可用时才有兜底效果。'
    ]
  },
  externalSearchTest: {
    title: '测试搜索',
    value: () => '用表单当前值实测',
    lines: [
      '直接用表单当前值（含未保存的修改）真实检索一次，不需要先保存。',
      'Key 是打码值时按已保存的 Key 实测；不会保存任何配置。',
      '验证通过与当前保存一致时，「最近验证」时间会自动更新。',
      '服务商、服务地址或 Key 变更后，旧验证作废，需要重新测试。'
    ]
  }
}

const {
  visible: helpVisible, index: helpIndex, total: helpTotal, title: helpTitle,
  lines: helpLines, current: helpCurrent, label: helpLabel, open: openHelp, step: stepHelp
} = useParamHelp(HELP, HELP_ORDER, () => form.value)

const load = async () => {
  loading.value = true
  try {
    const { data } = await api.get('/config/ai')
    form.value = withExternalDefaults(data)
    baseline.value = JSON.stringify(configPayload())
    presetId.value = ''
    loadError.value = false
  } catch {
    loadError.value = true
    // API 层提示错误；读取失败时保留当前草稿，不开放空表单覆盖服务器配置。
  } finally { loading.value = false }
}

const loadPresets = async () => {
  presetsLoading.value = true
  try {
    const { data } = await api.get('/config/ai/presets')
    presets.value = data
    presetsError.value = false
  } catch { presetsError.value = true }
  finally { presetsLoading.value = false }
}

const openSavePreset = (fromManage = false) => {
  if (formBusy.value || formError.value) return
  if (fromManage) managePresetDialog.value = false
  presetForm.value = { name: '', description: '', includeApiKey: true }
  savePresetDialog.value = true
}

const savePreset = async () => {
  if (formBusy.value || formError.value) return
  const name = presetForm.value.name.trim()
  if (!name) {
    ElMessage.warning('预设名称不能为空')
    return
  }
  presetSaving.value = true
  try {
    const { data } = await api.post('/config/ai/presets', {
      name,
      description: presetForm.value.description.trim(),
      includeApiKey: presetForm.value.includeApiKey,
      config: configPayload()
    })
    savePresetDialog.value = false
    await loadPresets()
    if (data?.id) selectedPresetId.value = data.id
    ElMessage.success(`已保存预设「${data?.name || name}」`)
  } catch { /* 请求失败时保留预设表单。 */ }
  finally { presetSaving.value = false }
}

const openManagePresets = async () => {
  await loadPresets()
  managePresetDialog.value = true
}

const openPresetMeta = (row) => {
  presetEditForm.value = { id: row.id, name: row.name, description: row.description || '' }
  editPresetDialog.value = true
}

const savePresetMeta = async () => {
  const name = presetEditForm.value.name.trim()
  if (!name) {
    ElMessage.warning('预设名称不能为空')
    return
  }
  presetSaving.value = true
  try {
    await api.put(`/config/ai/presets/${presetEditForm.value.id}`, {
      name,
      description: presetEditForm.value.description.trim()
    })
    editPresetDialog.value = false
    await loadPresets()
    ElMessage.success('预设已更新')
  } catch { /* API 层提示错误，保留名称与备注。 */ }
  finally { presetSaving.value = false }
}

const overwritePreset = async (row) => {
  if (formBusy.value || formError.value) return
  const lines = [`将用当前表单内容覆盖预设「${row.name}」的配置快照。`]
  if (isDirty.value) lines.push('当前表单有未保存修改，会一并保存进预设。')
  lines.push('API Key 按当前生效配置保存；表单里填了新 Key 则保存新 Key。')
  try {
    await ElMessageBox.confirm(lines.join(''), '覆盖预设', {
      type: 'warning', confirmButtonText: '覆盖', cancelButtonText: '取消'
    })
  } catch {
    return
  }

  presetSaving.value = true
  try {
    await api.put(`/config/ai/presets/${row.id}`, {
      name: row.name,
      description: row.description,
      includeApiKey: true,
      config: configPayload()
    })
    await loadPresets()
    ElMessage.success(`预设「${row.name}」已更新`)
  } catch { /* API 层提示错误，保留当前草稿。 */ }
  finally { presetSaving.value = false }
}

const duplicatePreset = async (row) => {
  if (formBusy.value) return
  presetSaving.value = true
  try {
    const { data } = await api.post(`/config/ai/presets/${row.id}/duplicate`)
    await loadPresets()
    ElMessage.success(`已复制为「${data?.name || '副本'}」`)
  } catch { /* API 层提示错误。 */ }
  finally { presetSaving.value = false }
}

const removePreset = async (row) => {
  if (formBusy.value) return
  try {
    await ElMessageBox.confirm(`删除预设「${row.name}」？当前生效配置不受影响。`, '删除预设', { type: 'warning', confirmButtonText: '删除', cancelButtonText: '取消' })
  } catch { return }
  presetSaving.value = true
  try {
    await api.delete(`/config/ai/presets/${row.id}`)
    if (selectedPresetId.value === row.id) selectedPresetId.value = null
    await loadPresets()
    ElMessage.success('预设已删除')
  } catch { /* API 层提示错误。 */ }
  finally { presetSaving.value = false }
}

const presetCommand = (command, row) => {
  if (command === 'rename') openPresetMeta(row)
  else if (command === 'overwrite') overwritePreset(row)
  else if (command === 'duplicate') duplicatePreset(row)
  else if (command === 'remove') removePreset(row)
}

const applyPresetById = async (id, fromManage = false) => {
  if (formBusy.value) return
  const p = presets.value.find((x) => x.id === id)
  if (!p) return
  if (!p.isValid) {
    ElMessage.error('预设数据损坏，无法应用')
    return
  }

  const lines = [`应用预设「${p.name}」会覆盖当前表单并立即保存。`]
  if (isDirty.value) lines.push('当前表单有未保存修改，这些修改会丢失。')
  if (!p.hasApiKey) lines.push('该预设未保存 API Key，将沿用当前 Key。')
  if (p.personalityMissing) lines.push(`人设「${p.personality}」已不存在，应用后会按自由文本处理。`)
  try {
    await ElMessageBox.confirm(lines.join(''), '应用预设', {
      type: 'warning', confirmButtonText: '应用并保存', cancelButtonText: '取消'
    })
  } catch {
    return
  }

  applyingPreset.value = true
  try {
    const { data } = await api.post(`/config/ai/presets/${id}/apply`)
    form.value = withExternalDefaults(data)
    baseline.value = JSON.stringify(configPayload())
    presetId.value = ''
    selectedPresetId.value = id
    if (fromManage) managePresetDialog.value = false
    ElMessage.success(`已应用预设「${p.name}」`)
  } catch { /* API 层提示错误，应用失败时保留当前草稿。 */ }
  finally { applyingPreset.value = false }
}

const applySelectedPreset = () => {
  if (selectedPresetId.value) applyPresetById(selectedPresetId.value)
}

const applyPreset = (id) => {
  const p = (form.value.availableProviders || []).find((x) => x.id === id)
  if (!p) return
  form.value.apiBaseUrl = p.apiBaseUrl
  if (p.defaultModel) form.value.modelName = p.defaultModel
}

const refreshCharacters = async () => {
  const { data } = await api.get('/config/characters')
  form.value.availableCharacters = data
}

const openEdit = (row) => {
  editing.value = !!row
  charReadonly.value = !!row && !row.isCustom
  charForm.value = row
    ? { name: row.name, displayName: row.displayName, description: row.description, systemPrompt: row.systemPrompt }
    : { name: '', displayName: '', description: '', systemPrompt: '' }
  editDialog.value = true
}

const saveCharacter = async () => {
  if (!charForm.value.name.trim() || !charForm.value.displayName.trim()) {
    ElMessage.warning('标识与名称不能为空')
    return
  }
  charSaving.value = true
  try {
    if (editing.value) {
      await api.put(`/config/characters/${charForm.value.name}`, charForm.value)
    } else {
      await api.post('/config/characters', charForm.value)
    }
    ElMessage.success('人设已保存')
    editDialog.value = false
    await refreshCharacters()
  } finally { charSaving.value = false }
}

const removeCharacter = async (row) => {
  await api.delete(`/config/characters/${row.name}`)
  ElMessage.success('人设已删除')
  if (form.value.personality === row.name) form.value.personality = ''
  await refreshCharacters()
}

const save = async (successMessage = 'AI 配置已保存') => {
  if (formBusy.value) return false
  if (formError.value) { ElMessage.warning(formError.value); return false }
  saving.value = true
  try {
    await api.put('/config/ai', configPayload())
    ElMessage.success(successMessage)
    await load()
    // 手动保存后当前配置未必还等于选中的预设，清掉标记避免误导。
    selectedPresetId.value = null
    return true
  } catch { return false /* API 层提示错误；保留未保存的草稿。 */ }
  finally { saving.value = false }
}

onMounted(async () => {
  await Promise.all([load(), loadPresets()])
})
</script>

<style scoped>
.ai-settings { min-width: 0; }
.ai-heading, .ai-title, .ai-actions, .ai-preset-toolbar, .ai-preset-note, .ai-save-bar, .ai-save-status { display: flex; align-items: center; flex-wrap: wrap; gap: 10px; }
.ai-heading { justify-content: space-between; align-items: flex-start; gap: 16px; margin-bottom: 22px; }
.ai-title h2 { margin: 0; color: var(--panel-text); font-size: 20px; font-weight: 600; }
.ai-heading p { margin: 6px 0 0; color: var(--panel-text-muted); font-size: 13px; }
.ai-actions { gap: 8px; }
.ai-actions :deep(.el-button + .el-button), .ai-preset-item-actions :deep(.el-button + .el-button) { margin-left: 0; }
.ai-action-icon { margin-left: 6px; }
.ai-preset-toolbar { padding: 16px 18px; background: var(--panel-raised); border-radius: 10px; border: 1px solid var(--panel-border-soft); }
.ai-preset-toolbar label { font-size: 13px; color: var(--panel-text-regular); }
.ai-preset-toolbar :deep(.el-select) { width: 300px; max-width: 100%; }
.ai-preset-note { margin-top: 12px; font-size: 12px; color: var(--panel-text-regular); overflow-wrap: anywhere; }
.ai-preset-footnote { font-size: 12px; color: var(--panel-text-muted); line-height: 1.7; margin: 12px 0 0; }
.ai-preset-card { margin-bottom: 16px; }
.ai-option { display: flex; align-items: center; justify-content: space-between; gap: 16px; min-width: 0; }
.ai-option > span { min-width: 0; overflow: hidden; text-overflow: ellipsis; }
.ai-option small { max-width: 50%; overflow: hidden; text-overflow: ellipsis; color: var(--panel-text-muted); font-size: 12px; font-weight: 400; }
.ai-warning { color: var(--el-color-warning); }
.ai-settings-form { display: flex; flex-direction: column; gap: 16px; }
.ai-primary-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 16px; }
.ai-section { min-width: 0; }
.ai-section-heading { display: flex; align-items: flex-start; gap: 10px; margin-bottom: 24px; }
.ai-section-heading > .el-icon { margin-top: 3px; color: var(--panel-accent-text); font-size: 18px; flex: none; }
.ai-section-heading > div { flex: 1; min-width: 0; }
.ai-section-heading h3 { margin: 0; color: var(--panel-text); font-size: 16px; font-weight: 600; }
.ai-section-heading p { margin: 5px 0 0; color: var(--panel-text-muted); font-size: 12px; line-height: 1.65; }
.ai-section-heading > .el-button { flex: none; padding-top: 2px; }
.ai-fields { display: grid; gap: 22px; }
.ai-context-grid, .ai-search-fields { grid-template-columns: repeat(2, minmax(0, 1fr)); column-gap: 32px; }
.ai-unit { color: var(--panel-text-muted); font-size: 12px; }
.ai-temperature { display: flex; align-items: center; gap: 20px; width: 100%; }
.ai-temperature :deep(.el-slider) { flex: 1; min-width: 0; margin: 0 8px; }
.ai-temperature :deep(.el-input-number) { width: 80px; flex: none; }
.ai-temperature :deep(.el-input-number .el-input__inner) { text-align: center; }
.ai-budget-collapse { border: 0; border-top: 1px solid var(--panel-border-soft); margin-top: 24px; }
.ai-budget-collapse :deep(.el-collapse-item__header) { height: auto; min-height: 56px; border: 0; gap: 10px; background: transparent; font-size: 14px; font-weight: 400; line-height: 1.6; }
.ai-budget-collapse :deep(.el-collapse-item__title) { display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: 4px 12px; flex: 1; min-width: 0; padding: 10px 0; }
.ai-budget-collapse :deep(.el-collapse-item__title small) { font-size: 12px; color: var(--panel-text-muted); }
.ai-budget-collapse :deep(.el-collapse-item__arrow) { margin-left: 0; }
.ai-budget-collapse :deep(.el-collapse-item__wrap) { border: 0; background: transparent; }
.ai-budget-collapse :deep(.el-collapse-item__content) { padding: 10px 0 0; }
.ai-budget-summary { padding: 16px; background: var(--panel-raised); border-radius: 8px; margin-top: 20px; }
.ai-budget-summary > div:first-child { display: flex; justify-content: space-between; flex-wrap: wrap; gap: 4px 12px; font-size: 12px; color: var(--panel-text-regular); margin-bottom: 10px; }
.ai-budget-summary p { margin: 8px 0 0; font-size: 12px; color: var(--panel-text-muted); line-height: 1.7; }
.ai-section-heading > .ai-search-master { display: flex; align-items: center; gap: 10px; color: var(--panel-text-regular); font-size: 13px; flex: none; }
.ai-search-content { display: flex; flex-direction: column; gap: 20px; }
.ai-search-route { max-width: 100%; }
.ai-search-route :deep(.el-radio-button__original-radio:disabled:checked + .el-radio-button__inner) { color: var(--panel-text-muted); }
.ai-search-fields { gap: 20px 32px; }
.ai-search-test { align-self: center; font-size: 12px; color: var(--panel-text-muted); padding: 0; }
.ai-search-test p { margin: 10px 0 4px; line-height: 1.6; }
.ai-search-test small { font-size: 12px; line-height: 1.7; }
.ai-search-content > .ai-budget-collapse { margin-top: 0; }
.ai-builtin-search { display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: 16px; padding: 16px 18px; border: 1px solid var(--panel-border-soft); border-radius: 10px; background: var(--panel-raised); }
.ai-builtin-search h4 { margin: 0; font-size: 14px; font-weight: 500; color: var(--panel-text); }
.ai-builtin-search p { margin: 6px 0 0; color: var(--panel-text-regular); font-size: 12px; line-height: 1.7; overflow-wrap: anywhere; }
.ai-builtin-search small { display: block; margin-top: 4px; font-size: 12px; color: var(--panel-text-muted); }
.ai-search-content > .ai-field { max-width: calc(50% - 16px); }
.ai-save-bar { justify-content: space-between; gap: 14px; padding: 4px 0; }
.ai-save-status { color: var(--panel-text-muted); font-size: 13px; }
.ai-save-status > span { width: 6px; height: 6px; border-radius: 50%; background: var(--el-color-success); }
.ai-save-status > span.is-dirty { background: var(--el-color-warning); }
.ai-form-error, .ai-load-error { margin-bottom: 0; }
.ai-load-error { margin-bottom: 16px; }
.ai-library-intro { color: var(--panel-text-muted); font-size: 13px; line-height: 1.7; margin: 0 0 16px; }
.ai-preset-list { max-height: 60vh; overflow: auto; overscroll-behavior: contain; }
.ai-preset-item { border: 1px solid var(--panel-border); border-radius: 10px; padding: 16px; }
.ai-preset-item + .ai-preset-item { margin-top: 12px; }
.ai-preset-item-title, .ai-preset-details, .ai-preset-item-footer, .ai-preset-item-actions { display: flex; align-items: center; flex-wrap: wrap; gap: 8px; }
.ai-preset-item-title strong { font-size: 14px; color: var(--panel-text); font-weight: 500; overflow-wrap: anywhere; }
.ai-preset-description { font-size: 13px; color: var(--panel-text-regular); line-height: 1.6; margin: 8px 0; overflow-wrap: anywhere; }
.ai-preset-details { gap: 6px 16px; margin-top: 8px; color: var(--panel-text-muted); font-size: 12px; overflow-wrap: anywhere; }
.ai-preset-address { margin: 6px 0 0; overflow-wrap: anywhere; }
.ai-preset-item-footer { justify-content: space-between; margin-top: 14px; gap: 12px; }
.ai-preset-item-footer > small { color: var(--panel-text-muted); font-size: 12px; }
.hint { width: 100%; color: var(--panel-text-muted); font-size: 12px; margin-top: 4px; line-height: 1.6; }
.probe-summary { margin-bottom: 12px; font-size: 13px; line-height: 1.7; }
.probe-desc { color: var(--panel-text-muted); font-size: 12px; }
.probe-footnote { color: var(--panel-text-muted); font-size: 12px; line-height: 1.7; margin-top: 12px; }
.probe-progress { display: flex; flex-direction: column; gap: 8px; }
.probe-progress-item { display: flex; align-items: center; gap: 10px; padding: 8px 12px; border: 1px solid var(--panel-border-soft); border-radius: 8px; }
.probe-progress-main { flex: 1; min-width: 0; }
.probe-progress-main code { font-size: 12px; }
.es-verdict { display: flex; align-items: flex-start; gap: 10px; font-size: 13px; line-height: 1.7; }
.es-hits { display: flex; flex-direction: column; gap: 8px; margin-top: 12px; }
.es-hit { display: block; padding: 8px 12px; border: 1px solid var(--panel-border-soft); border-radius: 8px; text-decoration: none; }
.es-hit:hover { border-color: var(--el-color-primary-light-5); background: var(--panel-raised); }
.es-hit-title { display: block; color: var(--panel-accent-text); font-size: 13px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.es-hit-url { display: block; color: var(--panel-text-muted); font-size: 12px; margin-top: 2px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
@media (max-width: 1100px) and (min-width: 901px), (max-width: 760px) { .ai-primary-grid { grid-template-columns: 1fr; } }
@media (max-width: 600px) {
  .ai-title h2 { font-size: 18px; }
  .ai-heading { margin-bottom: 18px; }
  .ai-heading > .ai-actions { width: 100%; }
  .ai-preset-toolbar { padding: 14px; }
  .ai-preset-toolbar label { width: 100%; }
  .ai-preset-toolbar :deep(.el-select) { width: 100%; }
  .ai-preset-toolbar > .el-button { margin-left: auto; }
  .ai-section-heading { margin-bottom: 20px; flex-wrap: wrap; }
  .ai-search-master { margin-left: 28px; width: calc(100% - 28px); justify-content: space-between; }
  .ai-context-grid, .ai-search-fields { grid-template-columns: 1fr; gap: 20px; }
  .ai-search-content > .ai-field { max-width: 100%; }
  .ai-search-route { display: flex; width: 100%; }
  .ai-search-route :deep(.el-radio-button) { flex: 1; min-width: 0; }
  .ai-search-route :deep(.el-radio-button__inner) { width: 100%; padding: 10px 6px; font-size: 13px; }
  .ai-budget-summary { padding: 12px; }
  .ai-save-bar > .ai-actions { width: 100%; justify-content: flex-end; }
  .ai-preset-item { padding: 12px; }
}
</style>
