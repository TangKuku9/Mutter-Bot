/*
     * 主题引导：优先使用面板里显式保存的深色设置（localStorage），
     * 没有保存过时跟随系统 prefers-color-scheme，并在系统主题变化时实时切换。
     * 同时保留对 html.dark 类名的支持（与主面板 panelSettings 行为一致）。
     */
    (function () {
      var root = document.documentElement;
      var savedDark = null;
      try {
        var raw = localStorage.getItem('mutter-panel-settings');
        if (raw) {
          var saved = JSON.parse(raw);
          if (typeof saved.darkMode === 'boolean') savedDark = saved.darkMode;
        }
      } catch (e) { /* 本地存储不可用时忽略 */ }

      var mql = window.matchMedia ? window.matchMedia('(prefers-color-scheme: dark)') : null;
      var hadDarkClass = root.classList.contains('dark');

      function applyTheme(dark) {
        root.classList.toggle('dark', dark);
        root.classList.toggle('light', !dark);
        var meta = document.querySelector('meta[name="theme-color"]');
        if (meta) meta.setAttribute('content', dark ? '#070b14' : '#f5f6fb');
      }

      applyTheme(savedDark !== null ? savedDark : (hadDarkClass || !!(mql && mql.matches)));

      if (mql) {
        var onChange = function (event) {
          if (savedDark === null) applyTheme(event.matches);
        };
        if (mql.addEventListener) mql.addEventListener('change', onChange);
        else if (mql.addListener) mql.addListener(onChange);
      }
    })();
