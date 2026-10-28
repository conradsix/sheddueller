// Applies saved display preferences before first paint. Loaded synchronously from <head>.
(function () {
  "use strict";

  var root = document.documentElement;

  try {
    var theme = localStorage.getItem("sheddueller.theme");
    if (theme === "light" || theme === "dark") {
      root.setAttribute("data-theme", theme);
    }

    if (localStorage.getItem("sheddueller.sidebar") === "collapsed") {
      root.setAttribute("data-sidebar", "collapsed");
    }
  } catch {
    // Storage can be unavailable (private mode, blocked site data); defaults apply.
  }
})();
