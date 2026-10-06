import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

// 构建产物输出到后端静态资源目录，随 dotnet publish 一起分发
export default defineConfig({
  plugins: [vue()],
  build: {
    outDir: '../src/QQBot.Web/wwwroot',
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
