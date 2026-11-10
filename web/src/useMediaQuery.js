import { ref, readonly, onMounted, onUnmounted } from 'vue'

export function useMediaQuery(query) {
  const media = window.matchMedia(query)
  const matches = ref(media.matches)
  const update = () => { matches.value = media.matches }
  onMounted(() => { update(); media.addEventListener('change', update) })
  onUnmounted(() => media.removeEventListener('change', update))
  return readonly(matches)
}
