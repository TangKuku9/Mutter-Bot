<template>
  <div v-loading="loading">
    <el-card shadow="never">
      <div class="toolbar">
        <span>统计范围：</span>
        <el-radio-group v-model="days" @change="load">
          <el-radio-button :value="1">1 天</el-radio-button>
          <el-radio-button :value="7">7 天</el-radio-button>
          <el-radio-button :value="30">30 天</el-radio-button>
          <el-radio-button :value="90">90 天</el-radio-button>
        </el-radio-group>
      </div>
    </el-card>

    <el-card shadow="never" style="margin-top: 16px">
      <el-descriptions :column="3" border>
        <el-descriptions-item label="实际模型请求">{{ summary.callCount ?? 0 }}</el-descriptions-item>
        <el-descriptions-item label="失败 / 取消">{{ summary.failedCount ?? 0 }}</el-descriptions-item>
        <el-descriptions-item label="未返回完整用量">{{ summary.unknownUsageCount ?? 0 }}</el-descriptions-item>
      </el-descriptions>
      <p class="stat-note">用量为上游已返回的 Token；未返回的部分不按零费用推算。包含主动评估、搜索、重试、决定不发言的调用和管理员手动探测。</p>
      <p v-if="summary.legacyCount" class="stat-note">另有 {{ summary.legacyCount }} 条历史回复用量已保留；旧记录无法还原实际请求次数和使用者，不计入实际请求数与用户排行。</p>
    </el-card>
    <el-row :gutter="16" style="margin-top: 16px">
      <el-col :span="12">
        <el-card shadow="never">
          <template #header><b>每日 Token 用量</b></template>
          <div ref="trendEl" class="trend-chart"></div>
        </el-card>
      </el-col>
      <el-col :span="12">
        <el-card shadow="never">
          <template #header><b>群 Token 排行（Top {{ groups.length }}）</b></template>
          <el-table :data="groups" size="small" max-height="420">
            <el-table-column label="群" show-overflow-tooltip>
              <template #default="{ row }">{{ row.groupName || row.groupId }}</template>
            </el-table-column>
            <el-table-column prop="callCount" label="实际请求" width="90" />
            <el-table-column label="Token" width="120">
              <template #default="{ row }">{{ (row.totalTokens ?? 0).toLocaleString() }}</template>
            </el-table-column>
          </el-table>
        </el-card>
      </el-col>
    </el-row>

    <el-card shadow="never" style="margin-top: 16px">
      <template #header><b>提问用户 Token 排行（Top {{ users.length }}）</b></template>
      <el-table :data="users" size="small">
        <el-table-column prop="userName" label="用户" width="180" />
        <el-table-column prop="userId" label="OpenId" show-overflow-tooltip />
        <el-table-column prop="callCount" label="实际请求" width="90" />
        <el-table-column label="Token" width="120">
          <template #default="{ row }">{{ (row.totalTokens ?? 0).toLocaleString() }}</template>
        </el-table-column>
      </el-table>
    </el-card>
  </div>
</template>

<script setup>
import { ref, watch, onMounted, onBeforeUnmount } from 'vue'
import * as echarts from 'echarts/core'
import { LineChart } from 'echarts/charts'
import { GridComponent, TooltipComponent, LegendComponent, GraphicComponent } from 'echarts/components'
import { CanvasRenderer } from 'echarts/renderers'
import api from '../api'
import { LatestRequest } from '../latestRequest'
import { panelSettings } from '../panelSettings'

echarts.use([LineChart, GridComponent, TooltipComponent, LegendComponent, GraphicComponent, CanvasRenderer])

const days = ref(7)
const daily = ref([])
const groups = ref([])
const users = ref([])
const summary = ref({})
const loading = ref(false)
const request = new LatestRequest()
const trendEl = ref(null)

let chart = null
let resizeObserver = null

const formatTokens = (value) => {
  if (value >= 1000000) return `${(value / 1000000).toFixed(1)}M`
  if (value >= 1000) return `${Math.round(value / 1000)}k`
  return String(value)
}

// 补齐区间内的每一天：后端只返回有调用的日期，直接连线会把空档画成平滑趋势
const enumerateDates = (start, end) => {
  const dates = []
  const cursor = new Date(`${start}T00:00:00Z`)
  const last = new Date(`${end}T00:00:00Z`)
  while (cursor <= last) {
    dates.push(cursor.toISOString().slice(0, 10))
    cursor.setUTCDate(cursor.getUTCDate() + 1)
  }
  return dates
}

const buildOption = () => {
  const dark = panelSettings.darkMode
  const axisColor = dark ? '#a3a6ad' : '#909399'
  const splitColor = dark ? '#363637' : '#ebeef5'
  const rows = daily.value
  const byDate = new Map(rows.map((row) => [row.date, row]))
  const dates = rows.length ? enumerateDates(rows[0].date, rows[rows.length - 1].date) : []
  const showSymbol = dates.length <= 31

  return {
    backgroundColor: 'transparent',
    color: [panelSettings.primaryColor, '#e6a23c'],
    animationDuration: 300,
    tooltip: {
      trigger: 'axis',
      backgroundColor: dark ? '#1d1e1f' : '#ffffff',
      borderColor: dark ? '#4c4d4f' : '#e4e7ed',
      textStyle: { color: dark ? '#e5eaf3' : '#303133', fontSize: 12 },
      axisPointer: { type: 'line', lineStyle: { color: axisColor, type: 'dashed' } },
      formatter: (params) => {
        const date = dates[params[0]?.dataIndex]
        const row = byDate.get(date)
        if (!row) return `<div style="font-weight:600">${date}</div><div>无数据</div>`
        return [
          `<div style="font-weight:600;margin-bottom:4px">${row.date}</div>`,
          `<div style="display:flex;justify-content:space-between;gap:20px"><span>Token</span><b>${(row.totalTokens ?? 0).toLocaleString()}</b></div>`,
          `<div style="display:flex;justify-content:space-between;gap:20px"><span>调用次数</span><b>${row.callCount ?? 0}</b></div>`
        ].join('')
      }
    },
    legend: {
      top: 0,
      right: 0,
      icon: 'roundRect',
      itemWidth: 12,
      itemHeight: 6,
      textStyle: { color: axisColor, fontSize: 12 },
      data: ['Token', '调用次数']
    },
    grid: { top: 36, right: 8, bottom: 4, left: 8, containLabel: true },
    xAxis: {
      type: 'category',
      data: dates.map((date) => date.slice(5)),
      axisLine: { lineStyle: { color: splitColor } },
      axisTick: { show: false },
      axisLabel: { color: axisColor, fontSize: 11, hideOverlap: true }
    },
    yAxis: [
      {
        type: 'value',
        axisLabel: { color: axisColor, fontSize: 11, formatter: formatTokens },
        splitLine: { lineStyle: { color: splitColor } }
      },
      {
        type: 'value',
        axisLabel: { color: axisColor, fontSize: 11 },
        splitLine: { show: false }
      }
    ],
    series: [
      {
        name: 'Token',
        type: 'line',
        data: dates.map((date) => byDate.get(date)?.totalTokens ?? null),
        showSymbol,
        symbol: 'circle',
        symbolSize: 6,
        lineStyle: { width: 2 },
        areaStyle: { opacity: 0.1 },
        emphasis: { focus: 'series' }
      },
      {
        name: '调用次数',
        type: 'line',
        yAxisIndex: 1,
        data: dates.map((date) => byDate.get(date)?.callCount ?? null),
        showSymbol,
        symbol: 'circle',
        symbolSize: 5,
        lineStyle: { width: 1.5, type: 'dashed' },
        emphasis: { focus: 'series' }
      }
    ],
    graphic: rows.length
      ? []
      : [{
          type: 'text',
          left: 'center',
          top: 'middle',
          style: { text: '暂无数据', fill: axisColor, fontSize: 13 }
        }]
  }
}

const renderChart = () => {
  if (chart) chart.setOption(buildOption(), true)
}

const load = async () => {
  loading.value = true
  const range = days.value
  try {
    await request.run((signal) => api.get('/chat/usage', { params: { days: range }, signal }), ({ data }) => {
      daily.value = data.daily || []
      groups.value = (data.groups || []).slice(0, 10)
      users.value = (data.users || []).slice(0, 10)
      summary.value = data.summary || {}
    })
  } catch { /* 统一错误提示 */ }
  finally { if (!request.pending) loading.value = false }
}

onMounted(() => {
  chart = echarts.init(trendEl.value)
  renderChart()
  resizeObserver = new ResizeObserver(() => chart?.resize())
  resizeObserver.observe(trendEl.value)
  load().catch(() => {})
})

watch([daily, () => panelSettings.primaryColor, () => panelSettings.darkMode], renderChart, { deep: true })

onBeforeUnmount(() => {
  request.cancel()
  resizeObserver?.disconnect()
  chart?.dispose()
  chart = null
})
</script>

<style scoped>
.stat-note { color: var(--el-text-color-secondary); font-size: 13px; line-height: 1.6; }
.toolbar { display: flex; align-items: center; gap: 10px; }
.trend-chart { width: 100%; height: 420px; }
</style>
