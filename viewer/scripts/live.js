// ================================================================
// live.js  --  live mode: the Pluto MCP server drives this viewer.
//
// Only active as index.html?live=1, served by the MCP server (scripts\mcp\ViewerHost.cs) at
// http://localhost:<port>/viewer/. A WebSocket to ws://<host>/live carries commands from the LLM's
// viewer_* tools, { id, cmd, args }, and this answers { id, ok, result | error }; it reconnects when
// the server restarts. Commands drive the viewer the way a user would (the dropdowns, Find, the groups
// switch) through the globals of viewer.js, so live mode adds no second render path:
//   load      { bin, features?, name }   the .bin is read through HTTP Range slices (never whole)
//   show      { component?, case?, beam_component?, deformed? }
//   highlight { items: [group items], only }   painted through FEAFeatures (tag "pluto-live")
//   unhighlight
//   isolate   { shells: [ids] | null, beams: [ids] | null }   null both = show everything
//   focus     { kind: elem | node, id }
//   state     -> model, components, cases, current view, pinned readout, groups
// Without ?live this file does nothing.
// ================================================================

var FEALive = (function () {
    var q = new URLSearchParams(window.location.search);
    if (!q.get('live')) return null;

    var ws = null, retry = 0;
    var LIVE_TAG = 'pluto-live';
    var savedHidden = null;     // other groups' hidden flags before a highlight with only = true

    function say(m) { if (typeof log === 'function') log(m); }

    // ---- a Blob-like file read through HTTP Range ------------------------
    // The format readers only call file.size and file.slice(a, b).arrayBuffer().
    async function remoteFile(url, name) {
        var head = await fetch(url, { method: 'HEAD' });
        if (!head.ok) throw new Error(head.status + ' ' + head.statusText + ' for ' + url);
        var size = parseInt(head.headers.get('Content-Length'), 10);
        if (!(size >= 0)) throw new Error('no Content-Length for ' + url);
        function slice(a, b) {
            a = Math.max(0, a || 0); b = Math.min(size, b === undefined ? size : b);
            return {
                size: Math.max(0, b - a),
                arrayBuffer: async function () {
                    if (b <= a) return new ArrayBuffer(0);
                    var r = await fetch(url, { headers: { Range: 'bytes=' + a + '-' + (b - 1) } });
                    if (!r.ok) throw new Error(r.status + ' ' + r.statusText + ' reading ' + url);
                    var buf = await r.arrayBuffer();
                    // a server that ignores Range sends everything: cut it here
                    return (r.status === 200 && buf.byteLength !== b - a) ? buf.slice(a, b) : buf;
                }
            };
        }
        return { name: name, size: size, slice: slice, arrayBuffer: function () { return slice(0, size).arrayBuffer(); } };
    }

    // ---- state -----------------------------------------------------------
    function optionTexts(sel) {
        return Array.prototype.map.call(sel ? sel.options : [], function (o) { return o.textContent; });
    }
    function selectedText(sel) {
        return sel && sel.selectedIndex >= 0 ? sel.options[sel.selectedIndex].textContent : null;
    }
    function text(el) { return el ? (el.textContent || '').trim() : null; }

    function state() {
        var s = {
            model: window.feaPrimaryName || null,
            loaded: !!feaModel,
            components: optionTexts(elComp),
            component: selectedText(elComp),
            cases: optionTexts(elLC).map(function (t) { return t.replace(/\s+\[env\]$/, ''); }),
            case: selectedText(elLC),
            caption: text(elViewCaption),
            deformed: !!(elDefEnable && elDefEnable.checked),
            pinned: !!pinned,
            readout: {
                mode: text(elRoMode), value: text(elRoValue), component: text(elRoComp),
                element: text(elRoElem), node: text(elRoNode), position: text(elRoPos),
                controlling: text(elRoControlling)
            },
            status: text(elStatus)
        };
        var bc = document.getElementById('beamComp');
        if (bc) { s.beamComponents = optionTexts(bc); s.beamComponent = selectedText(bc); }
        var env = window.FEAFeatures && FEAFeatures.envelope();
        s.groups = env && env.groups && env.groups.items ? env.groups.items.map(function (g) {
            return { name: g.name, shown: g.hidden === false, live: (g.tags || []).indexOf(LIVE_TAG) >= 0 };
        }) : [];
        s.groupsOn = !!(window.FEAFeatures && FEAFeatures.isOn());
        return s;
    }

    // ---- commands --------------------------------------------------------
    function waitFor(cond, ms) {
        return new Promise(function (res, rej) {
            var t0 = performance.now();
            (function tick() {
                if (cond()) { res(); return; }
                if (performance.now() - t0 > ms) { rej(new Error('timed out')); return; }
                setTimeout(tick, 50);
            })();
        });
    }

    function pickOption(sel, want, what) {
        if (!sel) throw new Error('no ' + what + ' list');
        var w = String(want).toLowerCase(), hit = -1;
        for (var i = 0; i < sel.options.length; i++) {
            var t = sel.options[i].textContent.replace(/\s+\[env\]$/, '').trim().toLowerCase();
            if (t === w) { hit = i; break; }
            if (hit < 0 && t.indexOf(w) === 0) hit = i;
        }
        if (hit < 0) throw new Error(what + ' "' + want + '" is not one of: ' + optionTexts(sel).join(', '));
        sel.selectedIndex = hit;
        sel.dispatchEvent(new Event('change'));
        return sel.options[hit].textContent;
    }

    var commands = {
        state: async function () { return state(); },

        load: async function (a) {
            var f = await remoteFile(a.bin, a.name || 'model');
            await loadModels([{ file: f, name: a.name || 'model' }]);
            if (a.features && window.FEAFeatures) {
                var r = await fetch(a.features);
                if (!r.ok) throw new Error(r.status + ' for ' + a.features);
                var blob = await r.blob();
                await FEAFeatures.loadFile(new File([blob], (a.name || 'model') + '.features.json', { type: 'application/json' }));
            }
            savedHidden = null;
            return state();
        },

        show: async function (a) {
            if (!feaModel) throw new Error('no model loaded (viewer_load)');
            if (a.component != null) {
                if (elKind && elKind.value !== 'all') { elKind.value = 'all'; elKind.dispatchEvent(new Event('change')); }
                pickOption(elComp, a.component, 'component');
            }
            if (a['case'] != null) pickOption(elLC, a['case'], 'load case');
            if (a.beam_component != null) pickOption(document.getElementById('beamComp'), a.beam_component, 'beam component');
            if (a.deformed != null && elDefEnable && elDefEnable.checked !== !!a.deformed) elDefEnable.click();
            await new Promise(function (r) { setTimeout(r, 150); });   // selectLC slices the LC asynchronously
            return state();
        },

        highlight: async function (a) {
            if (!feaModel || !window.FEAFeatures) throw new Error('no model loaded (viewer_load)');
            var env = FEAFeatures.ensureEnvelope();
            env.groups = env.groups || { items: [] };
            env.groups.items = (env.groups.items || []).filter(function (g) { return (g.tags || []).indexOf(LIVE_TAG) < 0; });
            if (a.only) {
                if (!savedHidden) {
                    savedHidden = {};
                    env.groups.items.forEach(function (g) { savedHidden[g.id || g.name] = g.hidden; });
                }
                env.groups.items.forEach(function (g) { g.hidden = true; });
            }
            (a.items || []).forEach(function (g) {
                g.tags = (g.tags || []).concat([LIVE_TAG]);
                g.hidden = false;
                env.groups.items.push(g);
            });
            FEAFeatures.setEnvelope(env, FEAFeatures.fileName() || 'live.features.json');
            if (!FEAFeatures.isOn()) { var t = document.getElementById('featGroups'); if (t) { t.checked = true; t.dispatchEvent(new Event('change')); } }
            return state();
        },

        unhighlight: async function () {
            if (!window.FEAFeatures || !FEAFeatures.envelope()) return state();
            var env = FEAFeatures.envelope();
            env.groups.items = (env.groups.items || []).filter(function (g) { return (g.tags || []).indexOf(LIVE_TAG) < 0; });
            if (savedHidden) env.groups.items.forEach(function (g) { var k = g.id || g.name; if (k in savedHidden) g.hidden = savedHidden[k]; });
            savedHidden = null;
            FEAFeatures.setEnvelope(env, FEAFeatures.fileName());
            return state();
        },

        // Element visibility, the same attribute the section-cut isolate writes.
        isolate: async function (a) {
            if (!feaModel || !feaBuild || !mesh) throw new Error('no model loaded (viewer_load)');
            var all = !a.shells && !a.beams;
            var nElem = feaBuild.elemNCount.length, keep = null, kept = 0;
            if (!all) {
                keep = new Uint8Array(nElem);
                ensureIdMaps();
                (a.shells || []).forEach(function (id) { var e = elemIdMap.get(id); if (e !== undefined && e < nElem) { keep[e] = 1; kept++; } });
            }
            var attr = mesh.geometry.getAttribute('elemVis'), arr = attr.array;
            var edge = feaEdges ? feaEdges.geometry.getAttribute('elemVis') : null, eArr = edge ? edge.array : null;
            var vp = 0, ep = 0;
            for (var e = 0; e < nElem; e++) {
                var nc = feaBuild.elemNCount[e], vis = keep ? keep[e] : 1, nv = nc === 4 ? 6 : 3;
                for (var i = 0; i < nv; i++) arr[vp++] = vis;
                if (eArr) for (var k = 0; k < nc * 2; k++) eArr[ep++] = vis;
            }
            attr.needsUpdate = true;
            if (edge) edge.needsUpdate = true;
            var beamsKept = 0;
            if (window.FEABeams && FEABeams.build() && FEABeams.view()) {
                var b = FEABeams.build(), v = FEABeams.view(), bAttr = b.geometry.getAttribute('elemVis');
                var want = all ? null : new Set(a.beams || []);
                for (var j = 0; j < b.nElem; j++) {
                    var on = all || want.has(v.elemIds[j]) ? 1 : 0;
                    if (on && !all) beamsKept++;
                    bAttr.array.fill(on, b.vertStart[j], b.vertStart[j] + b.vertCount[j]);
                }
                bAttr.needsUpdate = true;
            }
            requestRender();
            var s = state();
            s.isolated = all ? null : { shells: kept, beams: beamsKept };
            return s;
        },

        focus: async function (a) {
            if (!feaModel) throw new Error('no model loaded (viewer_load)');
            elFindKind.value = a.kind === 'node' ? 'node' : 'elem';
            elFindId.value = String(a.id);
            doFind();
            return state();
        }
    };

    // ---- socket ------------------------------------------------------------
    function connect() {
        var url = (location.protocol === 'https:' ? 'wss://' : 'ws://') + location.host + '/live';
        ws = new WebSocket(url);
        ws.onopen = function () {
            retry = 0;
            say('Live: connected to the Pluto server.');
            ws.send(JSON.stringify({ event: 'hello', state: state() }));
        };
        ws.onmessage = async function (ev) {
            var m;
            try { m = JSON.parse(ev.data); } catch (e) { return; }
            var reply = { id: m.id };
            try {
                var fn = commands[m.cmd];
                if (!fn) throw new Error('unknown command ' + m.cmd);
                reply.result = await fn(m.args || {});
                reply.ok = true;
            } catch (err) {
                reply.ok = false;
                reply.error = (err && err.message) || String(err);
            }
            if (ws && ws.readyState === 1) ws.send(JSON.stringify(reply));
        };
        ws.onclose = function () {
            ws = null;
            say('Live: disconnected; retrying.');
            setTimeout(connect, Math.min(10000, 1000 * Math.pow(2, retry++)));
        };
    }
    connect();

    return { state: state, _commands: commands, _remoteFile: remoteFile };
})();
