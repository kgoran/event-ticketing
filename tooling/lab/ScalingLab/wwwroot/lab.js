// The charts' hover layer: a crosshair and that second's values. Blazor redraws the charts every
// second; this listens on the document, so it keeps working across redraws, and reads the values
// from the chart's data-chart attribute, so it needs no round trip to the server.
(() => {
    const crosshair = document.createElement("div");
    crosshair.className = "crosshair";
    crosshair.hidden = true;
    const tooltip = document.createElement("div");
    tooltip.className = "chart-tooltip";
    tooltip.hidden = true;
    document.body.append(crosshair, tooltip); // outside Blazor's markup, which it leaves alone

    const format = value => value === null || value === undefined
        ? "–"
        : value >= 100 ? Math.round(value).toLocaleString("en") : String(Math.round(value * 10) / 10);

    const hide = () => { crosshair.hidden = true; tooltip.hidden = true; };

    document.addEventListener("pointermove", event => {
        const svg = event.target instanceof Element ? event.target.closest("svg[data-chart]") : null;
        if (!svg) { hide(); return; }

        const chart = JSON.parse(svg.dataset.chart);
        const count = Math.max(...chart.series.map(series => series.values.length));
        if (count < 2) { hide(); return; }

        const box = svg.getBoundingClientRect();
        const scale = box.width / chart.width;
        const x = (event.clientX - box.left) / scale;
        if (x < chart.left || x > chart.left + chart.plotWidth) { hide(); return; }

        const index = Math.round((x - chart.left) / chart.plotWidth * (count - 1));
        const snapped = box.left + (chart.left + index * chart.plotWidth / (count - 1)) * scale;

        crosshair.style.left = `${snapped}px`;
        crosshair.style.top = `${box.top + chart.top * scale}px`;
        crosshair.style.height = `${chart.plotHeight * scale}px`;
        crosshair.hidden = false;

        const secondsAgo = count - 1 - index;
        tooltip.replaceChildren();
        const when = document.createElement("div");
        when.className = "when";
        when.textContent = secondsAgo === 0 ? "last second" : `${secondsAgo} s ago`;
        tooltip.append(when);
        for (const series of chart.series) {
            const row = document.createElement("div");
            row.className = "row";
            const swatch = document.createElement("span");
            swatch.className = series.dashed ? "swatch dashed" : "swatch";
            swatch.style.background = series.color;
            swatch.style.color = series.color;
            const name = document.createElement("span");
            name.textContent = series.name;
            const value = document.createElement("span");
            value.className = "value";
            value.textContent = `${format(series.values[index])} ${chart.unit}`;
            row.append(swatch, name, value);
            tooltip.append(row);
        }

        tooltip.hidden = false;
        const right = snapped + 12 + tooltip.offsetWidth < window.innerWidth;
        tooltip.style.left = `${right ? snapped + 12 : snapped - 12 - tooltip.offsetWidth}px`;
        tooltip.style.top = `${Math.max(8, event.clientY - tooltip.offsetHeight - 12)}px`;
    });

    document.addEventListener("pointerleave", hide);
    window.addEventListener("scroll", hide, { passive: true });
})();
