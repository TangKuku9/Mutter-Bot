#!/usr/bin/env node
/**
 * 对 npm audit 报告执行「默认拒绝」门禁，只放行显式登记、且尚无修复版本的通告。
 * 每条放行都带复核截止日期：到期后自动重新拦截，避免例外被遗忘。
 *
 * 用法：npm audit --json > ../npm-audit.json || true
 *       node tools/check_npm_audit.mjs npm-audit.json
 */
import { readFileSync } from 'node:fs'

const ALLOWED = [
  {
    id: 'GHSA-fc5q-39xc-vjpf',
    until: '2027-02-28',
    reason: 'picomatch ReDoS 暂无修复版本，仅构建工具链可达'
  }
]

const reportPath = process.argv[2]
if (!reportPath) {
  console.error('用法: node tools/check_npm_audit.mjs <npm-audit.json>')
  process.exit(2)
}

let report
try {
  report = JSON.parse(readFileSync(reportPath, 'utf8'))
} catch (error) {
  console.error(`npm audit 报告无法解析（网络或 npm 故障按失败处理）: ${error.message}`)
  process.exit(1)
}

if (report.error) {
  console.error(`npm audit 执行失败: ${JSON.stringify(report.error)}`)
  process.exit(1)
}
if (report.vulnerabilities === null || typeof report.vulnerabilities !== 'object') {
  console.error('无法识别的 npm audit 报告：缺少 vulnerabilities')
  process.exit(1)
}

const advisoryId = url => /GHSA-[a-z0-9-]+/i.exec(url ?? '')?.[0]
const today = new Date().toISOString().slice(0, 10)
const allowList = new Map(ALLOWED.map(entry => [entry.id, entry]))
const checked = new Set()
const blocked = []
let allowedCount = 0

for (const [packageName, vulnerability] of Object.entries(report.vulnerabilities)) {
  for (const via of vulnerability.via ?? []) {
    // 字符串项是传递依赖链；具体通告会在被依赖包自己的 via 中报告。
    if (typeof via === 'string') continue
    const id = advisoryId(via.url)
    if (!id) {
      blocked.push(`${packageName}: ${via.title ?? via.url ?? '未知通告'}`)
      continue
    }
    if (checked.has(id)) continue
    checked.add(id)

    const entry = allowList.get(id)
    if (!entry) {
      blocked.push(`${packageName}: ${id} ${via.title ?? ''}`.trim())
    } else if (today > entry.until) {
      blocked.push(`${packageName}: ${id} 放行已过期（${entry.until}），请复核：${entry.reason}`)
    } else {
      allowedCount++
      console.warn(`已放行 ${id}: ${entry.reason}（复核截止 ${entry.until}）`)
    }
  }
}

if (blocked.length > 0) {
  for (const line of blocked) console.error(`拦截: ${line}`)
  process.exit(1)
}
console.log(`npm audit: 未发现未放行的依赖漏洞（显式放行 ${allowedCount} 项）`)
