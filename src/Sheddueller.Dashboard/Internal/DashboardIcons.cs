namespace Sheddueller.Dashboard.Internal;

using System.Collections.Frozen;

/// <summary>
/// Inline SVG bodies for the dashboard icon set. Icons are from Lucide (https://lucide.dev),
/// ISC License, Copyright (c) Lucide Contributors; portions MIT, Copyright (c) Cole Bemis (Feather).
/// Generated from lucide-static 0.577.0; add icons by copying the inner markup of the matching SVG.
/// </summary>
internal static class DashboardIcons
{
    private static readonly FrozenDictionary<string, string> Bodies = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["activity"] = """<path d="M22 12h-2.48a2 2 0 0 0-1.93 1.46l-2.35 8.36a.25.25 0 0 1-.48 0L9.24 2.18a.25.25 0 0 0-.48 0l-2.35 8.36A2 2 0 0 1 4.49 12H2" />""",
        ["arrow-down"] = """<path d="M12 5v14" /><path d="m19 12-7 7-7-7" />""",
        ["arrow-down-to-line"] = """<path d="M12 17V3" /><path d="m6 11 6 6 6-6" /><path d="M19 21H5" />""",
        ["arrow-left"] = """<path d="m12 19-7-7 7-7" /><path d="M19 12H5" />""",
        ["arrow-up"] = """<path d="m5 12 7-7 7 7" /><path d="M12 19V5" />""",
        ["arrow-up-down"] = """<path d="m21 16-4 4-4-4" /><path d="M17 20V4" /><path d="m3 8 4-4 4 4" /><path d="M7 4v16" />""",
        ["ban"] = """<circle cx="12" cy="12" r="10" /><path d="M4.929 4.929 19.07 19.071" />""",
        ["calendar-clock"] = """<path d="M16 14v2.2l1.6 1" /><path d="M16 2v4" /><path d="M21 7.5V6a2 2 0 0 0-2-2H5a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h3.5" /><path d="M3 10h5" /><path d="M8 2v4" /><circle cx="16" cy="16" r="6" />""",
        ["chart-line"] = """<path d="M3 3v16a2 2 0 0 0 2 2h16" /><path d="m19 9-5 5-4-4-3 3" />""",
        ["check"] = """<path d="M20 6 9 17l-5-5" />""",
        ["chevron-down"] = """<path d="m6 9 6 6 6-6" />""",
        ["chevron-right"] = """<path d="m9 18 6-6-6-6" />""",
        ["chevrons-up-down"] = """<path d="m7 15 5 5 5-5" /><path d="m7 9 5-5 5 5" />""",
        ["circle-alert"] = """<circle cx="12" cy="12" r="10" /><line x1="12" x2="12" y1="8" y2="12" /><line x1="12" x2="12.01" y1="16" y2="16" />""",
        ["circle-check"] = """<circle cx="12" cy="12" r="10" /><path d="m9 12 2 2 4-4" />""",
        ["circle-minus"] = """<circle cx="12" cy="12" r="10" /><path d="M8 12h8" />""",
        ["circle-pause"] = """<circle cx="12" cy="12" r="10" /><line x1="10" x2="10" y1="15" y2="9" /><line x1="14" x2="14" y1="15" y2="9" />""",
        ["circle-play"] = """<path d="M9 9.003a1 1 0 0 1 1.517-.859l4.997 2.997a1 1 0 0 1 0 1.718l-4.997 2.997A1 1 0 0 1 9 14.996z" /><circle cx="12" cy="12" r="10" />""",
        ["circle-plus"] = """<circle cx="12" cy="12" r="10" /><path d="M8 12h8" /><path d="M12 8v8" />""",
        ["circle-x"] = """<circle cx="12" cy="12" r="10" /><path d="m15 9-6 6" /><path d="m9 9 6 6" />""",
        ["clock"] = """<circle cx="12" cy="12" r="10" /><path d="M12 6v6l4 2" />""",
        ["columns-3"] = """<rect width="18" height="18" x="3" y="3" rx="2" /><path d="M9 3v18" /><path d="M15 3v18" />""",
        ["command"] = """<path d="M15 6v12a3 3 0 1 0 3-3H6a3 3 0 1 0 3 3V6a3 3 0 1 0-3 3h12a3 3 0 1 0-3-3" />""",
        ["copy"] = """<rect width="14" height="14" x="8" y="8" rx="2" ry="2" /><path d="M4 16c-1.1 0-2-.9-2-2V4c0-1.1.9-2 2-2h10c1.1 0 2 .9 2 2" />""",
        ["download"] = """<path d="M12 15V3" /><path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4" /><path d="m7 10 5 5 5-5" />""",
        ["ellipsis"] = """<circle cx="12" cy="12" r="1" /><circle cx="19" cy="12" r="1" /><circle cx="5" cy="12" r="1" />""",
        ["external-link"] = """<path d="M15 3h6v6" /><path d="M10 14 21 3" /><path d="M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6" />""",
        ["eye-off"] = """<path d="M10.733 5.076a10.744 10.744 0 0 1 11.205 6.575 1 1 0 0 1 0 .696 10.747 10.747 0 0 1-1.444 2.49" /><path d="M14.084 14.158a3 3 0 0 1-4.242-4.242" /><path d="M17.479 17.499a10.75 10.75 0 0 1-15.417-5.151 1 1 0 0 1 0-.696 10.75 10.75 0 0 1 4.446-5.143" /><path d="m2 2 20 20" />""",
        ["gauge"] = """<path d="m12 14 4-4" /><path d="M3.34 19a10 10 0 1 1 17.32 0" />""",
        ["git-fork"] = """<circle cx="12" cy="18" r="3" /><circle cx="6" cy="6" r="3" /><circle cx="18" cy="6" r="3" /><path d="M18 9v2c0 .6-.4 1-1 1H7c-.6 0-1-.4-1-1V9" /><path d="M12 12v3" />""",
        ["grip-vertical"] = """<circle cx="9" cy="12" r="1" /><circle cx="9" cy="5" r="1" /><circle cx="9" cy="19" r="1" /><circle cx="15" cy="12" r="1" /><circle cx="15" cy="5" r="1" /><circle cx="15" cy="19" r="1" />""",
        ["hourglass"] = """<path d="M5 22h14" /><path d="M5 2h14" /><path d="M17 22v-4.172a2 2 0 0 0-.586-1.414L12 12l-4.414 4.414A2 2 0 0 0 7 17.828V22" /><path d="M7 2v4.172a2 2 0 0 0 .586 1.414L12 12l4.414-4.414A2 2 0 0 0 17 6.172V2" />""",
        ["house"] = """<path d="M15 21v-8a1 1 0 0 0-1-1h-4a1 1 0 0 0-1 1v8" /><path d="M3 10a2 2 0 0 1 .709-1.528l7-6a2 2 0 0 1 2.582 0l7 6A2 2 0 0 1 21 10v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z" />""",
        ["inbox"] = """<polyline points="22 12 16 12 14 15 10 15 8 12 2 12" /><path d="M5.45 5.11 2 12v6a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-6l-3.45-6.89A2 2 0 0 0 16.76 4H7.24a2 2 0 0 0-1.79 1.11z" />""",
        ["infinity"] = """<path d="M6 16c5 0 7-8 12-8a4 4 0 0 1 0 8c-5 0-7-8-12-8a4 4 0 1 0 0 8" />""",
        ["info"] = """<circle cx="12" cy="12" r="10" /><path d="M12 16v-4" /><path d="M12 8h.01" />""",
        ["keyboard"] = """<path d="M10 8h.01" /><path d="M12 12h.01" /><path d="M14 8h.01" /><path d="M16 12h.01" /><path d="M18 8h.01" /><path d="M6 8h.01" /><path d="M7 16h10" /><path d="M8 12h.01" /><rect width="20" height="16" x="2" y="4" rx="2" />""",
        ["layers"] = """<path d="M12.83 2.18a2 2 0 0 0-1.66 0L2.6 6.08a1 1 0 0 0 0 1.83l8.58 3.91a2 2 0 0 0 1.66 0l8.58-3.9a1 1 0 0 0 0-1.83z" /><path d="M2 12a1 1 0 0 0 .58.91l8.6 3.91a2 2 0 0 0 1.65 0l8.58-3.9A1 1 0 0 0 22 12" /><path d="M2 17a1 1 0 0 0 .58.91l8.6 3.91a2 2 0 0 0 1.65 0l8.58-3.9A1 1 0 0 0 22 17" />""",
        ["layout-dashboard"] = """<rect width="7" height="9" x="3" y="3" rx="1" /><rect width="7" height="5" x="14" y="3" rx="1" /><rect width="7" height="9" x="14" y="12" rx="1" /><rect width="7" height="5" x="3" y="16" rx="1" />""",
        ["list"] = """<path d="M3 5h.01" /><path d="M3 12h.01" /><path d="M3 19h.01" /><path d="M8 5h13" /><path d="M8 12h13" /><path d="M8 19h13" />""",
        ["list-filter"] = """<path d="M2 5h20" /><path d="M6 12h12" /><path d="M9 19h6" />""",
        ["loader-circle"] = """<path d="M21 12a9 9 0 1 1-6.219-8.56" />""",
        ["lock"] = """<rect width="18" height="11" x="3" y="11" rx="2" ry="2" /><path d="M7 11V7a5 5 0 0 1 10 0v4" />""",
        ["monitor"] = """<rect width="20" height="14" x="2" y="3" rx="2" /><line x1="8" x2="16" y1="21" y2="21" /><line x1="12" x2="12" y1="17" y2="21" />""",
        ["moon"] = """<path d="M20.985 12.486a9 9 0 1 1-9.473-9.472c.405-.022.617.46.402.803a6 6 0 0 0 8.268 8.268c.344-.215.825-.004.803.401" />""",
        ["move-horizontal"] = """<path d="m18 8 4 4-4 4" /><path d="M2 12h20" /><path d="m6 8-4 4 4 4" />""",
        ["panel-left"] = """<rect width="18" height="18" x="3" y="3" rx="2" /><path d="M9 3v18" />""",
        ["pause"] = """<rect x="14" y="3" width="5" height="18" rx="1" /><rect x="5" y="3" width="5" height="18" rx="1" />""",
        ["pencil"] = """<path d="M21.174 6.812a1 1 0 0 0-3.986-3.987L3.842 16.174a2 2 0 0 0-.5.83l-1.321 4.352a.5.5 0 0 0 .623.622l4.353-1.32a2 2 0 0 0 .83-.497z" /><path d="m15 5 4 4" />""",
        ["play"] = """<path d="M5 5a2 2 0 0 1 3.008-1.728l11.997 6.998a2 2 0 0 1 .003 3.458l-12 7A2 2 0 0 1 5 19z" />""",
        ["plus"] = """<path d="M5 12h14" /><path d="M12 5v14" />""",
        ["refresh-cw"] = """<path d="M3 12a9 9 0 0 1 9-9 9.75 9.75 0 0 1 6.74 2.74L21 8" /><path d="M21 3v5h-5" /><path d="M21 12a9 9 0 0 1-9 9 9.75 9.75 0 0 1-6.74-2.74L3 16" /><path d="M8 16H3v5" />""",
        ["rotate-ccw"] = """<path d="M3 12a9 9 0 1 0 9-9 9.75 9.75 0 0 0-6.74 2.74L3 8" /><path d="M3 3v5h5" />""",
        ["save"] = """<path d="M15.2 3a2 2 0 0 1 1.4.6l3.8 3.8a2 2 0 0 1 .6 1.4V19a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2z" /><path d="M17 21v-7a1 1 0 0 0-1-1H8a1 1 0 0 0-1 1v7" /><path d="M7 3v4a1 1 0 0 0 1 1h7" />""",
        ["save-all"] = """<path d="M10 2v3a1 1 0 0 0 1 1h5" /><path d="M18 18v-6a1 1 0 0 0-1-1h-6a1 1 0 0 0-1 1v6" /><path d="M18 22H4a2 2 0 0 1-2-2V6" /><path d="M8 18a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9.172a2 2 0 0 1 1.414.586l2.828 2.828A2 2 0 0 1 22 6.828V16a2 2 0 0 1-2.01 2z" />""",
        ["search"] = """<path d="m21 21-4.34-4.34" /><circle cx="11" cy="11" r="8" />""",
        ["search-x"] = """<path d="m13.5 8.5-5 5" /><path d="m8.5 8.5 5 5" /><circle cx="11" cy="11" r="8" /><path d="m21 21-4.3-4.3" />""",
        ["server"] = """<rect width="20" height="8" x="2" y="2" rx="2" ry="2" /><rect width="20" height="8" x="2" y="14" rx="2" ry="2" /><line x1="6" x2="6.01" y1="6" y2="6" /><line x1="6" x2="6.01" y1="18" y2="18" />""",
        ["settings"] = """<path d="M9.671 4.136a2.34 2.34 0 0 1 4.659 0 2.34 2.34 0 0 0 3.319 1.915 2.34 2.34 0 0 1 2.33 4.033 2.34 2.34 0 0 0 0 3.831 2.34 2.34 0 0 1-2.33 4.033 2.34 2.34 0 0 0-3.319 1.915 2.34 2.34 0 0 1-4.659 0 2.34 2.34 0 0 0-3.32-1.915 2.34 2.34 0 0 1-2.33-4.033 2.34 2.34 0 0 0 0-3.831A2.34 2.34 0 0 1 6.35 6.051a2.34 2.34 0 0 0 3.319-1.915" /><circle cx="12" cy="12" r="3" />""",
        ["star"] = """<path d="M11.525 2.295a.53.53 0 0 1 .95 0l2.31 4.679a2.123 2.123 0 0 0 1.595 1.16l5.166.756a.53.53 0 0 1 .294.904l-3.736 3.638a2.123 2.123 0 0 0-.611 1.878l.882 5.14a.53.53 0 0 1-.771.56l-4.618-2.428a2.122 2.122 0 0 0-1.973 0L6.396 21.01a.53.53 0 0 1-.77-.56l.881-5.139a2.122 2.122 0 0 0-.611-1.879L2.16 9.795a.53.53 0 0 1 .294-.906l5.165-.755a2.122 2.122 0 0 0 1.597-1.16z" />""",
        ["sun"] = """<circle cx="12" cy="12" r="4" /><path d="M12 2v2" /><path d="M12 20v2" /><path d="m4.93 4.93 1.41 1.41" /><path d="m17.66 17.66 1.41 1.41" /><path d="M2 12h2" /><path d="M20 12h2" /><path d="m6.34 17.66-1.41 1.41" /><path d="m19.07 4.93-1.41 1.41" />""",
        ["timer"] = """<line x1="10" x2="14" y1="2" y2="2" /><line x1="12" x2="15" y1="14" y2="11" /><circle cx="12" cy="14" r="8" />""",
        ["trash-2"] = """<path d="M10 11v6" /><path d="M14 11v6" /><path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6" /><path d="M3 6h18" /><path d="M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2" />""",
        ["triangle-alert"] = """<path d="m21.73 18-8-14a2 2 0 0 0-3.48 0l-8 14A2 2 0 0 0 4 21h16a2 2 0 0 0 1.73-3" /><path d="M12 9v4" /><path d="M12 17h.01" />""",
        ["unfold-horizontal"] = """<path d="M16 12h6" /><path d="M8 12H2" /><path d="M12 2v2" /><path d="M12 8v2" /><path d="M12 14v2" /><path d="M12 20v2" /><path d="m19 15 3-3-3-3" /><path d="m5 9-3 3 3 3" />""",
        ["x"] = """<path d="M18 6 6 18" /><path d="m6 6 12 12" />""",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    // Material Symbols names used before the switch to Lucide; remove once no page passes them.
    private static readonly FrozenDictionary<string, string> LegacyNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["add"] = "plus",
        ["all_inclusive"] = "infinity",
        ["analytics"] = "activity",
        ["arrow_back"] = "arrow-left",
        ["arrow_downward"] = "arrow-down",
        ["arrow_upward"] = "arrow-up",
        ["cancel"] = "circle-x",
        ["check"] = "check",
        ["check_circle"] = "circle-check",
        ["close"] = "x",
        ["dashboard"] = "layout-dashboard",
        ["delete"] = "trash-2",
        ["dns"] = "server",
        ["download"] = "download",
        ["drag_indicator"] = "grip-vertical",
        ["edit"] = "pencil",
        ["error"] = "circle-alert",
        ["event_repeat"] = "calendar-clock",
        ["fit_width"] = "unfold-horizontal",
        ["format_list_bulleted"] = "list",
        ["home"] = "house",
        ["info"] = "info",
        ["layers"] = "layers",
        ["lock"] = "lock",
        ["monitoring"] = "chart-line",
        ["pause"] = "pause",
        ["pause_circle"] = "circle-pause",
        ["pending"] = "clock",
        ["play_arrow"] = "play",
        ["play_circle"] = "circle-play",
        ["progress_activity"] = "loader-circle",
        ["query_stats"] = "chart-line",
        ["refresh"] = "refresh-cw",
        ["remove_circle"] = "circle-minus",
        ["restart_alt"] = "rotate-ccw",
        ["save"] = "save",
        ["save_as"] = "save-all",
        ["schedule"] = "clock",
        ["search"] = "search",
        ["search_off"] = "search-x",
        ["settings"] = "settings",
        ["speed"] = "gauge",
        ["stacked_inbox"] = "inbox",
        ["sync"] = "refresh-cw",
        ["timer"] = "timer",
        ["view_column"] = "columns-3",
        ["visibility_off"] = "eye-off",
        ["warning"] = "triangle-alert",
        ["width_normal"] = "move-horizontal",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    public static string? Resolve(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        if (Bodies.ContainsKey(name))
        {
            return name;
        }

        return LegacyNames.TryGetValue(name, out var lucideName) ? lucideName : null;
    }

    public static string? GetBody(string? name)
      => Resolve(name) is { } resolved ? Bodies[resolved] : null;
}
