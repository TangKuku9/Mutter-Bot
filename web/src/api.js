import axios from 'axios'
import { ElMessage } from 'element-plus'

const api = axios.create({ baseURL: '/api', timeout: 30000 })

// 401 → 会话过期，跳回登录页（携带当前路径便于登录后返回）
api.interceptors.response.use(
  (res) => res,
  (err) => {
    if (err.response?.status === 401 && !location.pathname.includes('login')) {
      location.href = `/login.html?return=${encodeURIComponent(location.pathname + location.hash)}`
    }
    const msg = err.response?.data?.error || err.message || '请求失败'
    ElMessage.error(msg)
    return Promise.reject(err)
  }
)

export default api
