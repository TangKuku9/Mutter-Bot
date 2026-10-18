import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

// 版本号唯一来源是仓库根目录的 Directory.Build.props，避免前端与程序集各写一份
const buildProps = readFileSync(
  fileURLToPath(new URL('../Directory.Build.props', import.meta.url)),
  'utf8'
)
const appVersion = buildProps.match(/<Version>([^<]+)<\/Version>/)?.[1]
if (!appVersion) throw new Error('Directory.Build.props 缺少 <Version>')

// 构建产物输出到后端静态资源目录，随 dotnet publish 一起分发
export default defineConfig({
  plugins: [vue()],
  define: {
    __APP_VERSION__: JSON.stringify(appVersion)
  },
  build: {
    outDir: '../src/Mutter.Web/wwwroot',
    emptyOutDir: true
  },
  server: {
    port: 5199,
    proxy: {
      '/api': 'http://127.0.0.1:5188',
      '/hub': { target: 'http://127.0.0.1:5188', ws: true }
    }
  }
})
