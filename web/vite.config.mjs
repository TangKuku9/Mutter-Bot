import { readFileSync, writeFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'
import Components from 'unplugin-vue-components/vite'
import { ElementPlusResolver } from 'unplugin-vue-components/resolvers'
import { stampBrandHtml } from './brandVersion.mjs'

// 版本号唯一来源是仓库根目录的 Directory.Build.props，避免前端与程序集各写一份
const buildProps = readFileSync(
  fileURLToPath(new URL('../Directory.Build.props', import.meta.url)),
  'utf8'
)
const appVersion = buildProps.match(/<Version>([^<]+)<\/Version>/)?.[1]
if (!appVersion) throw new Error('Directory.Build.props 缺少 <Version>')

const publicLogin = fileURLToPath(new URL('./public/login.html', import.meta.url))
const brandVersionPlugin = () => {
  let outDir
  return {
    name: 'mutter-brand-version',
    configResolved(config) { outDir = resolve(config.root, config.build.outDir) },
    transformIndexHtml(html) { return stampBrandHtml(html, appVersion) },
    configureServer(server) {
      server.middlewares.use((req, res, next) => {
        if (req.url?.split('?')[0] !== '/login.html') return next()
        res.setHeader('Content-Type', 'text/html; charset=utf-8')
        res.end(stampBrandHtml(readFileSync(publicLogin, 'utf8'), appVersion))
      })
    },
    writeBundle() {
      writeFileSync(resolve(outDir, 'login.html'), stampBrandHtml(readFileSync(publicLogin, 'utf8'), appVersion))
    }
  }
}

// 构建产物输出到后端静态资源目录，随 dotnet publish 一起分发
export default defineConfig({
  plugins: [vue(), Components({ dts: false, resolvers: [ElementPlusResolver()] }), brandVersionPlugin()],
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
