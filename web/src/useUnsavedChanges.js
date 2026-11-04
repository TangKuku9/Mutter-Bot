import { onMounted, onUnmounted, unref } from 'vue'
import { onBeforeRouteLeave } from 'vue-router'
import { ElMessageBox } from 'element-plus'

export function useUnsavedChanges(dirty) {
  const confirmLeave = async () => {
    if (!unref(dirty)) return true
    try {
      await ElMessageBox.confirm('有尚未保存的修改，确定放弃这些修改吗？', '离开前确认', {
        confirmButtonText: '放弃修改', cancelButtonText: '继续编辑', type: 'warning', distinguishCancelAndClose: true
      })
      return true
    } catch { return false }
  }
  const beforeUnload = (event) => {
    if (unref(dirty)) { event.preventDefault(); event.returnValue = '' }
  }
  onBeforeRouteLeave(confirmLeave)
  onMounted(() => window.addEventListener('beforeunload', beforeUnload))
  onUnmounted(() => window.removeEventListener('beforeunload', beforeUnload))
  return confirmLeave
}
