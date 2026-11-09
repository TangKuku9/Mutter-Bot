<template>
  <el-card shadow="never" v-loading="loading">
    <template #header>
      <div class="card-head">
        <b>AI 配置</b>
        <el-button size="small" @click="openHelp('configPreset')">参数说明</el-button>
      </div>
    </template>

    <el-form :model="form" label-width="130px" style="max-width: 760px">
      <el-form-item label="配置预设">
        <el-select v-model="selectedPresetId" placeholder="选择已保存的预设" clearable style="width: 260px">
          <el-option v-for="p in presets" :key="p.id" :label="p.name" :value="p.id">
            <span>{{ p.name }}</span>
            <span style="float: right; color: #909399; font-size: 12px">{{ p.modelName || '未填模型' }}</span>
          </el-option>
        </el-select>
        <el-button style="margin-left: 12px" :disabled="!selectedPresetId" :loading="applyingPreset"
          @click="applySelectedPreset()">应用</el-button>
        <el-button style="margin-left: 12px" @click="openSavePreset()">保存为预设</el-button>
        <el-button style="margin-left: 12px" @click="openManagePresets()">管理预设</el-button>
        <ParamHelpButton :label="helpLabel('configPreset')" @click="openHelp('configPreset')" />
        <div class="hint" v-if="selectedPreset">
          {{ selectedPreset.hasApiKey ? '已保存密钥' : '未保存密钥，应用时沿用当前 Key' }}
          <span v-if="selectedPreset.personalityMissing">；人设「{{ selectedPreset.personality }}」已不存在</span>
        </div>
        <div class="hint" v-if="isDirty">当前表单有未保存修改；保存或应用后会重新开始对比。</div>
      </el-form-item>
      <el-form-item label="服务商">
        <el-select v-model="presetId" placeholder="选择服务商自动填充" clearable style="width: 320px" @change="applyPreset">
          <el-option v-for="p in form.availableProviders" :key="p.id" :label="p.name" :value="p.id">
            <span>{{ p.name }}</span>
            <span style="float: right; color: #909399; font-size: 12px">{{ p.defaultModel }}</span>
          </el-option>
        </el-select>
        <ParamHelpButton :label="helpLabel('presetId')" @click="openHelp('presetId')" />
        <div class="hint" v-if="presetHelp">{{ presetHelp }}</div>
      </el-form-item>
      <el-form-item label="API 地址">
        <el-input v-model="form.apiBaseUrl" placeholder="https://api.example.com/v1" />
        <ParamHelpButton :label="helpLabel('apiBaseUrl')" @click="openHelp('apiBaseUrl')" />
      </el-form-item>
      <el-form-item label="API Key">
        <el-input v-model="form.apiKey" type="password" show-password placeholder="留空或保持打码值表示不修改" />
        <ParamHelpButton :label="helpLabel('apiKey')" @click="openHelp('apiKey')" />
      </el-form-item>
      <el-form-item label="模型">
        <el-input v-model="form.modelName" placeholder="模型名" />
        <ParamHelpButton :label="helpLabel('modelName')" @click="openHelp('modelName')" />
      </el-form-item>
      <el-form-item label="人设">
        <el-select v-model="form.personality" clearable placeholder="选择角色人设" style="width: 320px">
          <el-option v-for="c in form.availableCharacters" :key="c.name" :label="c.displayName" :value="c.name">
            <span>{{ c.displayName }}<el-tag v-if="c.isCustom" size="small" type="warning" style="margin-left: 6px">自定义</el-tag></span>
            <span style="float: right; color: #909399; font-size: 12px">{{ c.description }}</span>
          </el-option>
        </el-select>
        <el-button style="margin-left: 12px" @click="manageDialog = true">管理人设</el-button>
        <ParamHelpButton :label="helpLabel('personality')" @click="openHelp('personality')" />
      </el-form-item>
      <el-form-item label="系统提示词">
        <el-input v-model="form.systemPrompt" type="textarea" :rows="5" placeholder="留空则使用内置默认提示词" />
        <ParamHelpButton :label="helpLabel('systemPrompt')" @click="openHelp('systemPrompt')" />
        <div class="hint">
          <span v-if="!form.systemPrompt">当前使用内置默认提示词。</span>
          <el-button size="small" link type="primary" @click="defaultPromptDialog = true">查看内置默认提示词</el-button>
        </div>
      </el-form-item>
      <el-form-item label="Temperature">
        <el-slider v-model="form.temperature" :min="0" :max="2" :step="0.1" show-input style="max-width: 420px" />
        <ParamHelpButton :label="helpLabel('temperature')" @click="openHelp('temperature')" />
      </el-form-item>
      <el-form-item label="回复输出上限">
        <el-input-number v-model="form.maxTokens" :min="64" :max="32768" :step="256" />
        <ParamHelpButton :label="helpLabel('maxTokens')" @click="openHelp('maxTokens')" />
      </el-form-item>
      <el-form-item label="模型上下文容量">
        <el-input-number v-model="form.contextWindowTokens" :min="4096" :max="1048576" :step="4096" />
        <ParamHelpButton :label="helpLabel('contextWindowTokens')" @click="openHelp('contextWindowTokens')" />
      </el-form-item>
      <el-form-item label="输入预算">
        <el-input-number v-model="form.maxInputTokens" :min="512" :max="Math.max(512, budget.availableInput)" :step="512" />
        <ParamHelpButton :label="helpLabel('maxInputTokens')" @click="openHelp('maxInputTokens')" />
      </el-form-item>
      <el-form-item label="容量分配">
        <div style="width: 100%">
          <el-progress :percentage="budget.percent" :status="budget.error ? 'exception' : undefined" />
          <div class="hint">输入 {{ budget.input }} + 输出 {{ budget.output }} + 预留 {{ budget.reserve }} / 容量 {{ budget.context }}</div>
          <el-alert v-if="budget.error" :title="budget.error" type="error" :closable="false" style="margin-top: 6px" />
        </div>
      </el-form-item>
      <el-form-item label="群记忆用于回复">
        <el-switch v-model="form.groupMemoryInjectionEnabled" />
        <ParamHelpButton :label="helpLabel('groupMemoryInjectionEnabled')" @click="openHelp('groupMemoryInjectionEnabled')" />
        <div class="hint">普通 @ 回复参考群管理中维护的记忆；与择机发言的全局开关独立。私聊不使用群记忆。</div>
      </el-form-item>
      <el-form-item label="上下文条数">
        <el-input-number v-model="form.maxContextMessages" :min="2" :max="100" />
        <ParamHelpButton :label="helpLabel('maxContextMessages')" @click="openHelp('maxContextMessages')" />
      </el-form-item>
      <el-form-item label="附图张数">
        <el-input-number v-model="form.maxAttachedImages" :min="0" :max="form.ceilingAttachedImages || 10" />
        <ParamHelpButton :label="helpLabel('maxAttachedImages')" @click="openHelp('maxAttachedImages')" />
      </el-form-item>
      <el-form-item label="联网搜索">
        <el-switch v-model="form.webSearchEnabled" />
        <ParamHelpButton :label="helpLabel('webSearchEnabled')" @click="openHelp('webSearchEnabled')" />
        <el-button size="small" style="margin-left: 12px" :loading="probing" :disabled="probing" @click="probeWebSearch">
          检测联网搜索
        </el-button>
        <div class="hint" v-if="form.webSearchShape">
          当前策略：{{ shapeLabel(form.webSearchShape) }}（文字已验证<span v-if="form.webSearchImageCapable === true">，带图已验证</span><span v-else-if="form.webSearchImageCapable === false">，带图不支持</span><span v-else>，带图未测</span>）
        </div>
        <div class="hint" v-else-if="form.webSearchEnabled">
          尚未选用实测策略，运行时按 API 地址自动推断——中转可能因此不生效。
        </div>
        <div class="hint" v-if="form.webSearchEnabled">
          带图时：
          <el-select v-model="form.webSearchImageMode" size="small" style="width: 150px">
            <el-option label="跟随能力" value="auto" />
            <el-option label="确保联网" value="two_stage" />
            <el-option label="关闭" value="off" />
          </el-select>
          <ParamHelpButton :label="helpLabel('webSearchImageMode')" @click="openHelp('webSearchImageMode')" />
        </div>
      </el-form-item>
      <el-form-item label="独立搜索服务">
        <div class="es-box">
          <div class="es-head">
            <div class="es-head-main">
              <span class="es-title">独立搜索服务<el-tag size="small" type="success" effect="plain" style="margin-left: 6px">推荐</el-tag></span>
              <span class="es-sub">
                由本程序先检索、再把结果作为资料注入对话（预检索注入），不依赖模型服务商支持搜索——
                适合中转站与不带内建搜索的模型。与上方「联网搜索」（模型服务商内建通道）相互独立；
                总开关仍是上方「联网搜索」，两者同时开启才会生效。
              </span>
            </div>
            <el-switch v-model="form.externalSearch.enabled" aria-label="启用独立搜索服务" />
            <ParamHelpButton :label="helpLabel('externalSearchEnabled')" @click="openHelp('externalSearchEnabled')" />
          </div>
          <el-alert v-if="form.externalSearch.enabled && !form.webSearchEnabled" type="warning" :closable="false"
            show-icon style="margin-top: 8px"
            title="独立搜索服务已开启，但上方「联网搜索」总开关未开启；两者同时开启才会真正检索。" />
          <div class="es-field">
            <span class="es-label">搜索服务商</span>
            <el-select v-model="form.externalSearch.provider" style="width: 240px">
              <el-option v-for="p in ES_PROVIDERS" :key="p.id" :label="p.name" :value="p.id" />
            </el-select>
            <ParamHelpButton :label="helpLabel('externalSearchProvider')" @click="openHelp('externalSearchProvider')" />
          </div>
          <div class="es-hint-inset hint" v-if="esProviderHint">{{ esProviderHint }}</div>
          <div class="es-field">
            <span class="es-label">服务地址</span>
            <el-input v-model="form.externalSearch.baseUrl" :placeholder="esBaseUrlPlaceholder" style="width: 380px" />
            <ParamHelpButton :label="helpLabel('externalSearchBaseUrl')" @click="openHelp('externalSearchBaseUrl')" />
          </div>
          <div class="es-field">
            <span class="es-label">API Key</span>
            <el-input v-model="form.externalSearch.apiKey" type="password" show-password
              :disabled="form.externalSearch.provider === 'searxng'"
              :placeholder="form.externalSearch.provider === 'searxng' ? 'SearXNG 免 Key' : '留空或保持打码值表示不修改'"
              style="width: 380px" />
            <ParamHelpButton :label="helpLabel('externalSearchApiKey')" @click="openHelp('externalSearchApiKey')" />
          </div>
          <div class="es-hint-inset hint" v-if="form.externalSearch.provider !== 'searxng'">
            只存在本机数据库，页面不回显明文；填新值会直接覆盖。
          </div>
          <div class="es-field">
            <span class="es-label">检索参数</span>
            <span class="es-inline-label">结果条数</span>
            <el-input-number v-model="form.externalSearch.maxResults" :min="1" :max="10" />
            <span class="es-inline-label" style="margin-left: 16px">超时</span>
            <el-input-number v-model="form.externalSearch.timeoutSeconds" :min="1" :max="30" />
            <span class="es-inline-label">秒</span>
            <ParamHelpButton :label="helpLabel('externalSearchLimits')" @click="openHelp('externalSearchLimits')" />
          </div>
          <div class="es-field">
            <span class="es-label">失败兜底</span>
            <el-switch v-model="form.externalSearch.fallbackToBuiltin" aria-label="外搜失败时回退内建搜索" />
            <span class="es-inline-label">外搜失败时，回退到服务商内建联网通道</span>
            <ParamHelpButton :label="helpLabel('externalSearchFallback')" @click="openHelp('externalSearchFallback')" />
          </div>
          <div class="es-hint-inset hint">默认关闭；兜底还需要上方「联网搜索」可用，开启后失败会多一次内建检索，回复可能更慢。0 条结果视为无资料，不会再检索。</div>
          <div class="es-foot">
            <el-button size="small" :loading="esProbing" :disabled="esProbing" @click="probeExternalSearch">
              测试搜索
            </el-button>
            <ParamHelpButton :label="helpLabel('externalSearchTest')" @click="openHelp('externalSearchTest')" />
            <span class="hint" style="margin-top: 0">
              最近验证：{{ form.externalSearchStatus.verifiedAt ? fmt(form.externalSearchStatus.verifiedAt) : '尚未验证' }}
            </span>
          </div>
        </div>
      </el-form-item>
      <el-form-item>
        <el-button type="primary" :loading="saving" :disabled="!!budget.error" @click="save()">保存配置</el-button>
        <el-button @click="reloadConfig">重新加载</el-button>
      </el-form-item>
    </el-form>

    <!-- 人设管理 -->
    <el-dialog v-model="manageDialog" title="人设管理" width="720px">
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
            <el-popconfirm v-if="row.isCustom" title="删除后引用该人设的配置将回退为默认，确认删除？" @confirm="removeCharacter(row)">
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
    <el-dialog v-model="editDialog" :title="editTitle" width="560px">
      <el-form :model="charForm" label-width="100px" v-if="charForm">
        <el-form-item label="标识" required>
          <el-input v-model="charForm.name" :disabled="charReadonly || editing" placeholder="英文标识，如 waifu，保存后不可改" maxlength="32" />
        </el-form-item>
        <el-form-item label="名称" required>
          <el-input v-model="charForm.displayName" :disabled="charReadonly" placeholder="显示名称，如 温柔学姐" />
        </el-form-item>
        <el-form-item label="描述">
          <el-input v-model="charForm.description" :disabled="charReadonly" placeholder="一句话简介，供列表展示" />
        </el-form-item>
        <el-form-item label="角色提示词">
          <el-input v-model="charForm.systemPrompt" :disabled="charReadonly" type="textarea" :rows="6" placeholder="设定角色身份、语气与行为规则，选中该人设后追加到系统提示词" />
        </el-form-item>
      </el-form>
      <template #footer v-if="!charReadonly">
        <el-button @click="editDialog = false">取消</el-button>
        <el-button type="primary" :loading="charSaving" @click="saveCharacter">保存</el-button>
      </template>
    </el-dialog>

    <!-- 保存为预设 -->
    <el-dialog v-model="savePresetDialog" title="保存为预设" width="520px">
      <el-form :model="presetForm" label-width="96px">
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
    <el-dialog v-model="editPresetDialog" title="编辑预设" width="520px">
      <el-form :model="presetEditForm" label-width="96px">
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
    <el-dialog v-model="managePresetDialog" title="配置预设管理" width="1000px">
      <el-table :data="presets" size="small" empty-text="还没有保存过配置预设">
        <el-table-column label="名称" min-width="160">
          <template #default="{ row }">
            <div>
              {{ row.name }}
              <el-tag v-if="!row.isValid" size="small" type="danger">数据损坏</el-tag>
            </div>
            <div v-if="row.description" class="probe-desc">{{ row.description }}</div>
            <div class="probe-desc">更新于 {{ fmt(row.updatedAt) }}</div>
          </template>
        </el-table-column>
        <el-table-column label="服务商 / 模型" min-width="200">
          <template #default="{ row }">
            <div>{{ row.modelName || '未填模型' }}</div>
            <div class="probe-desc">{{ row.apiBaseUrl || '未填地址' }}</div>
          </template>
        </el-table-column>
        <el-table-column label="人设" width="100">
          <template #default="{ row }">
            {{ row.personalityDisplayName || '未指定' }}
            <el-tag v-if="row.personalityMissing" size="small" type="warning">已失效</el-tag>
          </template>
        </el-table-column>
        <el-table-column label="联网" width="100">
          <template #default="{ row }">
            <el-tag size="small" :type="row.webSearchEnabled ? 'success' : 'info'">
              {{ row.webSearchEnabled ? '开启' : '关闭' }}
            </el-tag>
            <div v-if="row.webSearchEnabled" class="probe-desc">
              {{ row.webSearchShape ? shapeShortLabel(row.webSearchShape) : '自动推断' }}
            </div>
          </template>
        </el-table-column>
        <el-table-column label="密钥" width="80">
          <template #default="{ row }">
            <el-tag size="small" :type="row.hasApiKey ? 'success' : 'info'">
              {{ row.hasApiKey ? '已保存' : '未保存' }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column label="操作" width="300" fixed="right">
          <template #default="{ row }">
            <el-button size="small" link type="primary" :disabled="!row.isValid"
              @click="applyPresetById(row.id, true)">应用</el-button>
            <el-button size="small" link type="primary" @click="openPresetMeta(row)">重命名</el-button>
            <el-button size="small" link type="warning" @click="overwritePreset(row)">覆盖</el-button>
            <el-button size="small" link type="primary" @click="duplicatePreset(row)">复制</el-button>
            <el-popconfirm title="删除后不影响当前生效配置，确认删除？" @confirm="removePreset(row)">
              <template #reference>
                <el-button size="small" link type="danger">删除</el-button>
              </template>
            </el-popconfirm>
          </template>
        </el-table-column>
      </el-table>
      <div class="probe-footnote">
        「覆盖」会用当前表单内容替换该预设的配置快照；「应用」会立即覆盖并保存当前生效配置。
      </div>
      <template #footer>
        <el-button type="primary" @click="openSavePreset(true)">从当前表单新建</el-button>
        <el-button @click="managePresetDialog = false">关闭</el-button>
      </template>
    </el-dialog>

    <!-- 内置默认系统提示词 -->
    <el-dialog v-model="defaultPromptDialog" title="内置默认系统提示词" width="760px">
      <el-input :model-value="form.defaultSystemPrompt || '未获取到默认提示词'" type="textarea" :rows="14" readonly />
      <div class="probe-footnote">
        系统提示词留空时使用这段内容；选中「人设」后，角色提示词会在此基础上追加。
      </div>
      <template #footer>
        <el-button @click="defaultPromptDialog = false">关闭</el-button>
      </template>
    </el-dialog>

    <!-- 联网搜索探测：流式逐项进度 → 完整报告 → 每个已验证形状可一键选用 -->
    <el-dialog v-model="probeDialog" title="联网搜索探测报告" width="760px" @close="cancelProbe">
      <el-alert v-if="probeReport && !probeReport.reachable" type="error" :closable="false" show-icon
        :title="probeReport.error || '上游不可达'" style="margin-bottom: 12px" />
      <el-alert v-else-if="probeReport && probeReport.authFailed" type="error" :closable="false" show-icon
        :title="probeReport.error || '鉴权失败'" style="margin-bottom: 12px" />

      <template v-else-if="probePhase === 'running'">
        <div class="probe-summary">正在逐个形状实测当前填写的地址与模型，请稍候……</div>
        <div class="probe-progress">
          <div v-if="probeProgress.length === 0" class="probe-progress-item">
            <el-icon class="is-loading"><Loading /></el-icon>
            <div class="probe-progress-main">正在连接上游…</div>
          </div>
          <div v-for="item in probeProgress" :key="item.shape" class="probe-progress-item">
            <el-icon v-if="!item.done" class="is-loading"><Loading /></el-icon>
            <el-icon v-else :color="item.status === 'verified' ? '#67c23a' : '#909399'"><CircleCheck /></el-icon>
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
          <b>{{ verifiedCount }}</b> 种形状已验证、{{ ignoredCount }} 种被静默忽略。
          对已验证的形状点右侧「选用」，即时写进配置并打开联网搜索。
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
              <el-button v-if="form.webSearchShape === row.shape" size="small" type="success" plain disabled>
                已选用
              </el-button>
              <el-tooltip v-else :disabled="row.status === 'verified'" content="该形状未通过实测，不能选用" placement="top">
                <span>
                  <el-button size="small" type="primary" :disabled="row.status !== 'verified'"
                    @click="applyProbe(row)">选用</el-button>
                </span>
              </el-tooltip>
            </template>
          </el-table-column>
        </el-table>
        <div class="probe-footnote">
          「已验证」= 响应携带检索证据字段，或带搜索比基线多出大量 prompt token（搜索结果被注入），
          或回答给出了与当前北京时间吻合的时刻而基线给不出（部分中转会抹掉前两种证据）。
          「被静默忽略」= 请求 2xx 但无任何检索证据——这正是开关打开却毫无效果的情形。
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
    <el-dialog v-model="esProbeDialog" title="测试搜索 · 独立搜索服务" width="640px" @close="cancelEsProbe">
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
          <a v-for="h in esProbeResult.hits" :key="h.url" class="es-hit" :href="h.url" target="_blank"
            rel="noopener noreferrer">
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

    <ParamHelpDrawer v-model="helpVisible" :title="helpTitle" :lines="helpLines" :current="helpCurrent"
      :index="helpIndex" :total="helpTotal" @step="stepHelp" />
  </el-card>
</template>

<script setup>
import { ref, computed, onMounted, onBeforeUnmount } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Loading, CircleCheck } from '@element-plus/icons-vue'
import api from '../api'
import { tokenBudget, DEFAULT_INPUT_BUDGET } from '../tokenBudget'
import { useUnsavedChanges } from '../useUnsavedChanges'
import { useParamHelp } from '../paramHelp'
import ParamHelpButton from '../components/ParamHelpButton.vue'
import ParamHelpDrawer from '../components/ParamHelpDrawer.vue'

// 独立搜索服务：与后端 ExternalSearchInputDto 对齐的默认值；响应缺字段时兜底，默认不启用
const defaultExternalSearch = () => ({
  enabled: false,
  provider: 'searxng',
  baseUrl: '',
  apiKey: '',
  maxResults: 5,
  timeoutSeconds: 8,
  fallbackToBuiltin: false
})
const withExternalDefaults = (data) => ({
  ...data,
  externalSearch: { ...defaultExternalSearch(), ...(data?.externalSearch || {}) },
  externalSearchStatus: data?.externalSearchStatus || { verifiedAt: null }
})

const form = ref(withExternalDefaults({ availableProviders: [], availableCharacters: [] }))
const presetId = ref('')
const loading = ref(false)
const saving = ref(false)

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
// 上次读取/应用/保存后的表单快照，用来判断有没有未保存修改
const baseline = ref('')

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
  { id: 'brave', name: 'Brave Search' }
]
const esProviderName = (id) => ES_PROVIDERS.find((p) => p.id === id)?.name || id
const esBaseUrlPlaceholder = computed(() => ({
  searxng: 'https://searx.example.com（实例根地址）',
  tavily: '留空 = 官方端点 https://api.tavily.com',
  bocha: '留空 = 官方端点 https://api.bochaai.com/v1/web-search',
  brave: '留空 = 官方端点 https://api.search.brave.com/res/v1/web/search'
}[form.value.externalSearch?.provider] || ''))
const esProviderHint = computed(() => ({
  searxng: '免 Key。填写实例根地址即可；实例需在 settings.yml 的 search.formats 中启用 json 输出。',
  tavily: 'Key 必填；服务地址留空使用官方端点，也可指向自建网关。',
  bocha: 'Key 必填；服务地址留空使用官方端点。',
  brave: 'Key 必填；服务地址留空使用官方端点。'
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
  // 与后端校验同判据的前置检查：SearXNG 必须有实例地址，其余必须有 Key（打码值视为已配置）
  const missing = es.provider === 'searxng'
    ? (!es.baseUrl.trim() ? 'SearXNG 需要填写实例地址' : '')
    : (!es.apiKey.trim() ? `${esProviderName(es.provider)} 需要填写 API Key` : '')
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
  'configPreset', 'presetId', 'apiBaseUrl', 'apiKey', 'modelName', 'personality', 'systemPrompt',
  'temperature', 'maxTokens', 'contextWindowTokens', 'maxInputTokens', 'groupMemoryInjectionEnabled', 'maxContextMessages', 'maxAttachedImages',
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
      '先点「检测联网搜索」，在报告里对已验证的形状点「选用」，运行时会按选用的通道发请求。',
      '没有选用时按 API 地址自动推断，中转可能因此不生效。',
      '只在服务商支持时生效，会更慢也更费 token。'
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
      '适合中转站与不带内建搜索的模型；与上方「联网搜索」（服务商内建通道）相互独立。',
      '总开关仍是「联网搜索」：两者同时开启才会真正联网检索。',
      '配置默认关闭，保存后才会生效。'
    ]
  },
  externalSearchProvider: {
    title: '搜索服务商',
    value: (f) => ({ searxng: 'SearXNG', tavily: 'Tavily', bocha: '博查 Bocha', brave: 'Brave Search' }[f.externalSearch?.provider] || '未选择'),
    lines: [
      'SearXNG：自建元搜索引擎，免 Key，需一个开了 JSON format 的实例。',
      'Tavily / 博查 / Brave：商用搜索 API，Key 必填，服务地址可留空用官方端点。',
      '换服务商后需重新「测试搜索」获得新的验证时间。'
    ]
  },
  externalSearchBaseUrl: {
    title: '服务地址',
    value: (f) => f.externalSearch?.baseUrl || '未填写',
    lines: [
      'SearXNG 必填：实例根地址（不含 /search 路径），例如 https://searx.example.com。',
      '实例需在 settings.yml 的 search.formats 中加上 json，否则测试会提示未开启 JSON 输出。',
      '其余服务商留空即用官方默认端点；填了则指向自建网关，必须是 http/https 开头的完整 URL。'
    ]
  },
  externalSearchApiKey: {
    title: '搜索服务 API Key',
    value: (f) => (f.externalSearch?.provider === 'searxng' ? '免 Key' : (f.externalSearch?.apiKey ? '已配置' : '未配置')),
    lines: [
      'SearXNG 免 Key，输入框禁用。',
      '其余服务商必填；只存在本机数据库，页面不回显明文。',
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
  } finally { loading.value = false }
}

const loadPresets = async () => {
  const { data } = await api.get('/config/ai/presets')
  presets.value = data
}

const openSavePreset = (fromManage = false) => {
  if (fromManage) managePresetDialog.value = false
  presetForm.value = { name: '', description: '', includeApiKey: true }
  savePresetDialog.value = true
}

const savePreset = async () => {
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
  } finally { presetSaving.value = false }
}

const openManagePresets = async () => {
  await loadPresets()
  managePresetDialog.value = true
}

const openPresetMeta = (row) => {
  presetEditForm.value = { id: row.id, name: row.name, description: row.description }
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
  } finally { presetSaving.value = false }
}

const overwritePreset = async (row) => {
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
  } finally { presetSaving.value = false }
}

const duplicatePreset = async (row) => {
  const { data } = await api.post(`/config/ai/presets/${row.id}/duplicate`)
  await loadPresets()
  ElMessage.success(`已复制为「${data?.name || '副本'}」`)
}

const removePreset = async (row) => {
  await api.delete(`/config/ai/presets/${row.id}`)
  if (selectedPresetId.value === row.id) selectedPresetId.value = null
  await loadPresets()
  ElMessage.success('预设已删除')
}

const applyPresetById = async (id, fromManage = false) => {
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
      type: 'warning', confirmButtonText: '应用', cancelButtonText: '取消'
    })
  } catch {
    return
  }

  applyingPreset.value = true
  try {
    const { data } = await api.post(`/config/ai/presets/${id}/apply`)
    form.value = withExternalDefaults(data)
    baseline.value = JSON.stringify(configPayload())
    selectedPresetId.value = id
    if (fromManage) managePresetDialog.value = false
    ElMessage.success(`已应用预设「${p.name}」`)
  } finally { applyingPreset.value = false }
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
  if (budget.value.error) { ElMessage.warning(budget.value.error); return false }
  saving.value = true
  try {
    await api.put('/config/ai', configPayload())
    ElMessage.success(successMessage)
    await load()
    // 手动保存后当前配置未必还等于选中的预设，清掉标记避免误导。
    selectedPresetId.value = null
  } finally { saving.value = false }
}

onMounted(async () => {
  await Promise.all([load(), loadPresets()])
})
</script>

<style scoped>
.card-head { display: flex; justify-content: space-between; align-items: center; }
/* 预设提示是选中后才知道的结果，留在原地更醒目；width 100% 保证它独占一行 */
.hint { width: 100%; color: #909399; font-size: 12px; margin-top: 4px; }
/* 整行控件给右边的「?」留出位置，否则宽度占满会把按钮挤到下一行 */
.el-form-item__content > .el-input,
.el-form-item__content > .el-textarea { width: calc(100% - 32px); }
.probe-summary { margin-bottom: 12px; font-size: 13px; line-height: 1.6; }
.probe-desc { color: #909399; font-size: 12px; }
.probe-footnote { color: #909399; font-size: 12px; line-height: 1.6; margin-top: 10px; }
.probe-progress { display: flex; flex-direction: column; gap: 8px; }
.probe-progress-item {
  display: flex; align-items: center; gap: 10px;
  padding: 8px 12px; border: 1px solid var(--el-border-color-lighter); border-radius: 6px;
}
.probe-progress-main { flex: 1; min-width: 0; }
.probe-progress-main code { font-size: 12px; }
/* 独立搜索服务卡片：浅色内嵌块，与服务商内建通道（上方「联网搜索」）区分 */
.es-box {
  width: 100%; border: 1px solid var(--el-border-color-lighter); border-radius: 6px;
  padding: 12px 16px 10px; background: var(--el-fill-color-extra-light);
}
.es-head { display: flex; align-items: flex-start; gap: 12px; }
.es-head-main { flex: 1; min-width: 0; }
.es-title { font-weight: 600; font-size: 13px; color: var(--el-text-color-primary); }
.es-sub { display: block; color: #909399; font-size: 12px; line-height: 1.6; margin-top: 4px; }
.es-field { display: flex; align-items: center; gap: 8px; margin-top: 10px; flex-wrap: wrap; }
.es-label { flex: none; width: 84px; text-align: right; color: var(--el-text-color-regular); font-size: 13px; }
.es-inline-label { color: var(--el-text-color-regular); font-size: 13px; }
.es-hint-inset { padding-left: 92px; }
.es-foot { display: flex; align-items: center; gap: 8px; margin-top: 12px; }
/* 测试结果：判定行 + 可点击结果列表 */
.es-verdict { display: flex; align-items: flex-start; gap: 10px; font-size: 13px; line-height: 1.6; }
.es-hits { display: flex; flex-direction: column; gap: 8px; margin-top: 12px; }
.es-hit { display: block; padding: 8px 12px; border: 1px solid var(--el-border-color-lighter); border-radius: 6px; text-decoration: none; }
.es-hit:hover { border-color: var(--el-color-primary-light-5); background: var(--el-fill-color-light); }
.es-hit-title {
  display: block; color: var(--el-color-primary); font-size: 13px;
  overflow: hidden; text-overflow: ellipsis; white-space: nowrap;
}
.es-hit-url { display: block; color: #909399; font-size: 12px; margin-top: 2px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
</style>
