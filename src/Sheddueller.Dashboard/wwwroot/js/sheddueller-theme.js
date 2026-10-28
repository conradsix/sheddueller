// Loaded first, synchronously from <head>: creates the `sheddueller` namespace that the other dashboard
// scripts extend, provides its guarded browser storage, and applies saved display preferences before first paint.
(function () {
  "use strict";

  const sheddueller = (window.sheddueller ??= {});

  // localStorage can be unavailable (private mode, blocked site data) or full. Nothing here throws: reads
  // report whether they worked and writes return false, so each caller decides whether a failure matters.
  const storage = {
    // { ok, value }: value is the stored string, or null when nothing is stored or storage can't be read.
    read(key) {
      try {
        return { ok: true, value: window.localStorage.getItem(key) };
      } catch {
        return { ok: false, value: null };
      }
    },

    // Stores a string, or removes the key when value is null. Returns whether it worked.
    write(key, value) {
      try {
        if (value === null) {
          window.localStorage.removeItem(key);
        } else {
          window.localStorage.setItem(key, value);
        }

        return true;
      } catch {
        return false;
      }
    },

    // { ok, value }: as read, with the value parsed; a stored value that isn't valid JSON can't be read.
    readJson(key) {
      const stored = storage.read(key);
      if (!stored.ok || stored.value === null) {
        return stored;
      }

      try {
        return { ok: true, value: JSON.parse(stored.value) };
      } catch {
        return { ok: false, value: null };
      }
    },

    // Stores a value as JSON. Returns whether it worked.
    writeJson(key, value) {
      let text;
      try {
        text = JSON.stringify(value);
      } catch {
        return false;
      }

      return storage.write(key, text);
    },
  };

  sheddueller.storage = storage;

  // Preferences are a convenience: when storage can't be read, the defaults apply.
  const root = document.documentElement;
  const theme = storage.read("sheddueller.theme").value;
  if (theme === "light" || theme === "dark") {
    root.setAttribute("data-theme", theme);
  }

  if (storage.read("sheddueller.sidebar").value === "collapsed") {
    root.setAttribute("data-sidebar", "collapsed");
  }
})();
