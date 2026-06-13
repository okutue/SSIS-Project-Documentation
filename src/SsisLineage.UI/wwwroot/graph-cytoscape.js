// Triggers a browser download of base64-encoded content (used for CSV/JSON/YAML/Cypher export).
window.downloadFileFromBase64 = (fileName, base64Data) => {
    const link = document.createElement('a');
    link.download = fileName;
    link.href = 'data:application/octet-stream;base64,' + base64Data;
    document.body.appendChild(link);
    link.click();
    document.body.removeChild(link);
};

// Interactive SSIS lineage built on Cytoscape.js.
//  • "object" mode  → Fabric-style card nodes (package/task/component), dagre LR.
//  • "column" mode  → OpenMetadata-style table cards with column rows and
//                     column-to-column edges (built from column mappings).
// Edges leave each node from a single side-anchor and curve (sankey-like).
window.cyLineage = (function () {
    let cy = null;
    let dotNetRef = null;
    let mode = 'object';
    let isDark = false;         // kept in module scope so exportPng/toggleFullscreen can read it
    let homePositions = null;   // post-layout node positions, for resetLayout() after manual drags
    let columnClickHandler = null; // optional drill-down hook (e.g. VS Code webview); inert otherwise

    const safe = t => (t ? String(t).replace(/\s+/g, ' ').trim() : '');
    const esc = s => String(s ?? '').replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));

    const ICON_PATHS = {
        package: 'M20 2H4c-1 0-2 .9-2 2v3.01c0 .72.43 1.34 1 1.69V20c0 1.1 1.1 2 2 2h14c.9 0 2-.9 2-2V8.7c.57-.35 1-.97 1-1.69V4c0-1.1-1-2-2-2zm-5 12H9v-2h6v2zm5-7H4V4h16v3z',
        task: 'M21 3h-6.18C14.4 1.84 13.3 1 12 1c-1.3 0-2.4.84-2.82 2H3v18h18V3zm-9 0c.55 0 1 .45 1 1s-.45 1-1 1-1-.45-1-1 .45-1 1-1zm-2 14l-4-4 1.41-1.41L10 14.17l6.59-6.59L18 9l-8 8z',
        sql: 'M2 4v4h20V4H2zm4 3H4V5h2v2zm-4 7h20v-4H2v4zm4-3H4v2H4v-2h2zm-4 9h20v-4H2v4zm4-3H4v2h2v-2z',
        source: 'M11 7L9.6 8.4l2.6 2.6H2v2h10.2l-2.6 2.6L11 17l5-5-5-5zm9 12h-8v2h8c1.1 0 2-.9 2-2V5c0-1.1-.9-2-2-2h-8v2h8v14z',
        destination: 'M5 5h8V3H5c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h8v-2H5V5zm16 7l-4-4v3H9v2h8v3l4-4z',
        lookup: 'M15.5 14h-.79l-.28-.27C15.41 12.59 16 11.11 16 9.5 16 5.91 13.09 3 9.5 3S3 5.91 3 9.5 5.91 16 9.5 16c1.61 0 3.09-.59 4.23-1.57l.27.28v.79l5 4.99L20.49 19l-4.99-5zm-6 0C7.01 14 5 11.99 5 9.5S7.01 5 9.5 5 14 7.01 14 9.5 11.99 14 9.5 14z',
        transform: 'M10.59 9.17L5.41 4 4 5.41l5.17 5.17 1.42-1.41zM14.5 4l2.04 2.04L4 18.59 5.41 20 17.96 7.46 20 9.5V4h-5.5zm.33 9.41l-1.41 1.41 3.13 3.13L14.5 20H20v-5.5l-2.04 2.04-3.13-3.13z',
        table: 'M3 3h18v18H3V3zm2 4v4h6V7H5zm8 0v4h6V7h-6zm-8 6v4h6v-4H5zm8 0v4h6v-4h-6z'
    };
    const COLORS = {
        package: '#0ea5e9', task: '#475569', sql: '#2563eb',
        source: '#15803d', destination: '#b45309', lookup: '#0e7490', transform: '#6d28d9', table: '#2563eb'
    };
    const iconSvg = key => `<svg viewBox="0 0 24 24" width="18" height="18"><path fill="currentColor" d="${ICON_PATHS[key] || ICON_PATHS.task}"/></svg>`;

    const FOOT_ICONS = {
        lineage: 'M14 4l5 5-5 5v-3H8v3l-5-5 5-5v3h6V4z',
        details: 'M11 7h2v2h-2V7zm0 4h2v6h-2v-6zm1-9C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm0 18c-4.41 0-8-3.59-8-8s3.59-8 8-8 8 3.59 8 8-3.59 8-8 8z'
    };
    const footSvg = key => `<svg viewBox="0 0 24 24" width="15" height="15"><path fill="currentColor" d="${FOOT_ICONS[key]}"/></svg>`;

    const fileName = p => { const s = safe(p); const m = s.split(/[\\/]/); return m[m.length - 1] || s; };
    const trunc = (s, n) => { s = safe(s); return s.length > n ? s.slice(0, n) + '…' : s; };

    // Measure-based label wrapping: long table/column names break onto multiple lines
    // (growing the node's HEIGHT) instead of being truncated, so the full name stays
    // visible in PNG/screenshot exports where the hover tooltip can't be captured.
    // Column names rarely contain spaces, so a too-long token is hard-broken per character.
    const _measureCtx = (() => { try { return document.createElement('canvas').getContext('2d'); } catch (e) { return null; } })();
    function wrapLabel(text, fontPx, fontWeight, maxWidth, maxLines) {
        text = safe(text);
        if (!text || !_measureCtx) return { text, lines: 1 };
        _measureCtx.font = `${fontWeight || 400} ${fontPx}px "Helvetica Neue", Helvetica, Arial, sans-serif`;
        const fits = s => _measureCtx.measureText(s).width <= maxWidth;
        if (fits(text)) return { text, lines: 1 };

        const out = [];
        let cur = '';
        for (const tok of text.split(/(\s+)/)) {
            if (tok === '') continue;
            if (fits(cur + tok)) { cur += tok; continue; }
            if (/^\s+$/.test(tok)) { if (cur) { out.push(cur); cur = ''; } continue; }
            if (cur) { out.push(cur); cur = ''; }
            let chunk = '';                       // hard-break a token longer than one line
            for (const ch of tok) {
                if (chunk === '' || fits(chunk + ch)) chunk += ch;
                else { out.push(chunk); chunk = ch; }
            }
            cur = chunk;
        }
        if (cur) out.push(cur);

        let lines = out.length ? out : [text];
        if (maxLines && lines.length > maxLines) {
            lines = lines.slice(0, maxLines);
            let last = lines[maxLines - 1];
            while (last.length > 1 && !fits(last + '…')) last = last.slice(0, -1);
            lines[maxLines - 1] = last + '…';
        }
        return { text: lines.join('\n'), lines: lines.length };
    }

    const normalize = arr => (arr || []).map(o => {
        if (!o || typeof o !== 'object') return o;
        const r = {};
        for (const [k, v] of Object.entries(o)) r[k[0].toLowerCase() + k.slice(1)] = v;
        return r;
    });

    function taskMeta(type) {
        const t = (type || '').toLowerCase();
        if (t.includes('executesql')) return { icon: 'sql', subtitle: 'Execute SQL Task' };
        if (t.includes('pipeline')) return { icon: 'transform', subtitle: 'Data Flow Task' };
        if (t.includes('executepackage')) return { icon: 'package', subtitle: 'Execute Package Task' };
        return { icon: 'task', subtitle: 'Control Flow Task' };
    }

    // ───────────────────────── object-mode graph ─────────────────────────
    function buildObjectElements(graph, filter) {
        let packages = normalize(graph.packages || graph.Packages);
        let tasks = normalize(graph.tasks || graph.Tasks);
        let components = normalize(graph.components || graph.Components);
        const executionEdges = normalize(graph.executionEdges || graph.ExecutionEdges);
        const dataFlowEdges = normalize(graph.dataFlowEdges || graph.DataFlowEdges);

        const pkgFilter = (filter && (filter.packageId || filter.PackageId)) || '';
        const taskFilter = (filter && (filter.taskId || filter.TaskId)) || '';

        if (pkgFilter) {
            packages = packages.filter(p => safe(p.id) === pkgFilter || safe(p.name) === pkgFilter);
            const pkgIds = new Set(packages.map(p => safe(p.id)));
            tasks = tasks.filter(t => pkgIds.has(safe(t.packageId)));
            const taskIds = new Set(tasks.map(t => safe(t.id)));
            components = components.filter(c => taskIds.has(safe(c.taskId)));
        }
        if (taskFilter) {
            tasks = tasks.filter(t => safe(t.id) === taskFilter || safe(t.name) === taskFilter);
            const tIds = new Set(tasks.map(t => safe(t.id)));
            components = components.filter(c => tIds.has(safe(c.taskId)));
            const pIds = new Set(tasks.map(t => safe(t.packageId)));
            packages = packages.filter(p => pIds.has(safe(p.id)));
        }

        components = components.filter(c => !safe(c.type).includes('Execute SQL'));

        // Start-package detection — match by filename (case-insensitive, extension-agnostic).
        // The node label is typically stored without the .dtsx extension (e.g. "Stage") while
        // startPackage is the full filename (e.g. "Stage.dtsx"), so strip the extension from
        // both sides before comparing.
        const stripExt = s => s.replace(/\.[^.]+$/, '');
        const startPkgRaw  = safe((filter && (filter.startPackage || filter.StartPackage)) || '').toLowerCase();
        const startPkgBase = stripExt(startPkgRaw);   // "Stage.dtsx" → "Stage"

        const ids = new Set();
        const nodes = [];
        const addNode = (id, label, kind, meta) => {
            if (!id || ids.has(id)) return;
            ids.add(id);
            nodes.push({
                data: {
                    id, kind, state: '', outCount: 0,
                    label: label || id, subtitle: meta.subtitle || '', meta: meta.meta || '',
                    icon: meta.icon, color: COLORS[meta.icon] || COLORS.task,
                    isStart: !!meta.isStart
                }
            });
        };

        packages.forEach(p => {
            const nameLower = safe(p.name).toLowerCase();
            const isStart = !!(startPkgRaw && (
                nameLower === startPkgRaw ||    // exact match  ("Stage.dtsx" === "Stage.dtsx")
                nameLower === startPkgBase ||   // label without ext ("Stage" === "Stage")
                stripExt(nameLower) === startPkgBase  // both stripped (any ext on either side)
            ));
            addNode(safe(p.id), safe(p.name), 'package', { icon: 'package', subtitle: 'SSIS Package', meta: fileName(p.path || p.Path), isStart });
        });
        tasks.forEach(t => {
            const tm = taskMeta(safe(t.type));
            tm.meta = safe(t.packageName) ? `in ${safe(t.packageName)}` : safe(t.description);
            addNode(safe(t.id), safe(t.name), 'task', tm);
        });
        components.forEach(c => {
            const ct = safe(c.type);
            const icon = ct.includes('Source') ? 'source' : ct.includes('Destination') ? 'destination' : ct.includes('Lookup') ? 'lookup' : 'transform';
            const meta = safe(c.sqlQueryOrTable) ? trunc(c.sqlQueryOrTable, 42) : safe(c.connectionManager);
            addNode(safe(c.id), safe(c.name), 'component', { icon, subtitle: ct || 'Component', meta });
        });

        const edgeMap = new Map();
        const addEdge = (s, t, rel) => {
            if (!s || !t || s === t || !ids.has(s) || !ids.has(t)) return;
            const key = `${rel}|${s}|${t}`;
            const e = edgeMap.get(key);
            if (e) e.data.count++;
            else edgeMap.set(key, { data: { id: key, source: s, target: t, rel, count: 1, cpd: '0 0', cpw: '0.5 0.5' } });
        };
        tasks.forEach(t => addEdge(safe(t.packageId), safe(t.id), 'contains'));
        components.forEach(c => addEdge(safe(c.taskId), safe(c.id), 'contains'));
        const pkgIdSet = new Set(packages.map(p => safe(p.id)));
        executionEdges.forEach(e => addEdge(safe(e.fromTaskId), safe(e.toTaskId), pkgIdSet.has(safe(e.toTaskId)) ? 'invokes' : 'execution'));
        dataFlowEdges.forEach(e => addEdge(safe(e.fromComponentId), safe(e.toComponentId), 'data'));

        const outCount = new Map();
        edgeMap.forEach(e => { if (e.data.rel !== 'contains') outCount.set(e.data.source, (outCount.get(e.data.source) || 0) + 1); });
        nodes.forEach(n => n.data.outCount = outCount.get(n.data.id) || 0);

        return nodes.concat(Array.from(edgeMap.values()));
    }

    // ───────────────────────── column-mode graph ─────────────────────────
    const COL_W = 200, ROW_NODE_H = 20, ROW_PITCH = 27, HDR_NODE_H = 28, HDR_PITCH = 38, PAD = 12, RANK_GAP = 180, TABLE_GAP = 70;

    function tableKey(schema, table, fallback) {
        const s = safe(schema), t = safe(table);
        const key = (s && t) ? `${s}.${t}` : (t || safe(fallback));
        return key;
    }

    function buildColumnElements(graph, filter) {
        // Focused search hit (lineage search) — the column or table to highlight with the
        // same amber border the entry package gets. Matched against the node's display label.
        const focus = safe(filter && (filter.focus || filter.Focus)).toLowerCase();
        const focusScope = safe(filter && (filter.focusScope || filter.FocusScope)).toLowerCase();

        const maps = normalize(graph.columnMappings || graph.ColumnMappings);
        const comps = normalize(graph.components || graph.Components);
        const compMap = new Map();
        comps.forEach(c => compMap.set(safe(c.id), { name: safe(c.name), type: safe(c.type) }));

        const tables = new Map(); // key -> { cols:Set, label, tip, kind }
        const edges = [];
        const edgeSeen = new Set();

        // Resolve a mapping side to a STABLE node identity. Data-flow components
        // (OLE DB Source/Destination) are keyed by their component id so the
        // SQL-proc view and the data-flow view of the same component reconcile
        // into one node; plain SQL assets are keyed by schema.table.
        const assetOf = (op, compId, schema, table, compName) => {
            compId = safe(compId); schema = safe(schema); table = safe(table); compName = safe(compName);
            const tbl = table ? (schema ? `${schema}.${table}` : table) : '';
            const isXml = op === 'XML_FALLBACK';
            const isComp = compId && compMap.has(compId) && (isXml || !tbl);
            if (isComp) {
                const c = compMap.get(compId) || {};
                return { key: 'c:' + compId, kind: 'comp', tableLabel: tbl, comp: c.name || compName || compId };
            }
            if (tbl) return { key: 't:' + tbl, kind: 'table', tableLabel: tbl, comp: '' };
            const k = compName || compId || '?';
            return { key: 'x:' + k, kind: 'other', tableLabel: k, comp: '' };
        };

        // For a component node the header shows its resolved SQL table/proc, and
        // the component name (e.g. "OLE DB Source") becomes the hover tooltip.
        const ensure = a => {
            let t = tables.get(a.key);
            if (!t) { t = { cols: new Set(), label: '', tip: '', kind: a.kind }; tables.set(a.key, t); }
            if (a.kind === 'comp') {
                if (a.tableLabel) t.label = a.tableLabel;
                else if (!t.label) t.label = a.comp;
                if (a.comp) t.tip = a.comp;
            } else if (!t.label) {
                t.label = a.tableLabel;
            }
            return t;
        };

        maps.forEach(m => {
            const sCol = safe(m.sourceColumnName), tCol = safe(m.targetColumnName);
            if (!sCol && !tCol) return;
            const op = safe(m.operationType);
            const s = assetOf(op, m.sourceComponentId, m.sourceSchema, m.sourceTable, m.sourceComponentName);
            const t = assetOf(op, m.targetComponentId, m.targetSchema, m.targetTable, m.targetComponentName);
            if (s.key && sCol) ensure(s).cols.add(sCol);
            if (t.key && tCol) ensure(t).cols.add(tCol);
            if (s.key && t.key && sCol && tCol && s.key !== t.key) {
                const sId = `${s.key}::${sCol}`, tId = `${t.key}::${tCol}`;
                const ek = `${sId}>${tId}`;
                if (!edgeSeen.has(ek)) { edgeSeen.add(ek); edges.push({ sId, tId, sKey: s.key, tKey: t.key }); }
            }
        });

        if (tables.size === 0) return { elements: [], empty: true };

        // Table-level adjacency for ranking.
        const tEdges = new Set();
        edges.forEach(e => tEdges.add(`${e.sKey}>${e.tKey}`));
        const adj = new Map(), indeg = new Map();
        tables.forEach((_, k) => { adj.set(k, []); indeg.set(k, 0); });
        tEdges.forEach(s => { const [a, b] = s.split('>'); adj.get(a).push(b); indeg.set(b, (indeg.get(b) || 0) + 1); });

        // Longest-path rank (cycle-safe).
        const rank = new Map();
        const seen = new Set();
        const visit = (k, depth) => {
            if (depth > tables.size + 1) return rank.get(k) || 0;
            const cur = rank.get(k) || 0;
            (adj.get(k) || []).forEach(n => {
                if (rank.get(n) === undefined || rank.get(n) < cur + 1) {
                    rank.set(n, cur + 1);
                    if (!seen.has(`${k}>${n}>${cur}`)) { seen.add(`${k}>${n}>${cur}`); visit(n, depth + 1); }
                }
            });
            return cur;
        };
        tables.forEach((_, k) => { if ((indeg.get(k) || 0) === 0) { rank.set(k, 0); } });
        if (![...rank.values()].length) tables.forEach((_, k) => rank.set(k, 0));
        tables.forEach((_, k) => { if (rank.get(k) === undefined) rank.set(k, 0); });
        tables.forEach((_, k) => visit(k, 0));

        // Group by rank, assign preset positions.
        const byRank = new Map();
        tables.forEach((_, k) => { const r = rank.get(k) || 0; if (!byRank.has(r)) byRank.set(r, []); byRank.get(r).push(k); });
        const CARD_W = COL_W + 2 * PAD;
        const elements = [];
        const LINE_H = 15;                          // approx line box at the row/header font
        const HDR_GAP = HDR_PITCH - HDR_NODE_H;     // gap below the header
        const ROW_GAP = ROW_PITCH - ROW_NODE_H;     // gap between rows
        const HDR_TEXT_W = COL_W - 16, COL_TEXT_W = COL_W - 18;

        Array.from(byRank.keys()).sort((a, b) => a - b).forEach(r => {
            const x = r * (CARD_W + RANK_GAP) + CARD_W / 2;
            let y = 0;
            byRank.get(r).sort().forEach(key => {
                const tbl = tables.get(key);
                const cols = Array.from(tbl.cols).sort();
                const hdrLabel = tbl.label || key;
                const top = y;
                // parent container — focus-highlight when a table search hit matches it
                const tableFocus = !!focus && focusScope === 'table' && hdrLabel.toLowerCase() === focus;
                elements.push({ data: { id: key, ckind: 'table', label: hdrLabel, focus: tableFocus } });

                // header child — wrap and grow height to fit the full table name
                const hw = wrapLabel(hdrLabel, 12, 700, HDR_TEXT_W, 3);
                const hdrH = Math.max(HDR_NODE_H, hw.lines * LINE_H + 12);
                elements.push({
                    data: { id: `${key}::__hdr`, parent: key, ckind: 'hdr', label: hw.text, tip: tbl.tip || '', h: hdrH },
                    position: { x, y: top + hdrH / 2 }, grabbable: false, selectable: false
                });

                // column rows — variable height per wrapped label, stacked by accumulated y
                let rowY = top + hdrH + HDR_GAP;
                cols.forEach(c => {
                    const cw = wrapLabel(c, 11, 400, COL_TEXT_W, 4);
                    const rowH = Math.max(ROW_NODE_H, cw.lines * LINE_H + 6);
                    const tip = `${hdrLabel}.${c}`;
                    const colFocus = !!focus && focusScope === 'column' && tip.toLowerCase() === focus;
                    elements.push({
                        data: { id: `${key}::${c}`, parent: key, ckind: 'col', label: cw.text, table: key, tip, h: rowH, focus: colFocus },
                        position: { x, y: rowY + rowH / 2 }
                    });
                    rowY += rowH + ROW_GAP;
                });

                y = rowY + TABLE_GAP;
            });
        });

        edges.forEach(e => elements.push({ data: { id: `ce:${e.sId}>${e.tId}`, source: e.sId, target: e.tId, cpd: '0 0', cpw: '0.5 0.5' }, classes: 'celink' }));
        return { elements, empty: false };
    }

    // ───────────────────────── styles ─────────────────────────
    function stylesheet(isDark) {
        const text = isDark ? '#f1f5f9' : '#1e293b';
        const tblBg = isDark ? '#1e293b' : '#ffffff';
        const tblBorder = isDark ? '#3b4a61' : '#d8dee9';
        const rowBg = isDark ? '#0f1d33' : '#f1f5f9';
        const rowText = isDark ? '#cbd5e1' : '#334155';
        const hdrBg = isDark ? '#2563eb' : '#dbeafe';
        const hdrText = isDark ? '#ffffff' : '#1e3a8a';
        const celink = isDark ? '#64748b' : '#94a3b8';
        return [
            // object-mode card nodes are invisible canvas boxes; the HTML card draws them
            { selector: 'node[kind]', style: { 'width': 290, 'height': 104, 'shape': 'round-rectangle', 'background-opacity': 0, 'border-width': 0 } },

            // column-mode table containers + rows
            { selector: 'node[ckind="table"]', style: { 'shape': 'round-rectangle', 'background-color': tblBg, 'border-color': tblBorder, 'border-width': 1, 'padding': PAD, 'background-opacity': 1 } },
            { selector: 'node[ckind="hdr"]', style: { 'shape': 'round-rectangle', 'width': COL_W, 'height': 'data(h)', 'background-color': hdrBg, 'border-width': 0, 'label': 'data(label)', 'color': hdrText, 'font-size': 12, 'font-weight': 700, 'text-valign': 'center', 'text-halign': 'center', 'text-max-width': COL_W - 16, 'text-wrap': 'wrap', 'text-justification': 'center' } },
            { selector: 'node[ckind="col"]', style: { 'shape': 'round-rectangle', 'width': COL_W, 'height': 'data(h)', 'background-color': rowBg, 'border-width': 0, 'label': 'data(label)', 'color': rowText, 'font-size': 11, 'text-valign': 'center', 'text-halign': 'center', 'text-max-width': COL_W - 18, 'text-wrap': 'wrap', 'text-justification': 'center' } },

            // edges leave each node horizontally from a single side-anchor, round the
            // corner, and run straight when source and target share a height.
            {
                selector: 'edge', style: {
                    'width': 2, 'curve-style': 'round-taxi',
                    'taxi-direction': 'horizontal', 'taxi-turn': '50%',
                    'taxi-turn-min-distance': '14px', 'taxi-radius': 16,
                    'source-endpoint': '50% 0%', 'target-endpoint': '-50% 0%',
                    'target-arrow-shape': 'triangle', 'arrow-scale': 1
                }
            },
            { selector: 'edge[rel="contains"]', style: { 'line-color': '#94a3b8', 'line-style': 'dashed', 'width': 1, 'target-arrow-shape': 'none', 'opacity': 0.45 } },
            { selector: 'edge[rel="execution"]', style: { 'line-color': '#38bdf8', 'target-arrow-color': '#38bdf8' } },
            { selector: 'edge[rel="invokes"]', style: { 'line-color': '#a78bfa', 'target-arrow-color': '#a78bfa', 'line-style': 'dashed' } },
            { selector: 'edge[rel="data"]', style: { 'line-color': '#22c55e', 'target-arrow-color': '#22c55e', 'width': 3 } },
            { selector: 'edge.celink', style: { 'line-color': celink, 'target-arrow-color': celink, 'width': 1.4, 'arrow-scale': 0.7, 'opacity': 0.45 } },
            {
                selector: 'edge[count > 1]', style: {
                    'label': 'data(count)', 'font-size': 10, 'color': '#fff', 'font-weight': 700,
                    'text-background-color': '#334155', 'text-background-opacity': 1, 'text-background-padding': 3, 'text-background-shape': 'roundrectangle'
                }
            },
            // start package — amber canvas border (supplements the HTML card badge)
            { selector: 'node.start-pkg', style: { 'border-width': 3, 'border-color': '#f59e0b', 'border-opacity': 0.9 } },
            // object highlight (edges only — cards dim via html data flag)
            { selector: 'edge.faded', style: { 'opacity': 0.08 } },
            { selector: 'edge.hl', style: { 'width': 4, 'opacity': 1 } },
            // column path highlight
            { selector: '.cdim', style: { 'opacity': 0.1 } },
            { selector: '.cpath', style: { 'opacity': 1 } },
            { selector: 'edge.cpath', style: { 'line-color': '#38bdf8', 'target-arrow-color': '#38bdf8', 'width': 3, 'opacity': 1 } },
            { selector: 'node[ckind="col"].cpath', style: { 'border-width': 2, 'border-color': '#38bdf8' } },
            // searched column/table — amber border matching the entry-package highlight
            { selector: 'node[ckind="table"][?focus]', style: { 'border-width': 3, 'border-color': '#f59e0b', 'border-opacity': 0.9 } },
            { selector: 'node[ckind="col"][?focus]', style: { 'border-width': 2.5, 'border-color': '#f59e0b', 'border-opacity': 1 } }
        ];
    }

    function cardTpl(d) {
        const stateClass = d.state === 'hl' ? 'cy-hl' : d.state === 'dim' ? 'cy-dim' : '';
        const startClass = d.isStart ? ' cy-start' : '';
        const entryBadge = d.isStart ? '<span class="cy-entry-badge">&#x25B6; Entry Point</span>' : '';
        const badge = d.outCount > 0 ? `<span class="cy-badge" title="${d.outCount} downstream">${d.outCount}</span>` : '';
        const meta = d.meta ? `<div class="cy-meta" title="${esc(d.meta)}">${esc(d.meta)}</div>` : '';
        return `<div class="cy-card ${stateClass}${startClass}">
            ${entryBadge}<div class="cy-head">
                <div class="cy-ico" style="color:${d.color}">${iconSvg(d.icon)}</div>
                <div class="cy-htext">
                    <div class="cy-title" title="${esc(d.label)}">${esc(d.label)}</div>
                    <div class="cy-type">${esc(d.subtitle)}</div>
                </div>${badge}
            </div>${meta}
            <div class="cy-foot"><span class="cy-fic">${footSvg('lineage')}</span><span class="cy-fic">${footSvg('details')}</span></div>
        </div>`;
    }

    function highlightObject(node) {
        const nhood = node.closedNeighborhood();
        const keep = new Set(nhood.nodes().map(n => n.id()));
        cy.nodes().forEach(n => n.data('state', keep.has(n.id()) ? 'hl' : 'dim'));
        cy.edges().forEach(e => { const inside = nhood.contains(e); e.toggleClass('hl', inside); e.toggleClass('faded', !inside); });
    }

    function highlightColumnPath(node) {
        const path = node.predecessors().union(node.successors()).union(node);
        const keep = new Set(path.map(el => el.id()));
        cy.elements().forEach(el => {
            // keep table containers visible; dim only rows/edges outside the path
            if (el.isNode() && el.data('ckind') === 'table') { el.removeClass('cdim'); return; }
            const on = keep.has(el.id());
            el.toggleClass('cpath', on);
            el.toggleClass('cdim', !on);
        });
    }

    function clearHighlight() {
        if (!cy) return;
        cy.nodes().forEach(n => n.data('state', ''));
        cy.elements().removeClass('faded').removeClass('hl').removeClass('cdim').removeClass('cpath');
    }

    function addLegend(container, isDark, currentMode) {
        const legend = document.createElement('div');
        legend.className = 'cy-legend' + (isDark ? ' cy-dark' : '');
        legend.innerHTML = currentMode === 'column'
            ? '<span class="lg lg-col">Click a column to trace its full lineage path</span>'
            : '<span class="lg lg-exec">Execution</span><span class="lg lg-data">Data flow</span><span class="lg lg-invokes">Invokes</span><span class="lg lg-contains">Contains</span>';
        container.appendChild(legend);
    }

    function render(elementId, graph, filter, ref, isDarkArg, graphMode) {
        dotNetRef = ref;
        isDark = !!isDarkArg;   // store in module scope for use by exportPng / toggleFullscreen
        mode = graphMode === 'column' ? 'column' : 'object';
        const container = document.getElementById(elementId);
        if (!container || !graph) return;
        if (cy) { try { cy.destroy(); } catch (e) { /* ignore */ } cy = null; }
        container.innerHTML = '';
        container.classList.toggle('cy-dark', isDark);

        let elements, empty = false;
        if (mode === 'column') { const r = buildColumnElements(graph, filter); elements = r.elements; empty = r.empty; }
        else { elements = buildObjectElements(graph, filter); }

        if (empty) {
            container.innerHTML = '<div style="padding:24px;color:#94a3b8">No column-level mappings were extracted. Enable “enrich data-flow SQL procedures”, or use Objects view.</div>';
            return;
        }
        if (!elements || elements.length === 0) {
            container.innerHTML = '<div style="padding:24px;color:#fbbf24">No graph nodes for the current filter.</div>';
            return;
        }

        const isLarge = elements.length > 200;
        cy = cytoscape({
            container, elements, style: stylesheet(isDark), wheelSensitivity: 0.2,  // isDark is module-level
            // Performance hints for large graphs — hide edges while panning/zooming,
            // use a texture snapshot during motion instead of re-rendering every frame.
            hideEdgesOnViewport: isLarge,
            textureOnViewport: isLarge,
            pixelRatio: isLarge ? 1 : (window.devicePixelRatio || 1),
            layout: mode === 'column'
                ? { name: 'preset', fit: true, padding: 40 }
                : { name: 'dagre', rankDir: 'LR', nodeSep: 28, rankSep: 120, edgeSep: 12 }
        });

        if (mode === 'object' && typeof cy.nodeHtmlLabel === 'function') {
            cy.nodeHtmlLabel([{ query: 'node[kind]', halign: 'center', valign: 'center', halignBox: 'center', valignBox: 'center', tpl: cardTpl }]);
            // Mark start-package node with a class so the canvas border style applies
            cy.nodes('[kind="package"]').forEach(n => { if (n.data('isStart')) n.addClass('start-pkg'); });
        }

        cy.on('tap', 'node', evt => {
            const n = evt.target, d = n.data();
            if (mode === 'column') {
                if (d.ckind === 'col') {
                    highlightColumnPath(n);
                    if (columnClickHandler) columnClickHandler(d.tip || d.label);
                }
                return;
            }
            highlightObject(n);
            if (dotNetRef) dotNetRef.invokeMethodAsync('OnGraphNodeClick', d.id, d.kind);
        });
        cy.on('tap', 'edge', evt => {
            if (mode === 'column') return;
            const d = evt.target.data();
            if (dotNetRef) dotNetRef.invokeMethodAsync('OnGraphEdgeClick', d.source, d.target, d.rel);
        });
        cy.on('tap', evt => { if (evt.target === cy) clearHighlight(); });

        if (mode === 'column') {
            const tip = document.createElement('div');
            tip.className = 'cy-tip';
            tip.style.display = 'none';
            container.appendChild(tip);
            cy.on('mouseover', 'node[ckind="hdr"], node[ckind="col"]', evt => {
                const d = evt.target.data();
                if (!d.tip) return;
                tip.textContent = d.tip;
                tip.style.display = 'block';
            });
            cy.on('mousemove', evt => {
                if (tip.style.display === 'block' && evt.renderedPosition) {
                    tip.style.left = (evt.renderedPosition.x + 14) + 'px';
                    tip.style.top = (evt.renderedPosition.y + 14) + 'px';
                }
            });
            cy.on('mouseout', 'node[ckind="hdr"], node[ckind="col"]', () => { tip.style.display = 'none'; });
        }

        addLegend(container, isDark, mode);  // isDark is module-level
        cy.ready(() => { snapshotHome(); cy.fit(undefined, 48); });
    }

    function fit() { if (cy) cy.fit(undefined, 48); }

    // Capture each leaf node's position straight after layout so a later "Reset" can
    // undo manual drags. Compound parents (table containers) auto-fit around their
    // children, so they are skipped and restored implicitly.
    function snapshotHome() {
        if (!cy) return;
        homePositions = {};
        cy.nodes().forEach(n => {
            if (typeof n.isParent === 'function' && n.isParent()) return;
            const p = n.position();
            homePositions[n.id()] = { x: p.x, y: p.y };
        });
    }

    // Restore the original (post-layout) node positions and re-fit. Works for both the
    // object data-flow diagram (dagre positions) and the column diagram (preset positions).
    function resetLayout() {
        if (!cy) return;
        if (homePositions) {
            cy.batch(() => {
                cy.nodes().forEach(n => {
                    const p = homePositions[n.id()];
                    if (p) n.position({ x: p.x, y: p.y });
                });
            });
        }
        cy.fit(undefined, 48);
    }

    function exportPng(filename) {
        if (!cy) return;

        // Object-mode card nodes are HTML overlays rendered on top of an invisible
        // Cytoscape canvas node.  cy.png() only captures the canvas — temporarily
        // apply native Cytoscape node styles so every node is visible in the export,
        // then restore the invisible-node stylesheet immediately after.
        if (mode === 'object') {
            cy.nodes('[kind]').forEach(n => {
                const isStart = n.data('isStart');
                n.style({
                    'background-opacity': 1,
                    'background-color': isStart ? '#d97706' : (n.data('color') || '#475569'),
                    'border-width': isStart ? 3 : 1,
                    'border-color': isStart ? '#f59e0b' : (isDark ? '#475569' : '#94a3b8'),
                    'label': n.data('label'),
                    'color': '#ffffff',
                    'font-size': 12,
                    'font-weight': 600,
                    'text-valign': 'center',
                    'text-halign': 'center',
                    'text-wrap': 'ellipsis',
                    'text-max-width': 260
                });
            });
        }

        // Cap scale so neither output dimension exceeds ~8 000 px.
        // WebView2 / Chromium silently returns a blank canvas when the PNG would
        // exceed the ~16 384 px per-side limit; staying under 8 000 px adds headroom.
        const MAX_DIM = 8000;
        const bb = cy.elements().boundingBox();
        let scale = 2;
        if (bb.w > 0 && bb.h > 0) {
            const maxScale = Math.min(MAX_DIM / bb.w, MAX_DIM / bb.h);
            if (maxScale < scale) {
                // round down to nearest 0.25 step, minimum 0.25
                scale = Math.max(0.25, Math.floor(maxScale * 4) / 4);
            }
        }

        const bgColor = isDark ? '#0b1120' : '#eef1f5';
        const tryExport = s => {
            const uri = cy.png({ output: 'base64uri', bg: bgColor, full: true, scale: s });
            // A valid PNG data URI is always several hundred characters long;
            // a failed/blank canvas returns "data:," or a very short string.
            return (uri && uri.length > 200) ? uri : null;
        };

        const dataUri = tryExport(scale)
            ?? tryExport(Math.max(0.25, scale / 2))   // one step down
            ?? tryExport(0.25)                          // last-resort (quarter resolution)
            ?? null;

        // Restore transparent-card stylesheet so the live graph is unchanged
        if (mode === 'object') {
            cy.nodes('[kind]').forEach(n => {
                n.removeStyle('background-opacity background-color border-width border-color label color font-size font-weight text-valign text-halign text-wrap text-max-width');
            });
        }

        if (!dataUri) {
            alert(
                'PNG export failed — the diagram is too large to capture as a single image.\n\n' +
                'Try applying a Package or Task filter to reduce the graph size, then export again.'
            );
            return;
        }

        const a = document.createElement('a');
        a.href = dataUri;
        a.download = filename || 'ssis-lineage-diagram.png';
        a.click();
    }

    // Track fullscreen state with a class rather than relying on the :fullscreen
    // CSS pseudo-class, which Blazor's scoped-CSS transformer and some WebView2
    // builds do not handle reliably.
    function _applyFsClass() {
        const fsEl = document.fullscreenElement
            || document.webkitFullscreenElement
            || document.mozFullScreenElement;
        // Remove from all tracked elements first
        document.querySelectorAll('.is-fullscreen').forEach(e => e.classList.remove('is-fullscreen'));
        if (fsEl) fsEl.classList.add('is-fullscreen');
    }
    // One-time listener wires up class maintenance for every fullscreen change
    // (including Esc-key exit, which bypasses toggleFullscreen).
    if (!window._fsListenerAdded) {
        window._fsListenerAdded = true;
        ['fullscreenchange', 'webkitfullscreenchange', 'mozfullscreenchange'].forEach(ev =>
            document.addEventListener(ev, _applyFsClass));
    }

    function toggleFullscreen(containerId) {
        const el = document.getElementById(containerId);
        if (!el) return;
        const isFs = document.fullscreenElement
            || document.webkitFullscreenElement
            || document.mozFullScreenElement;
        if (!isFs) {
            const req = el.requestFullscreen || el.webkitRequestFullscreen || el.mozRequestFullScreen;
            if (req) req.call(el);
        } else {
            const exit = document.exitFullscreen || document.webkitExitFullscreen || document.mozCancelFullScreen;
            if (exit) exit.call(document);
        }
        // Re-fit after fullscreen transition so the graph fills the new size
        setTimeout(() => { if (cy) cy.fit(undefined, 48); }, 400);
    }

    function locate(id) {
        if (!cy) return;
        const n = cy.getElementById(id);
        if (n && n.length) {
            if (mode === 'object') highlightObject(n);
            cy.animate({ fit: { eles: n.closedNeighborhood(), padding: 90 } }, { duration: 300 });
        }
    }

    function setColumnClickHandler(fn) { columnClickHandler = typeof fn === 'function' ? fn : null; }

    return { render, fit, resetLayout, locate, clearHighlight, exportPng, toggleFullscreen, setColumnClickHandler };
})();
