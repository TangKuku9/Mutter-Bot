// Stable filenames remain compatible with existing installations; query versions
// force browsers to fetch the current brand after an application update.
export const stampBrandHtml = (html, version) => html
  .replaceAll('__APP_VERSION__', version)
  .replace(/(\/(?:logo-[\w-]+\.svg|favicon(?:-\d+)?\.(?:svg|png|ico)|apple-touch-icon\.png))(?:\?v=[\w.-]+)?(?=["'])/g, `$1?v=${version}`)
