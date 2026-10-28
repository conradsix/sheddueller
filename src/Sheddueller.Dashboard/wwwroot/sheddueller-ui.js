// Client-side behaviour for the Sheddueller dashboard shell and UI primitives: theme, sidebar,
// tooltips, popover menus, dialogs, clipboard, toasts, downloads, keyboard shortcuts and the
// command menu. Everything is attribute-driven so Blazor-rendered markup needs no per-element wiring.
(function () {
  "use strict";

  const root = document.documentElement;
  const hasPopover = Object.prototype.hasOwnProperty.call(HTMLElement.prototype, "popover");
  const isMac = /Mac|iPhone|iPad/.test(navigator.platform || navigator.userAgent);
  const guidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

  // Lucide (ISC) icon bodies used by script-rendered UI.
  const icons = {
    "arrow-right": '<path d="M5 12h14" /><path d="m12 5 7 7-7 7" />',
    "circle-alert": '<circle cx="12" cy="12" r="10" /><line x1="12" x2="12" y1="8" y2="12" /><line x1="12" x2="12.01" y1="16" y2="16" />',
    "circle-check": '<circle cx="12" cy="12" r="10" /><path d="m9 12 2 2 4-4" />',
    "info": '<circle cx="12" cy="12" r="10" /><path d="M12 16v-4" /><path d="M12 8h.01" />',
    "keyboard": '<path d="M10 8h.01" /><path d="M12 12h.01" /><path d="M14 8h.01" /><path d="M16 12h.01" /><path d="M18 8h.01" /><path d="M6 8h.01" /><path d="M7 16h10" /><path d="M8 12h.01" /><rect width="20" height="16" x="2" y="4" rx="2" />',
    "moon": '<path d="M20.985 12.486a9 9 0 1 1-9.473-9.472c.405-.022.617.46.402.803a6 6 0 0 0 8.268 8.268c.344-.215.825-.004.803.401" />',
    "panel-left": '<rect width="18" height="18" x="3" y="3" rx="2" /><path d="M9 3v18" />',
    "pause": '<rect x="14" y="3" width="5" height="18" rx="1" /><rect x="5" y="3" width="5" height="18" rx="1" />',
    "play": '<path d="M5 5a2 2 0 0 1 3.008-1.728l11.997 6.998a2 2 0 0 1 .003 3.458l-12 7A2 2 0 0 1 5 19z" />',
    "refresh-cw": '<path d="M3 12a9 9 0 0 1 9-9 9.75 9.75 0 0 1 6.74 2.74L21 8" /><path d="M21 3v5h-5" /><path d="M21 12a9 9 0 0 1-9 9 9.75 9.75 0 0 1-6.74-2.74L3 16" /><path d="M8 16H3v5" />',
    "search": '<path d="m21 21-4.34-4.34" /><circle cx="11" cy="11" r="8" />',
    "sun": '<circle cx="12" cy="12" r="4" /><path d="M12 2v2" /><path d="M12 20v2" /><path d="m4.93 4.93 1.41 1.41" /><path d="m17.66 17.66 1.41 1.41" /><path d="M2 12h2" /><path d="M20 12h2" /><path d="m6.34 17.66-1.41 1.41" /><path d="m19.07 4.93-1.41 1.41" />',
  };

  function icon(name) {
    const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    svg.setAttribute("class", "sd-icon");
    svg.setAttribute("viewBox", "0 0 24 24");
    svg.setAttribute("aria-hidden", "true");
    svg.innerHTML = icons[name] || icons.info;
    return svg;
  }

  function store(key, value) {
    try {
      if (value === null) {
        localStorage.removeItem(key);
      } else {
        localStorage.setItem(key, value);
      }
    } catch {
      // Preferences are a convenience; the dashboard works without storage.
    }
  }

  function navigate(href) {
    if (window.Blazor && typeof window.Blazor.navigateTo === "function") {
      window.Blazor.navigateTo(href);
    } else {
      window.location.href = new URL(href, document.baseURI).href;
    }
  }

  function isTyping(target) {
    return target instanceof Element && target.closest("input, textarea, select, [contenteditable='true']") !== null;
  }

  // ------------------------------------------------------------------ theme
  function currentTheme() {
    return root.getAttribute("data-theme") || "system";
  }

  function isDark() {
    const theme = root.getAttribute("data-theme");
    return theme ? theme === "dark" : window.matchMedia("(prefers-color-scheme: dark)").matches;
  }

  function setTheme(theme) {
    if (theme === "light" || theme === "dark") {
      root.setAttribute("data-theme", theme);
      store("sheddueller.theme", theme);
    } else {
      root.removeAttribute("data-theme");
      store("sheddueller.theme", null);
    }

    syncThemeButtons();
    document.dispatchEvent(new CustomEvent("sheddueller:themechange"));
  }

  function syncThemeButtons() {
    const theme = currentTheme();
    for (const button of document.querySelectorAll("[data-sd-theme]")) {
      button.setAttribute("aria-checked", String(button.getAttribute("data-sd-theme") === theme));
    }
  }

  // ---------------------------------------------------------------- sidebar
  const narrow = window.matchMedia("(max-width: 860px)");

  function toggleSidebar() {
    if (narrow.matches) {
      if (root.getAttribute("data-nav") === "open") {
        root.removeAttribute("data-nav");
      } else {
        root.setAttribute("data-nav", "open");
      }

      return;
    }

    const collapsed = root.getAttribute("data-sidebar") === "collapsed";
    if (collapsed) {
      root.removeAttribute("data-sidebar");
    } else {
      root.setAttribute("data-sidebar", "collapsed");
    }

    store("sheddueller.sidebar", collapsed ? null : "collapsed");
  }

  // --------------------------------------------------------------- tooltips
  let tooltip = null;
  let tooltipTimer = 0;
  let tooltipFor = null;

  function ensureTooltip() {
    if (!tooltip) {
      tooltip = document.createElement("div");
      tooltip.className = "sd-tooltip";
      tooltip.setAttribute("role", "tooltip");
      if (hasPopover) {
        tooltip.popover = "manual";
      } else {
        tooltip.hidden = true;
      }

      document.body.append(tooltip);
    }

    return tooltip;
  }

  function showTooltip(element) {
    const text = element.getAttribute("data-sd-tooltip");
    if (!text || (element.getAttribute("data-sd-tooltip-when") === "collapsed" && root.getAttribute("data-sidebar") !== "collapsed")) {
      return;
    }

    const tip = ensureTooltip();
    tip.textContent = text;
    try {
      if (hasPopover) {
        if (!tip.matches(":popover-open")) {
          tip.showPopover();
        }
      } else {
        tip.hidden = false;
      }
    } catch {
      return;
    }

    const anchor = element.getBoundingClientRect();
    const size = tip.getBoundingClientRect();
    const side = element.getAttribute("data-sd-tooltip-side");
    let top;
    let left;
    if (side === "right") {
      top = anchor.top + anchor.height / 2 - size.height / 2;
      left = anchor.right + 8;
    } else {
      top = anchor.top - size.height - 6;
      if (top < 8) {
        top = anchor.bottom + 6;
      }

      left = anchor.left + anchor.width / 2 - size.width / 2;
    }

    tip.style.top = `${Math.max(8, top)}px`;
    tip.style.left = `${Math.max(8, Math.min(left, window.innerWidth - size.width - 8))}px`;
    tooltipFor = element;
  }

  function hideTooltip() {
    window.clearTimeout(tooltipTimer);
    tooltipFor = null;
    if (!tooltip) {
      return;
    }

    if (hasPopover) {
      if (tooltip.matches(":popover-open")) {
        tooltip.hidePopover();
      }
    } else {
      tooltip.hidden = true;
    }
  }

  document.addEventListener("pointerover", (event) => {
    const element = event.target instanceof Element ? event.target.closest("[data-sd-tooltip]") : null;
    if (element === tooltipFor) {
      return;
    }

    hideTooltip();
    if (element && event.pointerType !== "touch") {
      tooltipTimer = window.setTimeout(() => showTooltip(element), 450);
    }
  });

  document.addEventListener("focusin", (event) => {
    const element = event.target instanceof Element ? event.target.closest("[data-sd-tooltip]") : null;
    hideTooltip();
    if (element && element.matches(":focus-visible")) {
      tooltipTimer = window.setTimeout(() => showTooltip(element), 300);
    }
  });

  document.addEventListener("pointerdown", hideTooltip, true);
  document.addEventListener("scroll", hideTooltip, true);

  // ------------------------------------------------------------------ menus
  // Menus are popover elements opened by a popovertarget button. Anchor positioning is not yet
  // available everywhere, so place them next to their invoker and flip at the viewport edges.
  function placeMenu(menu) {
    const invoker = menu.id ? document.querySelector(`[popovertarget="${CSS.escape(menu.id)}"]`) : null;
    if (!invoker) {
      return;
    }

    const anchor = invoker.getBoundingClientRect();
    const size = menu.getBoundingClientRect();
    let top = anchor.bottom + 6;
    if (top + size.height > window.innerHeight - 8 && anchor.top - size.height - 6 > 8) {
      top = anchor.top - size.height - 6;
    }

    let left = menu.getAttribute("data-sd-align") === "start" ? anchor.left : anchor.right - size.width;
    left = Math.max(8, Math.min(left, window.innerWidth - size.width - 8));
    menu.style.top = `${Math.max(8, top)}px`;
    menu.style.left = `${left}px`;
  }

  document.addEventListener("toggle", (event) => {
    const menu = event.target;
    if (!(menu instanceof HTMLElement) || !menu.classList.contains("sd-menu")) {
      return;
    }

    if (event.newState === "open") {
      placeMenu(menu);
      menu.setAttribute("data-sd-placed", "");
      const first = menu.querySelector("input, .sd-menu__item:not(:disabled)");
      if (first) {
        first.focus({ preventScroll: true });
      }
    } else {
      menu.removeAttribute("data-sd-placed");
    }
  }, true);

  document.addEventListener("keydown", (event) => {
    const menu = event.target instanceof Element ? event.target.closest(".sd-menu") : null;
    if (!menu || (event.key !== "ArrowDown" && event.key !== "ArrowUp")) {
      return;
    }

    const items = [...menu.querySelectorAll(".sd-menu__item:not(:disabled)")];
    if (items.length === 0) {
      return;
    }

    event.preventDefault();
    const index = items.indexOf(document.activeElement);
    const next = event.key === "ArrowDown"
      ? items[(index + 1) % items.length]
      : items[(index - 1 + items.length) % items.length];
    next.focus();
  });

  // ---------------------------------------------------------------- dialogs
  function showDialog(dialog) {
    if (dialog instanceof HTMLDialogElement && !dialog.open) {
      hideTooltip();
      dialog.showModal();
    }
  }

  function closeDialog(dialog, returnValue) {
    if (dialog instanceof HTMLDialogElement && dialog.open) {
      dialog.close(returnValue);
    }
  }

  // closedby="any" is not supported everywhere; close on a click outside the dialog box.
  document.addEventListener("click", (event) => {
    const dialog = event.target;
    if (!(dialog instanceof HTMLDialogElement) || !dialog.open || !dialog.matches(".sd-dialog, .sd-sheet, .sd-command")) {
      return;
    }

    const box = dialog.getBoundingClientRect();
    if (event.clientX < box.left || event.clientX > box.right || event.clientY < box.top || event.clientY > box.bottom) {
      dialog.close();
    }
  });

  // --------------------------------------------------------- clipboard/toast
  let toaster = null;

  function toast(options) {
    const settings = typeof options === "string" ? { title: options } : options || {};
    if (!toaster) {
      toaster = document.createElement("div");
      toaster.className = "sd-toaster";
      toaster.setAttribute("aria-live", "polite");
      if (hasPopover) {
        toaster.popover = "manual";
        toaster.style.cssText = "inset:auto;margin:0;border:0;padding:0;background:none;overflow:visible";
      }

      document.body.append(toaster);
    }

    if (hasPopover) {
      // Re-show so the toaster sits above any open modal in the top layer.
      if (toaster.matches(":popover-open")) {
        toaster.hidePopover();
      }

      toaster.showPopover();
    }

    const tone = settings.tone === "success" || settings.tone === "error" ? settings.tone : "info";
    const item = document.createElement("div");
    item.className = `sd-toast sd-toast--${tone}`;
    item.setAttribute("role", tone === "error" ? "alert" : "status");
    item.append(icon(tone === "success" ? "circle-check" : tone === "error" ? "circle-alert" : "info"));
    const body = document.createElement("div");
    const title = document.createElement("div");
    title.textContent = settings.title || "";
    body.append(title);
    if (settings.description) {
      const description = document.createElement("div");
      description.className = "sd-toast__description";
      description.textContent = settings.description;
      body.append(description);
    }

    item.append(body);
    toaster.append(item);
    window.setTimeout(() => {
      item.remove();
      if (hasPopover && toaster.childElementCount === 0 && toaster.matches(":popover-open")) {
        toaster.hidePopover();
      }
    }, settings.duration || 4500);
  }

  function legacyCopy(text) {
    const area = document.createElement("textarea");
    area.value = text;
    area.setAttribute("readonly", "");
    area.style.cssText = "position:fixed;top:0;left:0;opacity:0";
    document.body.append(area);
    area.select();
    let copied = false;
    try {
      copied = document.execCommand("copy");
    } catch {
      copied = false;
    }

    area.remove();
    return copied;
  }

  async function copy(text, label) {
    let copied = false;
    try {
      await navigator.clipboard.writeText(text);
      copied = true;
    } catch {
      copied = legacyCopy(text);
    }

    const what = label || "text";
    toast(copied
      ? { title: `Copied ${what}`, tone: "success" }
      : { title: `Couldn't copy ${what}`, description: "The browser blocked clipboard access.", tone: "error" });
    return copied;
  }

  function download(fileName, content, contentType) {
    const blob = new Blob([content], { type: contentType || "text/plain;charset=utf-8" });
    const url = URL.createObjectURL(blob);
    const link = document.createElement("a");
    link.href = url;
    link.download = fileName;
    document.body.append(link);
    link.click();
    link.remove();
    window.setTimeout(() => URL.revokeObjectURL(url), 1000);
  }

  // ------------------------------------------------------- declarative hooks
  document.addEventListener("click", (event) => {
    if (!(event.target instanceof Element)) {
      return;
    }

    const target = event.target;
    const themeButton = target.closest("[data-sd-theme]");
    if (themeButton) {
      setTheme(themeButton.getAttribute("data-sd-theme"));
      return;
    }

    const action = target.closest("[data-sd-action]");
    if (action) {
      switch (action.getAttribute("data-sd-action")) {
        case "toggle-sidebar":
          toggleSidebar();
          return;
        case "close-nav":
          root.removeAttribute("data-nav");
          return;
        case "command":
          openCommand();
          return;
        default:
          break;
      }
    }

    const opener = target.closest("[data-sd-open-dialog]");
    if (opener) {
      showDialog(document.getElementById(opener.getAttribute("data-sd-open-dialog")));
      return;
    }

    const closer = target.closest("[data-sd-close-dialog]");
    if (closer) {
      closeDialog(closer.closest("dialog"));
      return;
    }

    const copyButton = target.closest("[data-sd-copy]");
    if (copyButton) {
      copy(copyButton.getAttribute("data-sd-copy"), copyButton.getAttribute("data-sd-copy-label"));
      return;
    }

    // Close a popover menu after one of its items runs.
    const item = target.closest(".sd-menu .sd-menu__item");
    if (item && !item.hasAttribute("data-sd-keep-open") && hasPopover) {
      const menu = item.closest("[popover]");
      if (menu && menu.matches(":popover-open")) {
        menu.hidePopover();
      }
    }

    // Close the narrow-screen navigation drawer after navigating.
    if (target.closest(".sd-sidebar a[href]") && root.getAttribute("data-nav") === "open") {
      root.removeAttribute("data-nav");
    }
  });

  // ----------------------------------------------------------- command menu
  let commandItems = [];
  let commandActive = 0;

  function commandElements() {
    return {
      dialog: document.getElementById("sd-command"),
      input: document.getElementById("sd-command-input"),
      list: document.getElementById("sd-command-list"),
    };
  }

  function openCommand() {
    const { dialog, input } = commandElements();
    if (!dialog || !input) {
      return;
    }

    input.value = "";
    showDialog(dialog);
    renderCommand();
    input.focus();
  }

  function buildCommandItems(query) {
    const text = query.trim();
    const lower = text.toLowerCase();
    const matches = (label) => lower.length === 0 || label.toLowerCase().includes(lower);
    const items = [];

    if (guidPattern.test(text)) {
      items.push({ group: "Jobs", icon: "arrow-right", label: `Open job ${text.toLowerCase()}`, run: () => navigate(`jobs/${text.toLowerCase()}`) });
    }

    for (const link of document.querySelectorAll(".sd-nav a[data-sd-nav]")) {
      const label = link.textContent.trim();
      if (matches(label)) {
        const svg = link.querySelector("svg");
        items.push({ group: "Pages", svg: svg ? svg.cloneNode(true) : null, label, run: () => navigate(link.getAttribute("href")) });
      }
    }

    if (text.length > 0 && !guidPattern.test(text)) {
      const encoded = encodeURIComponent(text);
      items.push(
        { group: "Find jobs", icon: "search", label: `Handler contains “${text}”`, run: () => navigate(`jobs?handler=${encoded}`) },
        { group: "Find jobs", icon: "search", label: `Tag contains “${text}”`, run: () => navigate(`jobs?tag=${encoded}`) },
        { group: "Find jobs", icon: "search", label: `Concurrency group contains “${text}”`, run: () => navigate(`jobs?group=${encoded}`) });
    }

    const live = document.querySelector("[data-sd-action-live]");
    const refresh = document.querySelector("[data-sd-action-refresh]");
    const actions = [
      { icon: isDark() ? "sun" : "moon", label: isDark() ? "Use light theme" : "Use dark theme", run: () => setTheme(isDark() ? "light" : "dark") },
      live ? { icon: live.getAttribute("aria-pressed") === "true" ? "pause" : "play", label: live.getAttribute("aria-pressed") === "true" ? "Pause live updates" : "Resume live updates", hint: "P", run: () => live.click() } : null,
      refresh ? { icon: "refresh-cw", label: "Refresh now", run: () => refresh.click() } : null,
      { icon: "panel-left", label: "Toggle sidebar", hint: "[", run: toggleSidebar },
      { icon: "keyboard", label: "Keyboard shortcuts", hint: "?", run: () => showDialog(document.getElementById("sd-shortcuts")) },
    ];

    for (const action of actions) {
      if (action && matches(action.label)) {
        items.push({ group: "Actions", ...action });
      }
    }

    return items;
  }

  function renderCommand() {
    const { input, list } = commandElements();
    if (!input || !list) {
      return;
    }

    commandItems = buildCommandItems(input.value);
    commandActive = 0;
    list.replaceChildren();
    let group = null;
    commandItems.forEach((item, index) => {
      if (item.group !== group) {
        group = item.group;
        const label = document.createElement("div");
        label.className = "sd-menu__label";
        label.textContent = group;
        list.append(label);
      }

      const option = document.createElement("div");
      option.className = "sd-menu__item";
      option.id = `sd-command-${index}`;
      option.setAttribute("role", "option");
      option.setAttribute("aria-selected", String(index === commandActive));
      option.dataset.index = String(index);
      option.append(item.svg || icon(item.icon));
      const text = document.createElement("span");
      text.textContent = item.label;
      option.append(text);
      if (item.hint) {
        const hint = document.createElement("span");
        hint.className = "sd-menu__hint";
        hint.textContent = item.hint;
        option.append(hint);
      }

      list.append(option);
    });

    if (commandItems.length === 0) {
      const empty = document.createElement("div");
      empty.className = "sd-menu__empty";
      empty.textContent = `No matches for “${input.value}”`;
      list.append(empty);
    }

    input.setAttribute("aria-activedescendant", commandItems.length > 0 ? "sd-command-0" : "");
  }

  function moveCommand(delta) {
    const { input, list } = commandElements();
    if (commandItems.length === 0) {
      return;
    }

    commandActive = (commandActive + delta + commandItems.length) % commandItems.length;
    for (const option of list.querySelectorAll("[role='option']")) {
      option.setAttribute("aria-selected", String(Number(option.dataset.index) === commandActive));
    }

    const active = document.getElementById(`sd-command-${commandActive}`);
    active.scrollIntoView({ block: "nearest" });
    input.setAttribute("aria-activedescendant", active.id);
  }

  function runCommand(index) {
    const item = commandItems[index];
    if (!item) {
      return;
    }

    closeDialog(commandElements().dialog);
    item.run();
  }

  document.addEventListener("input", (event) => {
    if (event.target instanceof Element && event.target.id === "sd-command-input") {
      renderCommand();
    }
  });

  document.addEventListener("keydown", (event) => {
    if (!(event.target instanceof Element) || event.target.id !== "sd-command-input") {
      return;
    }

    if (event.key === "ArrowDown" || event.key === "ArrowUp") {
      event.preventDefault();
      moveCommand(event.key === "ArrowDown" ? 1 : -1);
    } else if (event.key === "Enter") {
      event.preventDefault();
      runCommand(commandActive);
    }
  });

  document.addEventListener("pointermove", (event) => {
    const option = event.target instanceof Element ? event.target.closest("#sd-command-list [role='option']") : null;
    if (option && Number(option.dataset.index) !== commandActive) {
      moveCommand(Number(option.dataset.index) - commandActive);
    }
  });

  document.addEventListener("click", (event) => {
    const option = event.target instanceof Element ? event.target.closest("#sd-command-list [role='option']") : null;
    if (option) {
      runCommand(Number(option.dataset.index));
    }
  });

  // -------------------------------------------------------------- shortcuts
  let goPrefixAt = 0;

  document.addEventListener("keydown", (event) => {
    if ((event.metaKey || event.ctrlKey) && !event.altKey && event.key.toLowerCase() === "k") {
      event.preventDefault();
      const { dialog } = commandElements();
      if (dialog && dialog.open) {
        closeDialog(dialog);
      } else {
        openCommand();
      }

      return;
    }

    if (event.defaultPrevented || event.metaKey || event.ctrlKey || event.altKey || isTyping(event.target)) {
      return;
    }

    if (document.querySelector("dialog[open]") || (hasPopover && document.querySelector(".sd-menu:popover-open"))) {
      return;
    }

    const key = event.key;
    if (goPrefixAt > 0 && Date.now() - goPrefixAt < 1000) {
      goPrefixAt = 0;
      const link = document.querySelector(`.sd-nav a[data-sd-nav-key="${CSS.escape(key)}"]`);
      if (link) {
        event.preventDefault();
        navigate(link.getAttribute("href"));
      }

      return;
    }

    switch (key) {
      case "g":
        goPrefixAt = Date.now();
        break;
      case "?":
        event.preventDefault();
        showDialog(document.getElementById("sd-shortcuts"));
        break;
      case "/": {
        const search = document.querySelector("[data-sd-search]") || document.querySelector(".sd-main input[type='search']");
        if (search) {
          event.preventDefault();
          search.focus();
          search.select?.();
        }

        break;
      }
      case "p":
        document.querySelector("[data-sd-action-live]")?.click();
        break;
      case "[":
        toggleSidebar();
        break;
      default:
        break;
    }
  });

  // ------------------------------------------------------------------- boot
  function boot() {
    for (const kbd of document.querySelectorAll("[data-sd-mod-key]")) {
      kbd.textContent = isMac ? "⌘" : "Ctrl";
    }

    syncThemeButtons();
  }

  // Blazor renders the shell after this script runs; refresh platform-specific labels when it appears.
  new MutationObserver(() => {
    if (document.querySelector("[data-sd-mod-key]:empty")) {
      boot();
    }
  }).observe(document.body, { childList: true, subtree: true });

  window.sheddueller = {
    setTheme,
    toggleSidebar,
    toast,
    copy,
    download,
    navigate,
    dialog: {
      show: showDialog,
      close: closeDialog,
      attach(dialog, dotNetReference) {
        if (!(dialog instanceof HTMLDialogElement) || dialog.dataset.sdAttached) {
          return;
        }

        dialog.dataset.sdAttached = "true";
        dialog.addEventListener("close", () => {
          dotNetReference.invokeMethodAsync("OnDialogClosed");
        });
      },
    },
  };

  boot();
})();
