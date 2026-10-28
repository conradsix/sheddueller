(function () {
    "use strict";

    let drawerEscapeHandler = null;
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
        registerDrawerEscape: function (receiver) {
            if (drawerEscapeHandler !== null) {
                document.removeEventListener("keydown", drawerEscapeHandler);
            }

            drawerEscapeHandler = function (event) {
                if (event.key === "Escape") {
                    event.preventDefault();
                    receiver.invokeMethodAsync("CloseFromEscapeAsync");
                }
            };
            document.addEventListener("keydown", drawerEscapeHandler);
        },
        unregisterDrawerEscape: function () {
            if (drawerEscapeHandler !== null) {
                document.removeEventListener("keydown", drawerEscapeHandler);
                drawerEscapeHandler = null;
            }
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
                const columns = Array.from(table.querySelectorAll("col:not(.jobs-table__filler)"));
                if (!Number.isInteger(index) || index < 0 || index >= columns.length) {
                    return;
                }

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
                    const tableWidth = widths.reduce((total, width) => total + width, 0);
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
                const headerCount = clone.querySelectorAll("thead th").length;
                const widths = [];
                for (let index = 0; index < headerCount; index++) {
                    const cells = clone.querySelectorAll(`tr > :nth-child(${index + 1})`);
                    const measured = Array.from(cells).reduce(
                        (maximum, cell) => Math.max(maximum, Math.ceil(cell.getBoundingClientRect().width)),
                        0);
                    widths.push(Math.max(minimums[index], Math.min(maximums[index], measured + 2)));
                }

                return widths;
            } finally {
                clone.remove();
            }
        }
    };
})();
