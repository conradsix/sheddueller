(function () {
    "use strict";

    let tableColumnPointerDownHandler = null;
    let activeColumnResizeCleanup = null;

    window.shedduellerJobViews = {
        load: function (key) {
            const value = window.localStorage.getItem(key);
            return value === null ? null : JSON.parse(value);
        },
        save: function (key, value) {
            window.localStorage.setItem(key, JSON.stringify(value));
        },
        registerTableColumnResizing: function (receiver) {
            window.shedduellerJobViews.unregisterTableColumnResizing();

            tableColumnPointerDownHandler = function (event) {
                if (event.button !== 0) {
                    return;
                }

                const handle = event.target.closest("[data-sd-column-resizer]");
                if (handle === null) {
                    return;
                }

                const table = handle.closest(".jobs-table");
                const index = Number.parseInt(handle.dataset.columnIndex, 10);
                // Configured columns carry data-sd-col; selection and action columns keep fixed widths.
                const columns = Array.from(table.querySelectorAll("col[data-sd-col]"));
                if (!Number.isInteger(index) || index < 0 || index >= columns.length) {
                    return;
                }

                const fixedWidth = Array.from(table.querySelectorAll("col:not([data-sd-col]):not(.jobs-table__filler)"))
                    .reduce((total, column) => total + Math.round(column.getBoundingClientRect().width), 0);

                handle.focus({ preventScroll: true });
                event.preventDefault();
                const minimum = Number.parseInt(handle.dataset.minWidth, 10);
                const maximum = Number.parseInt(handle.dataset.maxWidth, 10);
                const startX = event.clientX;
                const widths = columns.map(column => Math.round(column.getBoundingClientRect().width));
                const startWidth = widths[index];
                const previousCursor = document.body.style.cursor;
                let currentWidth = startWidth;

                table.classList.add("jobs-table--resizing");
                document.body.style.cursor = "col-resize";
                handle.setPointerCapture(event.pointerId);

                const resize = function (moveEvent) {
                    moveEvent.preventDefault();
                    currentWidth = Math.max(minimum, Math.min(maximum, Math.round(startWidth + moveEvent.clientX - startX)));
                    widths[index] = currentWidth;
                    columns[index].style.width = `${currentWidth}px`;
                    const tableWidth = widths.reduce((total, width) => total + width, fixedWidth);
                    table.style.width = `max(100%, ${tableWidth}px)`;
                    table.style.minWidth = `${tableWidth}px`;
                    handle.setAttribute("aria-valuenow", currentWidth.toString());
                };

                const finish = function (finishEvent, commit) {
                    finishEvent?.preventDefault();
                    document.removeEventListener("pointermove", resize);
                    document.removeEventListener("pointerup", pointerUp);
                    document.removeEventListener("pointercancel", pointerCancel);
                    table.classList.remove("jobs-table--resizing");
                    document.body.style.cursor = previousCursor;
                    if (handle.hasPointerCapture(event.pointerId)) {
                        handle.releasePointerCapture(event.pointerId);
                    }
                    activeColumnResizeCleanup = null;

                    if (commit && currentWidth !== startWidth) {
                        receiver.invokeMethodAsync("SetColumnWidthFromResizeAsync", index, currentWidth);
                    }
                };

                const pointerUp = function (upEvent) {
                    finish(upEvent, true);
                };

                const pointerCancel = function (cancelEvent) {
                    finish(cancelEvent, true);
                };

                activeColumnResizeCleanup = function () {
                    finish(null, false);
                };
                document.addEventListener("pointermove", resize, { passive: false });
                document.addEventListener("pointerup", pointerUp, { once: true });
                document.addEventListener("pointercancel", pointerCancel, { once: true });
            };

            document.addEventListener("pointerdown", tableColumnPointerDownHandler);
        },
        unregisterTableColumnResizing: function () {
            if (activeColumnResizeCleanup !== null) {
                activeColumnResizeCleanup();
            }

            if (tableColumnPointerDownHandler !== null) {
                document.removeEventListener("pointerdown", tableColumnPointerDownHandler);
                tableColumnPointerDownHandler = null;
            }
        },
        measureJobColumnWidths: function (minimums, maximums) {
            const table = document.querySelector(".jobs-table");
            if (table === null) {
                return [];
            }

            const clone = table.cloneNode(true);
            clone.querySelectorAll("[data-sd-column-resizer]").forEach(handle => handle.remove());
            clone.querySelectorAll("col").forEach(column => column.removeAttribute("style"));
            clone.removeAttribute("style");
            Object.assign(clone.style, {
                position: "fixed",
                top: "0",
                left: "-100000px",
                width: "max-content",
                minWidth: "0",
                tableLayout: "auto",
                visibility: "hidden",
                pointerEvents: "none"
            });
            document.body.appendChild(clone);

            try {
                // Measure only configured columns (th[data-sd-col]); the selection, filler, and action columns
                // are not part of the view and have no minimum/maximum.
                const headerRow = clone.querySelector("thead tr");
                const headers = Array.from(clone.querySelectorAll("thead th[data-sd-col]"));
                const rows = Array.from(clone.querySelectorAll("tr"))
                    .filter(row => row.cells.length === headerRow.cells.length);
                const widths = new Array(headers.length).fill(0);
                for (const header of headers) {
                    const index = Number.parseInt(header.dataset.sdCol, 10);
                    if (!Number.isInteger(index) || index < 0 || index >= minimums.length) {
                        continue;
                    }

                    const measured = rows.reduce(
                        (maximum, row) => Math.max(maximum, Math.ceil(row.cells[header.cellIndex].getBoundingClientRect().width)),
                        0);
                    widths[index] = Math.max(minimums[index], Math.min(maximums[index], measured + 2));
                }

                return widths;
            } finally {
                clone.remove();
            }
        }
    };
})();
