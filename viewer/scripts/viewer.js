// ================================================================
// viewer.js  --  raw FEA per-corner field viewer.
//
// Loads a fixed-width binary via the file picker, builds a
// duplicate-vertex mesh, recolors by component / load case, and
// reports exact field values on hover (live) or pin (click).
//
// Views: primary LCs, min/max/abs(max) envelopes, Global DSR
// (worst check across LCs+checks, value or controlling-check map),
// GPU-side deformed shape with +/- cycling animation.
// ================================================================

var scene, camera, renderer, controls;
var perspCam = null, orthoCam = null;
var mesh = null;

var feaModel = null;        // PRIMARY parsed binary model (geometry source)
var feaSet = null;          // FEAModelSet: all loaded models, global LC index
var feaBuild = null;        // geometry build result
var feaLCData = null;       // resident Float32Array of the current LC
var currentLC = 0;
var currentComp = 0;        // regular component (kept while a design strength shows)
var currentStr = -1;        // >= 0: a design-strength component is displayed
var feaMaterial = null;
var feaEdges = null;
var feaEdgeMaterial = null;
var feaMarker = null;
var flashMesh = null;       // alarm-region x-ray overlay (shares mesh geometry)
var colormapName = 'turbo';
var autoRange = true;
var vMin = 0, vMax = 1;
var feaRaycaster = new THREE.Raycaster();
var pinned = false;
var pointerDown = null;
var lastQuery = null;       // last FEAQuery result (for the calc card)

var texScalar = null;       // 256x1 LUT for the active colormap
var texCat = null;          // discrete LUT for the controlling-check map

// In-memory envelope buffers (NaN-skipping folds of the primary LCs).
// envBufs = { min, max, abs, minLC, maxLC, absLC } -- the *LC arrays
// track which LC produced each winning slot value (null if the model
// was too big to afford them).
var envBufs = null;
var envGlobalDsrValue = null;   // Float32Array[nElements * maxCorners]
var envGlobalDsrSource = null;  // Uint16Array  -- winning DSR component
var envGlobalDsrLC = null;      // Uint16Array  -- winning LC
var currentEnvelope = null;     // 'min' | 'max' | 'abs' | 'dsr' | null
var dsrCategorical = false;     // Global DSR shown as controlling-check map
var dsrIndices = [];            // meta indices of kind:'dsr' components
var smoothing = true;           // any smoothing on (default on)
var smoothMode = 'elem';        // 'elem' = element means at nodes (default since 2026-09-10) | 'node' = coincident-node averaging
var absValue = false;           // display |value| (shader-side, post-interp)

var alarmEnabled = false;       // flag values >= alarmThreshold in alarmColor
var alarmThreshold = 1.0;
var alarmColor = [255, 0, 255]; // magenta -- distinct from every colormap's max
var alarmAutoOn = false;        // alarm was auto-enabled by entering a DSR view
var alarmPrev = null;           // { enabled, threshold } to restore on leaving
var dsrAlarmOptOut = false;     // user unchecked alarm inside a DSR view
var flashUntil = 0;             // performance.now() ms; flash pulses until then
var FLASH_MS = 1800;            // three pulses

// Deformed-shape state. dispScale uniform = scale (static) or
// scale * sin(2*pi*speed*t) when animating -- cycles +disp -> -disp.
var deform = { enabled: false, scale: 100, animating: false, speed: 0.5 };
var deformMaxDisp = 0;          // largest |disp| in current LC (for auto-scale)
var deformLCLoaded = -1;        // which LC the dispVec attribute holds
var deformScaleAuto = true;     // scale untouched by the user -> compute on enable

var viewCube = null;            // top-right orientation gizmo (viewCube.js)
var focusOrb = null;            // red sphere at controls.target
var focusOrbHideAt = 0;         // performance.now() ms; orb visible until then
var focusTween = null;          // { from, to, t0, duration }
var orbiting = false;           // true while OrbitControls is being dragged

var highlightObj = null;        // find-flash element/node marker (Group)
var highlightMats = null;       // its materials, for opacity pulsing
var highlightStart = 0;         // performance.now() ms
var HIGHLIGHT_MS = 2200;        // ~3 pulses, then auto-removed
var elemIdMap = null, nodeIdMap = null;   // real ID -> index (built lazily)

// Render-on-demand: animate() always ticks for input + tweens, but only
// renders when something visual actually changed. Saves a lot of GPU on
// large models that are sitting still. Continuous states (deform
// animation, flash pulse) force a render every frame while active.
var needsRender = true;
var prevOrbVisible = false;
function requestRender() { needsRender = true; }

// ---- DOM refs ----------------------------------------------------
var elFile      = document.getElementById('feaFile');
var elBtnDemo   = document.getElementById('btnDemo');
var elBtnFit    = document.getElementById('btnFit');
var elOrtho     = document.getElementById('feaOrtho');
var elZUp       = document.getElementById('feaZUp');
var elFindKind  = document.getElementById('findKind');
var elFindId    = document.getElementById('findId');
var elBtnFind   = document.getElementById('btnFind');
var elBtnComputeEnv  = document.getElementById('btnComputeEnv');
var elEnvComputeRow  = document.getElementById('envComputeRow');
var elEnvButtonsRow  = document.getElementById('envButtonsRow');
var elEnvDsrRow      = document.getElementById('envDsrRow');
var elBtnEnvMin = document.getElementById('btnEnvMin');
var elBtnEnvMax = document.getElementById('btnEnvMax');
var elBtnEnvAbs = document.getElementById('btnEnvAbs');
var elBtnEnvDsr = document.getElementById('btnEnvDsr');
var elBtnEnvCat = document.getElementById('btnEnvCat');
var elLC        = document.getElementById('feaLC');
var elBtnLCPrev = document.getElementById('btnLCPrev');
var elBtnLCNext = document.getElementById('btnLCNext');
var elKind      = document.getElementById('feaKind');
var elComp      = document.getElementById('feaComp');
var elColormap  = document.getElementById('feaColormap');
var elAuto      = document.getElementById('feaAuto');
var elMin       = document.getElementById('feaMin');
var elMax       = document.getElementById('feaMax');
var elEdges     = document.getElementById('feaEdges');
var elSmooth    = document.getElementById('feaSmooth');
var elAbs       = document.getElementById('feaAbs');
var elAlarm     = document.getElementById('feaAlarm');
var elAlarmVal  = document.getElementById('feaAlarmVal');
var elBtnFlash  = document.getElementById('btnFlash');
var elStatus    = document.getElementById('feaStatus');
var elLegendTitle  = document.getElementById('legendTitle');
var elLegend    = document.getElementById('feaLegend');
var elLegendLabels = document.getElementById('feaLegendLabels');
var elLegendMax = document.getElementById('feaLegendMax');
var elLegendMid = document.getElementById('feaLegendMid');
var elLegendMin = document.getElementById('feaLegendMin');
var elLegendCats   = document.getElementById('feaLegendCats');
var elViewCaption  = document.getElementById('viewCaption');
var elRoMode    = document.getElementById('roMode');
var elRoValue   = document.getElementById('roValue');
var elRoComp    = document.getElementById('roComp');
var elRoElem    = document.getElementById('roElem');
var elRoNode    = document.getElementById('roNode');
var elRoCorners = document.getElementById('roCorners');
var elRoUV      = document.getElementById('roUV');
var elRoPos     = document.getElementById('roPos');
var elRoControllingRow = document.getElementById('roControllingRow');
var elRoControlling    = document.getElementById('roControlling');
var elCalcCard  = document.getElementById('calcCard');
var elDeformSection = document.getElementById('deformSection');
var elDefEnable = document.getElementById('defEnable');
var elDefScale  = document.getElementById('defScale');
var elDefSlider = document.getElementById('defSlider');
var elBtnDefAuto = document.getElementById('btnDefAuto');
var elDefAnimate = document.getElementById('defAnimate');
var elDefSpeed  = document.getElementById('defSpeed');

// ================================================================
// Utilities
// ================================================================
function log(msg) {
    if (elStatus) elStatus.textContent = msg;
    if (window.console) console.log('[viewer] ' + msg);
}

function fmt(v, digits) {
    if (v === undefined || v === null || !(v === v)) return 'no data';
    if (v === Infinity) return '+inf';
    if (v === -Infinity) return '-inf';
    var a = Math.abs(v);
    if (a !== 0 && (a < 1e-3 || a >= 1e6)) return v.toExponential(3);
    return v.toLocaleString(undefined, { maximumFractionDigits: digits == null ? 3 : digits });
}

// Mode helpers -- the one place "what are we looking at" is decided.
function inDsrMode()  { return currentEnvelope === 'dsr'; }
function inCatMode()  { return currentEnvelope === 'dsr' && dsrCategorical; }
function inStrMode()  { return currentStr >= 0 && !currentEnvelope; }
function isDsrComponent() {
    return !inDsrMode() && !inStrMode() && feaModel &&
        feaModel.meta.components[currentComp] &&
        feaModel.meta.components[currentComp].kind === 'dsr';
}
function isDsrView()  { return inDsrMode() || isDsrComponent(); }
function deformBlocked() { return deform.enabled; }   // picking disabled while deformed
function strengthComponents() {
    return (feaModel && feaModel.meta.strengths)
        ? feaModel.meta.strengths.components : null;
}
function activeComponent() {
    if (!feaModel) return null;
    return inStrMode() ? strengthComponents()[currentStr]
                       : feaModel.meta.components[currentComp];
}
function compUnit(i) {
    var c = feaModel && feaModel.meta.components[i];
    return (c && c.unit) ? c.unit : '';
}
// Global LC index -> bare name / unambiguous "file · name" label (the
// file prefix only appears when more than one model is loaded, since
// LC names/numbers commonly collide across model variants).
function lcName(i) {
    return feaSet ? feaSet.lcName(i) : ('LC ' + (i + 1));
}
function lcFullName(i) {
    return feaSet ? feaSet.lcFullName(i) : lcName(i);
}

// ================================================================
// Three.js setup  (the field mesh, edges and flash overlay are all
// ShaderMaterial and unlit; the lights exist only for the Phong
// marker / focus-orb spheres)
// ================================================================
(function initThree() {
    scene = new THREE.Scene();
    scene.background = new THREE.Color(0x111114);
    scene.add(new THREE.AmbientLight(0x404040));
    var dirLight = new THREE.DirectionalLight(0xffffff, 0.8);
    dirLight.position.set(1, 2, 3);
    scene.add(dirLight);

    perspCam = new THREE.PerspectiveCamera(
        50, window.innerWidth / window.innerHeight, 0.1, 100000);
    perspCam.position.set(120, 90, 180);
    camera = perspCam;

    renderer = new THREE.WebGLRenderer({ antialias: true });
    renderer.setPixelRatio(window.devicePixelRatio || 1);
    renderer.setSize(window.innerWidth, window.innerHeight);
    renderer.domElement.id = 'feaCanvas';
    document.body.appendChild(renderer.domElement);

    controls = makeControls(camera);

    viewCube = new ViewCube(camera, controls, requestRender);

    // World up axis: Y (STAAD) or Z (plant / SP3D). Remembered per browser.
    var savedUp = null;
    try { savedUp = localStorage.getItem('pluto.zUp'); } catch (e) {}
    if (savedUp === '1') setZUp(true, true);
})();

// Browser zoom changes window.devicePixelRatio; the renderer's pixel
// ratio must follow or the drawing buffer, viewport math and raycast
// coordinates drift apart (symptom: picking lands on the wrong spot).
// Zoom usually fires a resize event, but not on every path -- animate()
// also watches the DPR each frame as a backstop.
var lastDPR = window.devicePixelRatio || 1;

// OrbitControls freezes its spherical frame from camera.up at
// construction, so changing the up axis means rebuilding it.
function makeControls(cam, keepTarget) {
    var c = new THREE.OrbitControls(cam, renderer.domElement);
    c.enableDamping = true;
    c.dampingFactor = 0.12;
    if (keepTarget) c.target.copy(keepTarget);
    var orbitTimer = 0;
    c.addEventListener('start', function () { orbitTimer = setTimeout(function () { orbiting = true; }, 100); });
    c.addEventListener('end',   function () { clearTimeout(orbitTimer); orbiting = false; });
    return c;
}

var zUp = false;
function setZUp(on, silent) {
    zUp = !!on;
    var up = zUp ? new THREE.Vector3(0, 0, 1) : new THREE.Vector3(0, 1, 0);
    perspCam.up.copy(up);
    if (orthoCam) orthoCam.up.copy(up);
    var target = controls ? controls.target.clone() : new THREE.Vector3();
    // Keep the camera from sitting exactly on the new pole (gimbal lock).
    var dir = camera.position.clone().sub(target);
    if (dir.lengthSq() > 0) {
        var n = dir.clone().normalize();
        if (Math.abs(n.dot(up)) > 0.999) { dir.x += dir.length() * 0.05; }
        camera.position.copy(target).add(dir);
    }
    if (controls) controls.dispose();
    controls = makeControls(camera, target);
    controls.update();
    if (viewCube) viewCube.setMainControls(controls, zUp ? 'z' : 'y');
    if (elZUp) elZUp.checked = zUp;
    try { localStorage.setItem('pluto.zUp', zUp ? '1' : '0'); } catch (e) {}
    if (!silent) log('World up axis: ' + (zUp ? 'Z' : 'Y') + '.');
    needsRender = true;
}

function handleResize() {
    var w = window.innerWidth, h = window.innerHeight;
    lastDPR = window.devicePixelRatio || 1;
    perspCam.aspect = w / h;
    perspCam.updateProjectionMatrix();
    if (orthoCam) {
        var halfH = (orthoCam.top - orthoCam.bottom) / 2;
        var halfW = halfH * (w / h);
        orthoCam.left = -halfW; orthoCam.right = halfW;
        orthoCam.updateProjectionMatrix();
    }
    renderer.setPixelRatio(lastDPR);
    renderer.setSize(w, h);
    if (viewCube) viewCube.updatePixelRatio();
    needsRender = true;
}
window.addEventListener('resize', handleResize);

// ---- main loop ----------------------------------------------------
function animate() {
    requestAnimationFrame(animate);
    var now = performance.now();

    // Backstop for browser-zoom changes that don't fire a resize event.
    if ((window.devicePixelRatio || 1) !== lastDPR) handleResize();

    updateFocusTween();
    if (focusTween) needsRender = true;

    // controls.update() with damping returns true while the camera is
    // still settling; that's our trigger to keep rendering.
    if (controls.update()) needsRender = true;

    updateFocusOrb();
    if (window.FEASectionCut) FEASectionCut.updateScale();
    var orbVisibleNow = focusOrb && focusOrb.visible;
    if (orbVisibleNow || prevOrbVisible) needsRender = true;
    prevOrbVisible = orbVisibleNow;

    updateCameraNearFar();    // sets needsRender on a meaningful change

    if (deform.enabled && deform.animating) {
        setDispUniforms(currentDispScale(now));
        needsRender = true;
    }

    if (flashUntil > now) {
        updateFlash(now);
        needsRender = true;
    } else if (flashMesh && flashMesh.visible) {
        flashMesh.visible = false;
        needsRender = true;
    }

    if (highlightObj) {
        updateHighlightFlash(now);
        needsRender = true;
    }

    if (needsRender) {
        renderer.render(scene, camera);
        if (viewCube) viewCube.render();
        needsRender = false;
    }
}

// Keep near/far tight to the camera-to-target distance so the depth
// buffer has precision to spare for the edge overlay. Only update when
// the change is meaningful, to avoid every-frame matrix churn.
function updateCameraNearFar() {
    if (!mesh) return;
    var d = camera.position.distanceTo(controls.target);
    if (d < 1e-6) return;
    var newNear = Math.max(0.01, d * 0.01);
    var newFar  = d * 100;
    if (Math.abs(camera.near - newNear) / newNear > 0.05 ||
        Math.abs(camera.far  - newFar)  / newFar  > 0.05) {
        camera.near = newNear;
        camera.far  = newFar;
        camera.updateProjectionMatrix();
        needsRender = true;
    }
}

// Tween controls.target toward focusTween.to over focusTween.duration,
// ease-out cubic. Set by Ctrl+click and by find (the camera itself
// never dollies -- only the orbit focus moves).
function updateFocusTween() {
    if (!focusTween) return;
    var t = (performance.now() - focusTween.t0) / focusTween.duration;
    if (t >= 1) {
        controls.target.copy(focusTween.to);
        if (focusTween.camTo) camera.position.copy(focusTween.camTo);
        focusTween = null;
    } else {
        var s = 1 - Math.pow(1 - t, 3);
        controls.target.lerpVectors(focusTween.from, focusTween.to, s);
        if (focusTween.camTo) camera.position.lerpVectors(focusTween.camFrom, focusTween.camTo, s);
    }
}

// Show the red focus orb at controls.target while orbiting OR for ~1s
// after a Ctrl+click. Fades out over the last 200ms.
function orbScale() {
    var dist = camera.position.distanceTo(controls.target);
    if (dist < 0.03) return 0;
    return dist > 20 ? 0.01 * dist : Math.max(0.1, 0.074 - 0.048 * Math.log(dist));
}

function updateFocusOrb() {
    if (!focusOrb) return;
    var s = orbScale();
    if (s === 0) { focusOrb.visible = false; return; }
    var now = performance.now();
    var msLeft = focusOrbHideAt - now;
    if (orbiting) {
        focusOrb.visible = true;
    } else if (msLeft > 0) {
        focusOrb.visible = true;
    } else {
        focusOrb.visible = false;
        return;
    }
    focusOrb.scale.setScalar(s);
    focusOrb.position.copy(controls.target);
    if (feaMarker && feaMarker.visible) feaMarker.scale.setScalar(s);
}

function tweenFocusTo(point) {
    focusTween = {
        from: controls.target.clone(),
        to:   point.clone(),
        t0:   performance.now(),
        duration: 350
    };
    if (focusOrb) {
        focusOrb.position.copy(point);
        focusOrbHideAt = performance.now() + 1000;
    }
}

// Double-click: focus AND fly. Keeps the current view direction (the PCO
// workbench rule) and lands at 30% of the current orbit distance, so each
// double-click moves the camera meaningfully closer without disorienting.
function flyFocusTo(point) {
    var cur = camera.position.distanceTo(controls.target);
    var dir = camera.position.clone().sub(controls.target);
    if (dir.lengthSq() < 1e-12) dir.set(0, 0, 1); else dir.normalize();
    var dist = Math.max(cur * 0.3, 1e-4);
    tweenFocusTo(point);
    focusTween.camFrom = camera.position.clone();
    focusTween.camTo = point.clone().addScaledVector(dir, dist);
    focusTween.duration = 600;
}
animate();

// ================================================================
// Model loading
// ================================================================
function trimExt(name) {
    return String(name).replace(/\.(bin|feabin|dat|rbnl)$/i, '');
}

// Files written at plant coordinates (1e4-1e6 in file units) wobble a few
// pixels while the camera settles: node positions become float32 on the GPU,
// where 2^-24 relative precision at those magnitudes is a visible fraction
// of an inch. Recenter the node table (shared by every view: shells, beams,
// picking) at load; roPosText adds the offset back so the readout still
// shows plant coordinates. Small models are left untouched.
var viewRecenter = null;    // [x, y, z] subtracted from every node, or null
function recenterModel(model) {
    viewRecenter = null;
    var xyz = model && model.nodes;
    // a v4 domain view carries the count in header.nNodes (model.nNodes was undefined there,
    // so v4 files were never recentered: SAP models at survey coordinates flickered, 2026-10-02)
    var n = model ? (model.header ? model.header.nNodes : model.nNodes) : 0;
    if (!xyz || !n) return;
    var mn = [Infinity, Infinity, Infinity], mx = [-Infinity, -Infinity, -Infinity];
    for (var i = 0; i < n * 3; i++) {
        var a = i % 3;
        if (xyz[i] < mn[a]) mn[a] = xyz[i];
        if (xyz[i] > mx[a]) mx[a] = xyz[i];
    }
    var c = [Math.round((mn[0] + mx[0]) / 2), Math.round((mn[1] + mx[1]) / 2), Math.round((mn[2] + mx[2]) / 2)];
    if (Math.max(Math.abs(c[0]), Math.abs(c[1]), Math.abs(c[2])) < 1000) return;
    for (var j = 0; j < n * 3; j++) xyz[j] -= c[j % 3];
    viewRecenter = c;
    log('Recentered model by [' + c.join(', ') + '] (large world coordinates; readout adds it back).');
}

// entries: [{ file, name }] -- one or more model files with identical
// geometry (e.g. soil-spring variants). The first successfully loaded
// file is the primary (geometry / components / strengths source);
// later files are validated against it and their LCs concatenated
// into one globally indexed list.
async function loadModels(entriesIn) {
    try {
        var accepted = [], skipped = [], warns = [];
        for (var i = 0; i < entriesIn.length; i++) {
            var en = entriesIn[i];
            log('Reading ' + en.name + '  (' + (i + 1) + '/' + entriesIn.length + ')...');
            var m;
            try {
                var unified = await PlutoFormat.load(en.file, log);
                m = PlutoFormat.shellView(unified);     // synthetic empty view when beam-only
            } catch (err) {
                skipped.push(en.name + ': ' + err.message);
                continue;
            }
            if (accepted.length === 0) {
                accepted.push({ model: m, name: en.name, remap: null });
            } else {
                var v = FEAModelSet.validate(accepted[0].model, m);
                if (!v.ok) {
                    skipped.push(en.name + ': ' + v.reason);
                    continue;
                }
                v.warn.forEach(function (w) { warns.push(en.name + ': ' + w); });
                // geometry verified identical -- free the duplicate buffers,
                // only the file handle / header / meta are needed from here
                m.nodes = m.elems = m.nodeIds = m.elemIds = null;
                accepted.push({ model: m, name: en.name, remap: v.remap });
            }
        }
        if (accepted.length === 0) {
            log('Load failed: ' + (skipped[0] || 'no readable files.'));
            return;
        }

        var model = accepted[0].model;
        recenterModel(model);
        var build = FEAGeometry.build(model);

        disposeCurrentModel();
        feaModel = model;
        feaSet = FEAModelSet.build(accepted);
        window.feaPrimaryName = accepted.map(function (a) { return a.name; }).join(' + ');
        if (window.FEAOverlays) FEAOverlays.onPrimaryLoaded();
        feaBuild = build;

        dsrIndices = [];
        model.meta.components.forEach(function (c, i2) {
            if (c.kind === 'dsr') dsrIndices.push(i2);
        });

        // Colored field mesh (ShaderMaterial, true bilinear fragment).
        texScalar = FEAShaders.makeColormapTexture(colormapName);
        feaMaterial = new THREE.ShaderMaterial({
            uniforms: {
                colormap: { value: texScalar },
                vMin: { value: 0 },
                vMax: { value: 1 },
                alarmThreshold: { value: alarmEnabled ? alarmThreshold : 0 },
                alarmColor: { value: new THREE.Vector3(
                    alarmColor[0] / 255, alarmColor[1] / 255, alarmColor[2] / 255) },
                uAbs: { value: 0 },
                uCategorical: { value: 0 },
                dispScale: { value: 0 },
                uGroupMode: { value: 0 },
                groupPalette: { value: FEAShaders.makePaletteTexture([[200, 200, 200]]) },
                uGroupCount: { value: 1 }
            },
            vertexShader: FEAShaders.vertex,
            fragmentShader: FEAShaders.fragment,
            side: THREE.DoubleSide
        });
        // Aggressive polygon offset + dynamic near/far (updateCameraNearFar
        // in the animate loop) together kill the z-fight that makes edges
        // flicker on large models. Default factor/units = 1/1 isn't enough
        // once the model spans many feet.
        feaMaterial.polygonOffset = true;
        feaMaterial.polygonOffsetFactor = 2;
        feaMaterial.polygonOffsetUnits = 4;
        mesh = new THREE.Mesh(build.geometry, feaMaterial);
        scene.add(mesh);

        // Alarm flash overlay: same geometry, x-ray fragment that draws
        // only flagged regions. Invisible except while flashing.
        flashMesh = new THREE.Mesh(build.geometry, new THREE.ShaderMaterial({
            uniforms: {
                alarmThreshold: { value: 0 },
                alarmColor: { value: feaMaterial.uniforms.alarmColor.value },
                uAbs: { value: 0 },
                uFlashAlpha: { value: 0 },
                dispScale: { value: 0 }
            },
            vertexShader: FEAShaders.vertex,
            fragmentShader: FEAShaders.flashFragment,
            side: THREE.DoubleSide,
            transparent: true,
            depthTest: false,
            depthWrite: false
        }));
        flashMesh.renderOrder = 5;
        flashMesh.visible = false;
        scene.add(flashMesh);

        // Element-perimeter edge overlay (toggle). ShaderMaterial so the
        // edges deform in lockstep with the mesh; depthWrite: false stops
        // overlapping edges from z-fighting each other; renderOrder = 1
        // forces the edges through after the mesh so the offset never
        // loses.
        var edgeGeo = new THREE.BufferGeometry();
        edgeGeo.setAttribute('position',
            new THREE.BufferAttribute(build.edgePositions, 3));
        edgeGeo.setAttribute('dispVec',
            new THREE.BufferAttribute(new Float32Array(build.edgePositions.length), 3));
        var edgeVis = new Float32Array(build.edgePositions.length / 3);
        edgeVis.fill(1);
        edgeGeo.setAttribute('elemVis', new THREE.BufferAttribute(edgeVis, 1));
        feaEdgeMaterial = new THREE.ShaderMaterial({
            uniforms: {
                uColor: { value: new THREE.Vector3(0, 0, 0) },
                uOpacity: { value: 0.35 },
                dispScale: { value: 0 }
            },
            vertexShader: FEAShaders.edgeVertex,
            fragmentShader: FEAShaders.edgeFragment,
            transparent: true,
            depthWrite: false
        });
        feaEdges = new THREE.LineSegments(edgeGeo, feaEdgeMaterial);
        feaEdges.renderOrder = 1;
        feaEdges.visible = elEdges.checked;
        scene.add(feaEdges);

        // Pin marker.
        var d = new THREE.Box3().setFromObject(mesh).getSize(new THREE.Vector3());
        var span = Math.max(d.x, d.y, d.z) || 1;
        feaMarker = new THREE.Mesh(
            new THREE.SphereGeometry(1, 24, 16),
            new THREE.MeshPhongMaterial({ color: 0xffffff, shininess: 60 }));
        feaMarker.scale.setScalar(span * 0.012);
        feaMarker.visible = false;
        scene.add(feaMarker);

        focusOrb = new THREE.Mesh(
            new THREE.SphereGeometry(1, 24, 16),
            new THREE.MeshPhongMaterial({ color: 0xff3333, shininess: 60 }));
        focusOrb.scale.setScalar(span * 0.012);
        focusOrb.visible = false;
        scene.add(focusOrb);

        if (window.FEABeams) FEABeams.onModelLoaded(model);
        if (window.FEAPredicates) FEAPredicates.onModelLoaded();   // before features: groups may resolve via predicates
        if (window.FEAFeatures) FEAFeatures.onModelLoaded();
        if (window.FEASectionCut && FEASectionCut.onModelLoaded) FEASectionCut.onModelLoaded();

        populateLCSelect();
        populateKindSelect();
        populateComponentSelect();
        currentLC = 0;
        currentComp = 0;
        setPinned(false);
        updateDeformAvailability();

        await selectLC(0);
        fitView();
        var perFile = accepted.map(function (a) {
            return a.model.header.nFieldLC;
        }).join('+');
        log('Loaded ' + accepted.length + ' file(s) · ' + feaSet.nLC + ' LCs' +
            (accepted.length > 1 ? ' (' + perFile + '), geometry verified' : '') +
            ' · ' + model.header.nElements + ' elements.' +
            (warns.length ? '  ⚠ ' + warns.join('; ') : '') +
            (skipped.length ? '  ✕ skipped ' + skipped.join('; ') : ''));
        if (window.console && (warns.length || skipped.length)) {
            console.warn('[viewer] load notes:', { warns: warns, skipped: skipped });
        }
    } catch (err) {
        log('Load failed: ' + err.message);
        if (window.console) console.error(err);
    }
}

function disposeCurrentModel() {
    if (flashMesh) {
        scene.remove(flashMesh);        // shares mesh geometry -- dispose below
        flashMesh.material.dispose();
        flashMesh = null;
    }
    if (mesh) {
        scene.remove(mesh);
        mesh.geometry.dispose();
        mesh.material.dispose();
        mesh = null;
        feaMaterial = null;
    }
    if (texScalar) { texScalar.dispose(); texScalar = null; }
    if (texCat)    { texCat.dispose();    texCat = null; }
    if (feaEdges) {
        scene.remove(feaEdges);
        feaEdges.geometry.dispose();
        feaEdges.material.dispose();
        feaEdges = null;
        feaEdgeMaterial = null;
    }
    if (feaMarker) {
        scene.remove(feaMarker);
        feaMarker.geometry.dispose();
        feaMarker.material.dispose();
        feaMarker = null;
    }
    if (focusOrb) {
        scene.remove(focusOrb);
        focusOrb.geometry.dispose();
        focusOrb.material.dispose();
        focusOrb = null;
    }
    clearHighlight();
    if (window.FEABeams) FEABeams.onModelCleared();
    if (window.FEAFeatures) FEAFeatures.onModelCleared();
    if (window.FEASectionCut) FEASectionCut.onModelCleared();
    if (window.FEAPredicates) FEAPredicates.onModelCleared();
    focusTween = null;
    focusOrbHideAt = 0;
    flashUntil = 0;
    feaSet = null;
    feaLCData = null;
    lastQuery = null;
    envBufs = null;
    envGlobalDsrValue = envGlobalDsrSource = envGlobalDsrLC = null;
    currentEnvelope = null;
    dsrCategorical = false;
    currentStr = -1;
    dsrIndices = [];
    elemIdMap = nodeIdMap = null;
    deformMaxDisp = 0;
    deformLCLoaded = -1;
    deformScaleAuto = true;
    setDeformEnabled(false);
    dsrAlarmOptOut = false;
    alarmAutoOn = false;
    alarmPrev = null;
    hideCalcCard();
    if (elEnvComputeRow) elEnvComputeRow.style.display = '';
    if (elEnvButtonsRow) elEnvButtonsRow.style.display = 'none';
    if (elEnvDsrRow) elEnvDsrRow.style.display = 'none';
    setEnvActive(null);
}

// ---- LC / component dropdowns -----------------------------------
function populateLCSelect() {
    elLC.innerHTML = '';
    function addOpt(host, g, lc) {
        var opt = document.createElement('option');
        opt.value = g;
        opt.textContent = lc.name + (lc.type === 'envelope' ? '  [env]' : '');
        host.appendChild(opt);
    }
    if (feaSet.count > 1) {
        // Group by model file (LC names/numbers collide across variants):
        // the file name appears once as a divider, options stay bare.
        var og = null, curFile = -1;
        feaSet.lcs.forEach(function (lc, g) {
            if (lc.file !== curFile) {
                curFile = lc.file;
                og = document.createElement('optgroup');
                // "alias — full file name": the header is where the long
                // name lives, and it teaches the alias used inline.
                var full = feaSet.entryName(curFile);
                if (full.length > 40) full = full.slice(0, 39) + '…';
                og.label = feaSet.entryShort(curFile) + ' — ' + full;
                elLC.appendChild(og);
            }
            addOpt(og, g, lc);
        });
    } else {
        feaSet.lcs.forEach(function (lc, g) { addOpt(elLC, g, lc); });
    }
    elLC.disabled = false;
}

// Display label for a component kind. Known kinds get fixed labels;
// anything else (a "generic field" -- e.g. constituent preDSR checks)
// shows its kind string verbatim, so the writer controls the casing.
var KIND_LABELS = { force: 'Force', moment: 'Moment', stress: 'Stress', envelope: 'Envelope', displacement: 'Displacement', dsr: 'DSR', other: 'Other', unknown: 'Other' };
var KIND_ORDER = ['force', 'moment', 'stress', 'envelope', 'displacement', 'dsr', 'other', 'unknown'];
function kindLabel(k) { return KIND_LABELS[k] || k; }

// Kinds present in the model, known ones first in fixed order, then any
// generic kinds in first-appearance order.
function orderedKinds(components) {
    var seen = [];
    components.forEach(function (c) {
        var k = c.kind || 'unknown';
        if (seen.indexOf(k) < 0) seen.push(k);
    });
    return KIND_ORDER.filter(function (k) { return seen.indexOf(k) >= 0; })
        .concat(seen.filter(function (k) { return KIND_ORDER.indexOf(k) < 0; }));
}

// Type filter for the component dropdown. 'all' shows everything in
// kind optgroups; a kind string shows a flat list of that kind only.
// '__strengths' is the design-strength block (its components live
// outside meta.components, so it needs its own filter token).
var kindFilter = 'all';
var STRENGTH_FILTER = '__strengths';

function populateKindSelect() {
    elKind.innerHTML = '';
    function add(value, label) {
        var opt = document.createElement('option');
        opt.value = value;
        opt.textContent = label;
        elKind.appendChild(opt);
    }
    add('all', 'All types');
    orderedKinds(feaModel.meta.components).forEach(function (k) {
        add(k, kindLabel(k));
    });
    if (strengthComponents()) add(STRENGTH_FILTER, 'Design Strength');
    kindFilter = 'all';
    elKind.value = 'all';
    elKind.disabled = false;
}

function populateComponentSelect() {
    elComp.innerHTML = '';
    var comps = feaModel.meta.components;
    var strs = strengthComponents();

    function addOpt(host, value, text) {
        var opt = document.createElement('option');
        opt.value = value;
        opt.textContent = text;
        host.appendChild(opt);
    }

    if (kindFilter === STRENGTH_FILTER) {
        // Option values are 'str:<i>' so the change handler can tell
        // design strengths apart from regular component indices.
        (strs || []).forEach(function (c, i) { addOpt(elComp, 'str:' + i, c.name); });
    } else if (kindFilter !== 'all') {
        comps.forEach(function (c, i) {
            if ((c.kind || 'unknown') === kindFilter) addOpt(elComp, String(i), c.name);
        });
    } else {
        var groups = {};
        comps.forEach(function (c, i) {
            var k = c.kind || 'unknown';
            (groups[k] || (groups[k] = [])).push({ idx: i, name: c.name });
        });
        // Every kind gets an optgroup -- generic kinds are plottable fields
        // with default behavior, they just don't trigger any DSR machinery.
        orderedKinds(comps).forEach(function (k) {
            var og = document.createElement('optgroup');
            og.label = kindLabel(k);
            groups[k].forEach(function (e) { addOpt(og, String(e.idx), e.name); });
            elComp.appendChild(og);
        });
        if (strs) {
            var ogStr = document.createElement('optgroup');
            ogStr.label = 'Design Strength (LC-independent)';
            strs.forEach(function (c, i) { addOpt(ogStr, 'str:' + i, c.name); });
            elComp.appendChild(ogStr);
        }
    }
    elComp.disabled = false;
}

// Select `value` in the component dropdown, widening the type filter
// back to All if the current filter hides it (used by programmatic
// selection changes like leaving a strength view for an envelope).
function ensureCompSelected(value) {
    var present = Array.prototype.some.call(elComp.options, function (o) {
        return o.value === value;
    });
    if (!present) {
        kindFilter = 'all';
        elKind.value = 'all';
        populateComponentSelect();
    }
    elComp.value = value;
}

// On-demand: slice exactly one LC's field block, then recolor. A
// strength view survives LC changes (its display is LC-independent,
// but the resident LC still feeds deformation).
async function selectLC(lc) {
    if (!feaModel) return;
    currentLC = lc;
    currentEnvelope = null;
    dsrCategorical = false;
    setEnvActive(null);
    if (!feaSet || feaSet.nLC === 0) {
        // Geometry-only profile: nothing to slice; beams draw neutral.
        feaLCData = null;
        updateDeformAvailability();
        if (window.FEABeams) FEABeams.sync();
        if (window.FEAFeatures) FEAFeatures.sync();
        updateViewCaption();
        log('Geometry-only model: no load cases (features / groups still work).');
        return;
    }
    elLC.value = lc;
    log('Slicing LC ' + (lc + 1) + ' field block...');
    feaLCData = await feaSet.readLC(lc);   // drops previous LC
    if (window.FEABeams) await FEABeams.onLCChanged(lc);
    if (deform.enabled) refreshDispVecs();
    updateDeformAvailability();
    applyComponentAndRange();
    var ac = activeComponent();
    log('Active: ' + lcFullName(lc) + ' / ' + (ac ? ac.name : 'no component'));
}

// Switch the display to an LC-independent design-strength component. Exits any
// envelope view; keeps (and if needed restores) the resident LC so the
// deformed-shape mode stays available.
async function enterStrengthView(i) {
    var strs = strengthComponents();
    if (!strs || i < 0 || i >= strs.length) return;
    currentStr = i;              // before setEnvActive: updateLCDim reads it
    currentEnvelope = null;
    dsrCategorical = false;
    setEnvActive(null);
    try {
        if (!feaModel.strData) {
            log('Reading design-strength block...');
            await FEABinary.readStrengths(feaModel);
        }
        if (!feaLCData) feaLCData = await feaSet.readLC(currentLC);
    } catch (err) {
        currentStr = -1;
        log('Strength read failed: ' + err.message);
        return;
    }
    updateDeformAvailability();
    applyComponentAndRange();
    log('Design strength: ' + strs[i].name + ' (does not vary with load case)');
}

function stepLC(delta) {
    if (!feaSet) return;
    var next = currentLC + delta;
    if (next < 0 || next >= feaSet.nLC) return;   // steps across file boundaries
    selectLC(next);
}

// Stream all primaries (LCs with type !== 'envelope') through a single
// NaN-skipping fold pass that produces min, max, and abs(max) envelopes
// (each with a which-LC-won tracker) in one go, plus the Global DSR
// envelope when DSR components exist. Buffers live in JS memory only;
// the file and the metadata are not touched.
async function computeEnvelopes() {
    if (!feaSet) return;
    // Primaries across ALL loaded models -- the envelopes and Global DSR
    // span the whole set, and the controlling-LC trackers store global
    // indices, so a hot spot reports which model AND which LC governed.
    var primaries = [];
    for (var i = 0; i < feaSet.nLC; i++)
        if (feaSet.lcs[i].type !== 'envelope') primaries.push(i);
    if (primaries.length === 0) { log('No primary LCs to envelope.'); return; }

    elBtnComputeEnv.disabled = true;
    try {
        var bufs = null;
        var dsrFold = { value: null, source: null, lc: null };
        for (var k = 0; k < primaries.length; k++) {
            log('Folding LC ' + (k + 1) + '/' + primaries.length +
                ' (' + lcFullName(primaries[k]) + ')...');
            // yield so the status line actually paints between folds
            await new Promise(function (r) { setTimeout(r, 0); });
            var prim = await feaSet.readLC(primaries[k]);
            bufs = FEAAttributes.foldEnvelopesThreeWay(bufs, prim, primaries[k]);
            if (dsrIndices.length > 0) {
                var r = FEAAttributes.foldGlobalDsr(
                    dsrFold.value, dsrFold.source, dsrFold.lc,
                    prim, dsrIndices, feaModel.header, primaries[k]);
                dsrFold = r;
            }
        }
        envBufs = bufs;
        envGlobalDsrValue = dsrFold.value;
        envGlobalDsrSource = dsrFold.source;
        envGlobalDsrLC = dsrFold.lc;
        elEnvComputeRow.style.display = 'none';
        elEnvButtonsRow.style.display = '';
        elEnvDsrRow.style.display = dsrIndices.length > 0 ? '' : 'none';
        log('Envelopes ready across ' + primaries.length + ' primaries' +
            (dsrIndices.length ? ' (incl. global DSR over ' + dsrIndices.length + ' check(s))' : '') +
            (bufs.minLC ? ', controlling LC tracked.' : '.'));
    } finally {
        elBtnComputeEnv.disabled = false;
    }
}

// Switch the active view to one of the cached envelopes / Global DSR.
// kind 'dsr' takes an optional categorical flag (controlling-check map).
function selectEnvelope(kind, categorical) {
    // Envelopes fold LC components; a strength view has no place there.
    // Fall back to the still-selected regular component.
    if (currentStr >= 0) {
        currentStr = -1;
        ensureCompSelected(String(currentComp));
    }
    if (kind === 'dsr') {
        if (!envGlobalDsrValue || !feaModel) return;
        currentEnvelope = 'dsr';
        dsrCategorical = !!categorical;
        feaLCData = null;   // not used in DSR mode -- value source is the slot array
        setDeformEnabled(false);
        applyComponentAndRange();
        setEnvActive('dsr');
        log(dsrCategorical
            ? 'Active: Global DSR — controlling-check map'
            : 'Active: Global DSR envelope (worst check across LCs)');
        return;
    }
    var buf = envBufs && (kind === 'min' ? envBufs.min : kind === 'max' ? envBufs.max : envBufs.abs);
    if (!buf || !feaModel) return;
    currentEnvelope = kind;
    dsrCategorical = false;
    feaLCData = buf;
    setDeformEnabled(false);
    applyComponentAndRange();
    setEnvActive(kind);
    var ec = feaModel.meta.components[currentComp];
    log('Active: ' + envelopeLabel(kind) + ' / ' + (ec ? ec.name : 'no component'));
}

function envelopeLabel(kind) {
    return kind === 'min' ? 'Min envelope'
         : kind === 'max' ? 'Max envelope'
         : kind === 'abs' ? 'Abs(max) envelope'
         : kind === 'dsr' ? (dsrCategorical ? 'Global DSR — controlling check' : 'Global DSR envelope')
         : '';
}

function setEnvActive(kind) {
    if (!elBtnEnvMin) return;
    elBtnEnvMin.classList.toggle('active', kind === 'min');
    elBtnEnvMax.classList.toggle('active', kind === 'max');
    elBtnEnvAbs.classList.toggle('active', kind === 'abs');
    elBtnEnvDsr.classList.toggle('active', kind === 'dsr' && !dsrCategorical);
    elBtnEnvCat.classList.toggle('active', kind === 'dsr' && dsrCategorical);
    updateLCDim();
    updateDeformAvailability();
}

// The LC dropdown is stale context whenever the display doesn't follow
// it (envelope views mix LCs; strength views ignore them) -- dim it.
// Changing it still works: it exits an envelope, and under a strength
// view it swaps the resident LC that feeds deformation.
function updateLCDim() {
    elLC.style.opacity = (currentEnvelope || inStrMode()) ? '0.45' : '';
}

// Auto-enable the alarm at 1.0 when entering a DSR view (a DSR display
// without the overstress flag is a trap). Also kicks in when the alarm
// is on but sitting at a stress-scale threshold (> 10) that could never
// trip on a 0..~2 DSR field. The previous alarm state is restored on
// leaving; unchecking the alarm inside a DSR view opts out for the rest
// of the model session.
function autoAlarmForDsr() {
    if (isDsrView() && !inCatMode()) {
        var uselessThreshold = alarmEnabled && alarmThreshold > 10;
        if ((!alarmEnabled || uselessThreshold) && !dsrAlarmOptOut && !alarmAutoOn) {
            alarmPrev = { enabled: alarmEnabled, threshold: alarmThreshold };
            alarmEnabled = true;
            alarmThreshold = 1.0;
            alarmAutoOn = true;
            elAlarm.checked = true;
            elAlarmVal.value = '1.0';
            elAlarmVal.disabled = false;
        }
    } else if (alarmAutoOn) {
        alarmEnabled = alarmPrev.enabled;
        alarmThreshold = alarmPrev.threshold;
        alarmAutoOn = false;
        elAlarm.checked = alarmEnabled;
        elAlarmVal.value = String(alarmThreshold);
        elAlarmVal.disabled = !alarmEnabled;
    }
}

// Recolor by rewriting cornerVals only -- positions are never rebuilt.
function applyComponentAndRange() {
    if (!feaModel) return;
    var inDsr = inDsrMode();
    var cat = inCatMode();

    autoAlarmForDsr();

    if (inDsr) {
        if (!envGlobalDsrValue) return;
        var smVal = null;
        if (smoothing) {
            smVal = FEAAttributes.smoothGlobalDsr(
                feaBuild, envGlobalDsrValue, envGlobalDsrSource, envGlobalDsrLC,
                feaModel.header.nNodes);
            feaBuild.dsrNodeMaxCache = smVal;
        } else {
            feaBuild.dsrNodeSrcCache = null;
            feaBuild.dsrNodeMaxCache = null;
            feaBuild.dsrCornerSmoothed = null;
            feaBuild.dsrCornerSmoothedSrc = null;
            feaBuild.dsrCornerSmoothedLC = null;
        }
        if (cat) {
            var remap = new Uint16Array(feaModel.header.cornerComponents);
            dsrIndices.forEach(function (ci, ord) { remap[ci] = ord; });
            var srcArr = smoothing ? feaBuild.dsrCornerSmoothedSrc : envGlobalDsrSource;
            var valArr = smoothing ? feaBuild.dsrCornerSmoothed : envGlobalDsrValue;
            FEAAttributes.updateCornerValsFromSlotCategories(
                feaBuild, srcArr, valArr, remap, feaModel.header);
        } else {
            FEAAttributes.updateCornerValsFromSlotArray(
                feaBuild,
                smoothing ? feaBuild.dsrCornerSmoothed : envGlobalDsrValue,
                feaModel.header);
        }
        feaBuild.nodeAvgCache = null;
        feaBuild.cornerAvgCache = null;
    } else if (inStrMode()) {
        if (!feaModel.strData) return;
        var nStr = strengthComponents().length;
        if (smoothing) {
            (smoothMode === 'elem' ? FEAAttributes.updateCornerValsElemAveraged
                                   : FEAAttributes.updateCornerValsNodeAveraged)(
                feaBuild, feaModel, feaModel.strData, currentStr, nStr);
        } else {
            FEAAttributes.updateCornerVals(
                feaBuild, feaModel, feaModel.strData, currentStr, nStr);
            feaBuild.nodeAvgCache = null;
            feaBuild.cornerAvgCache = null;
        }
    } else if (smoothing) {
        if (!feaLCData) return;
        (smoothMode === 'elem' ? FEAAttributes.updateCornerValsElemAveraged
                               : FEAAttributes.updateCornerValsNodeAveraged)(
            feaBuild, feaModel, feaLCData, currentComp);
    } else {
        if (!feaLCData) return;
        FEAAttributes.updateCornerVals(feaBuild, feaModel, feaLCData, currentComp);
        feaBuild.nodeAvgCache = null;   // invalidate stale smoothed caches
        feaBuild.cornerAvgCache = null;
    }

    // Colormap texture: discrete category LUT in cat mode, scalar LUT
    // otherwise. Category range maps ordinal k to texel k exactly.
    if (cat) {
        if (!texCat) texCat = FEAShaders.makeCategoricalTexture(Math.max(dsrIndices.length, 1));
        feaMaterial.uniforms.colormap.value = texCat;
        vMin = -0.5;
        vMax = dsrIndices.length - 0.5;
    } else {
        feaMaterial.uniforms.colormap.value = texScalar;
        if (autoRange) {
            // DSR views (Global DSR or a kind:'dsr' component) default to
            // [0, 1] rather than data-driven min/max -- 0 means no demand,
            // 1 means at the design strength, that's the natural scale for the field.
            // Manual range entry still overrides as before.
            if (inDsr || isDsrComponent()) {
                vMin = 0; vMax = 1;
            } else {
                var r0;
                if (smoothing) {
                    r0 = FEAAttributes.computeRangeAveraged(feaBuild);
                } else if (inStrMode()) {
                    r0 = FEAAttributes.computeRange(
                        feaModel, feaModel.strData, currentStr, strengthComponents().length);
                } else {
                    r0 = FEAAttributes.computeRange(feaModel, feaLCData, currentComp);
                }
                if (absValue) r0 = absTransformRange(r0);
                vMin = r0.min; vMax = r0.max;
            }
            elMin.value = fmt(vMin, 4);
            elMax.value = fmt(vMax, 4);
        }
    }
    feaMaterial.uniforms.vMin.value = vMin;
    feaMaterial.uniforms.vMax.value = vMax;
    feaMaterial.uniforms.uCategorical.value = cat ? 1 : 0;
    feaMaterial.uniforms.uAbs.value = (absValue && !cat) ? 1 : 0;
    if (flashMesh) flashMesh.material.uniforms.uAbs.value = (absValue && !cat) ? 1 : 0;
    applyAlarmUniform();
    updateRangeInputsDisabled();
    drawLegend();
    updateViewCaption();

    if (inDsr) {
        elRoComp.textContent = cat
            ? 'Controlling DSR check (per corner)'
            : 'Global DSR (controlling check per corner)';
    } else {
        var c = activeComponent();
        elRoComp.textContent = c ? c.name + (c.unit ? ' [' + c.unit + ']' : '') + ' (' + c.kind + ')' : '—';
    }
    if (window.FEABeams) FEABeams.sync();
    if (window.FEAFeatures) FEAFeatures.sync();
    if (window.FEASectionCut) FEASectionCut.refresh();
    needsRender = true;
}

// |v| display range from a raw range: if the field crosses zero the
// magnitudes span [0, max|.|], otherwise the abs of both endpoints.
function absTransformRange(r) {
    if (r.min < 0 && r.max > 0)
        return { min: 0, max: Math.max(-r.min, r.max), empty: r.empty };
    var a = Math.abs(r.min), b = Math.abs(r.max);
    return { min: Math.min(a, b), max: Math.max(a, b), empty: r.empty };
}

function updateRangeInputsDisabled() {
    var cat = inCatMode();
    elMin.disabled = autoRange || cat;
    elMax.disabled = autoRange || cat;
}

// ================================================================
// Active-view caption (canvas overlay + legend title)
// ================================================================
function viewSourceLabel() {
    if (!feaModel) return '—';
    if (currentEnvelope) return envelopeLabel(currentEnvelope);
    if (inStrMode()) return 'Design Strength';
    if (feaSet && feaSet.nLC === 0) return 'geometry';
    return lcFullName(currentLC);
}

// The legend / caption follow the BEAM field when there is no shell field to show
// (frame-only models, 2026-10-02): {name, unit, min, max} or null.
function beamLegend() {
    if (!window.FEABeams || !FEABeams.legend || inDsrMode()) return null;
    var hasShells = feaModel && feaModel.header && feaModel.header.nElements > 0;
    if (hasShells && activeComponent()) return null;
    return FEABeams.legend();
}

function updateViewCaption() {
    if (!feaModel) { elViewCaption.textContent = '—'; elLegendTitle.textContent = '—'; return; }
    var parts = [viewSourceLabel()];
    var bl = beamLegend();
    if (bl) {
        var bname = bl.name + (bl.unit ? ' [' + bl.unit + ']' : '');
        parts.push((absValue ? '|' + bname + '|' : bname) + ' · beams');
    } else if (!inDsrMode()) {
        var c = activeComponent();
        if (!c) {
            parts.push(feaSet && feaSet.nLC === 0 ? 'geometry only' : 'no component');
        } else {
            var cname = c.name + (c.unit ? ' [' + c.unit + ']' : '');
            if (absValue) cname = '|' + cname + '|';
            parts.push(cname);
        }
    } else if (!inCatMode() && absValue) {
        // abs on the (already >= 0) DSR value is a no-op; don't advertise it
    }
    if (smoothing && !bl) parts.push(smoothMode === 'elem' ? 'element means' : 'smoothed');
    // The colorscale title stays about the FIELD only -- deformation
    // state (an exaggeration of geometry, not of values) shows in the
    // canvas caption but not above the legend.
    elLegendTitle.textContent = parts.join(' · ');
    if (deform.enabled) {
        parts.push('deformed ×' + fmt(deform.scale, 1) + (deform.animating ? ' (animating)' : ''));
    }
    elViewCaption.innerHTML = parts.join(' <span class="vc-dim">·</span> ');
    // full file name on hover (inline labels use the short alias)
    elViewCaption.title = (feaSet && feaSet.count > 1 && !currentEnvelope)
        ? feaSet.lcLongName(currentLC) : '';
}

// ================================================================
// Camera framing / standard views / projection
// ================================================================
function modelSphere() {
    if (!mesh) return null;
    var box = new THREE.Box3().setFromObject(mesh);
    if (window.FEABeams && FEABeams.mesh()) box.union(new THREE.Box3().setFromObject(FEABeams.mesh()));
    return box.getBoundingSphere(new THREE.Sphere());
}

function fitView() {
    if (!mesh) return;
    var sph = modelSphere();
    var r = sph.radius || 1;
    // Deformed shapes can poke past the static bounds -- pad by the
    // exaggerated peak displacement.
    if (deform.enabled) r += deform.scale * deformMaxDisp;
    controls.target.copy(sph.center);
    var dir = camera.position.clone().sub(sph.center);
    if (dir.lengthSq() < 1e-12) dir.set(0.5, 0.45, 0.9);
    dir.normalize().multiplyScalar(r * 2.6);
    camera.position.copy(sph.center).add(dir);
    frameCamera(r);
    controls.update();
    needsRender = true;
}

// Set near/far (+ ortho frustum) so a sphere of radius r is framed.
function frameCamera(r) {
    camera.near = r * 0.01;
    camera.far = r * 200;
    if (camera.isOrthographicCamera) {
        var aspect = window.innerWidth / window.innerHeight;
        var halfH = r * 1.15;
        var halfW = halfH * aspect;
        camera.left = -halfW; camera.right = halfW;
        camera.top = halfH; camera.bottom = -halfH;
        camera.zoom = 1;
    }
    camera.updateProjectionMatrix();
}

// Perspective <-> orthographic. The switch preserves the apparent size:
// persp->ortho matches the frustum height at the target distance;
// ortho->persp converts accumulated ortho zoom back into distance.
function setProjection(ortho) {
    var target = controls.target;
    if (ortho) {
        var d = perspCam.position.distanceTo(target);
        var halfH = d * Math.tan(perspCam.fov * Math.PI / 360);
        var aspect = window.innerWidth / window.innerHeight;
        if (!orthoCam) orthoCam = new THREE.OrthographicCamera(-1, 1, 1, -1, 0.1, 100000);
        orthoCam.left = -halfH * aspect; orthoCam.right = halfH * aspect;
        orthoCam.top = halfH; orthoCam.bottom = -halfH;
        orthoCam.zoom = 1;
        orthoCam.near = perspCam.near; orthoCam.far = perspCam.far;
        orthoCam.position.copy(perspCam.position);
        orthoCam.up.copy(perspCam.up);
        orthoCam.updateProjectionMatrix();
        camera = orthoCam;
    } else {
        var curHalfH = (orthoCam.top - orthoCam.bottom) / 2 / orthoCam.zoom;
        var newDist = curHalfH / Math.tan(perspCam.fov * Math.PI / 360);
        var dir = orthoCam.position.clone().sub(target);
        if (dir.lengthSq() < 1e-12) dir.set(0.5, 0.45, 0.9);
        perspCam.position.copy(target).add(dir.normalize().multiplyScalar(newDist));
        perspCam.up.copy(orthoCam.up);
        camera = perspCam;
    }
    controls.object = camera;
    if (viewCube) viewCube.setMainCamera(camera);
    controls.update();
    needsRender = true;
}

// ================================================================
// Find element / node by real ID + fly-to highlight
// ================================================================
function ensureIdMaps() {
    if (elemIdMap) return;
    elemIdMap = new Map();
    nodeIdMap = new Map();
    var eIds = feaModel.elemIds, nIds = feaModel.nodeIds;
    for (var e = 0; e < eIds.length; e++) elemIdMap.set(eIds[e], e);
    for (var n = 0; n < nIds.length; n++) nodeIdMap.set(nIds[n], n);
}

function clearHighlight() {
    if (!highlightObj) return;
    scene.remove(highlightObj);
    highlightObj.traverse(function (o) {
        if (o.geometry) o.geometry.dispose();
        if (o.material) o.material.dispose();
    });
    highlightObj = null;
    highlightMats = null;
}

// Pulse the find-flash like the alarm flash: raised-cosine cycles,
// drawn on top of everything (depthTest false), auto-removed at the end.
function updateHighlightFlash(now) {
    var t = (now - highlightStart) / HIGHLIGHT_MS;
    if (t >= 1) { clearHighlight(); return; }
    var a = 0.5 - 0.5 * Math.cos(2 * Math.PI * 3 * t);
    for (var i = 0; i < highlightMats.length; i++) {
        highlightMats[i].material.opacity = highlightMats[i].peak * a;
    }
}

function startHighlight(group, mats) {
    clearHighlight();
    highlightObj = group;
    highlightMats = mats;
    highlightStart = performance.now();
    mats.forEach(function (m) { m.material.opacity = 0; });
    scene.add(group);
    needsRender = true;
}

function highlightElement(elemIdx) {
    var nc = feaBuild.elemNCount[elemIdx];
    var k, corners = [];
    for (k = 0; k < nc; k++)
        corners.push(nodeVec(feaBuild.elemCorners[elemIdx * 4 + k]));

    var group = new THREE.Group();
    group.renderOrder = 6;

    // Filled face -- visible at any zoom level, unlike a 1px outline.
    var tris = nc === 4 ? [[0, 1, 2], [0, 2, 3]] : [[0, 1, 2]];
    var facePts = [];
    tris.forEach(function (tr) {
        facePts.push(corners[tr[0]], corners[tr[1]], corners[tr[2]]);
    });
    var faceMat = new THREE.MeshBasicMaterial({
        color: 0xffe14d, transparent: true, opacity: 0,
        depthTest: false, depthWrite: false, side: THREE.DoubleSide });
    var face = new THREE.Mesh(
        new THREE.BufferGeometry().setFromPoints(facePts), faceMat);
    face.renderOrder = 6;
    group.add(face);

    // Perimeter outline on top of the fill.
    var edgePts = [];
    for (k = 0; k < nc; k++)
        edgePts.push(corners[k], corners[(k + 1) % nc]);
    var lineMat = new THREE.LineBasicMaterial({
        color: 0xffe14d, transparent: true, opacity: 0, depthTest: false });
    var line = new THREE.LineSegments(
        new THREE.BufferGeometry().setFromPoints(edgePts), lineMat);
    line.renderOrder = 7;
    group.add(line);

    startHighlight(group, [
        { material: faceMat, peak: 0.55 },
        { material: lineMat, peak: 1.0 }
    ]);
}

function highlightNode(nodeIdx) {
    var sph = modelSphere();
    var mat = new THREE.MeshBasicMaterial({ color: 0xffe14d, depthTest: false,
        depthWrite: false, transparent: true, opacity: 0 });
    var m = new THREE.Mesh(new THREE.SphereGeometry(1, 20, 14), mat);
    m.scale.setScalar((sph ? sph.radius : 100) * 0.008);
    m.position.copy(nodeVec(nodeIdx));
    m.renderOrder = 6;
    var group = new THREE.Group();
    group.add(m);
    startHighlight(group, [{ material: mat, peak: 0.95 }]);
}

function nodeVec(nodeIdx) {
    return new THREE.Vector3(
        feaModel.nodes[nodeIdx * 3],
        feaModel.nodes[nodeIdx * 3 + 1],
        feaModel.nodes[nodeIdx * 3 + 2]);
}

function elemCentroid(elemIdx) {
    var nc = feaBuild.elemNCount[elemIdx];
    var c = new THREE.Vector3();
    for (var k = 0; k < nc; k++)
        c.add(nodeVec(feaBuild.elemCorners[elemIdx * 4 + k]));
    return c.multiplyScalar(1 / nc);
}

// Find sets the orbit focus (no zoom -- the camera keeps its distance)
// and flashes the target on top of everything so it's findable even
// when small or obscured.
function focusElement(elemIdx) {
    tweenFocusTo(elemCentroid(elemIdx));
    highlightElement(elemIdx);
}

function focusNode(nodeIdx) {
    tweenFocusTo(nodeVec(nodeIdx));
    highlightNode(nodeIdx);
}

function doFind() {
    if (!feaModel) return;
    var id = parseInt(elFindId.value, 10);
    if (isNaN(id)) { log('Find: enter a numeric ID.'); return; }
    ensureIdMaps();
    if (elFindKind.value === 'elem') {
        var e = elemIdMap.get(id);
        if (e === undefined) { log('Element ID ' + id + ' not found.'); return; }
        focusElement(e);
        log('Element ' + id + ' (index ' + e + ').');
    } else {
        var n = nodeIdMap.get(id);
        if (n === undefined) { log('Node ID ' + id + ' not found.'); return; }
        focusNode(n);
        log('Node ' + id + ' (index ' + n + ').');
    }
}

// ================================================================
// Colormap legend
// ================================================================
function drawLegend() {
    var ctx = elLegend.getContext('2d');
    var w = elLegend.width, h = elLegend.height;
    ctx.clearRect(0, 0, w, h);

    if (inCatMode()) {
        // Discrete controlling-check swatches, top = category 0.
        var n = Math.max(dsrIndices.length, 1);
        var catHoriz = w >= h;          // same axis rule as the gradient below
        for (var i = 0; i < n; i++) {
            var c0 = FEAShaders.categoryColor(i);
            ctx.fillStyle = 'rgb(' + c0[0] + ',' + c0[1] + ',' + c0[2] + ')';
            var span = catHoriz ? w : h;
            var a0 = Math.round(i * span / n), a1 = Math.round((i + 1) * span / n);
            if (catHoriz) ctx.fillRect(a0, 0, a1 - a0, h);
            else ctx.fillRect(0, a0, w, a1 - a0);
        }
        elLegendLabels.style.display = 'none';
        elLegendCats.style.display = '';
        elLegendCats.innerHTML = '';
        dsrIndices.forEach(function (ci, ord) {
            var row = document.createElement('div');
            row.className = 'cat-row';
            var sw = document.createElement('span');
            sw.className = 'cat-swatch';
            var cc = FEAShaders.categoryColor(ord);
            sw.style.background = 'rgb(' + cc[0] + ',' + cc[1] + ',' + cc[2] + ')';
            var name = document.createElement('span');
            name.textContent = feaModel.meta.components[ci].name;
            row.appendChild(sw);
            row.appendChild(name);
            elLegendCats.appendChild(row);
        });
        return;
    }

    elLegendLabels.style.display = '';
    elLegendCats.style.display = 'none';

    var anchors = FEAShaders.colormaps[colormapName] || FEAShaders.colormaps.viridis;
    // Above the alarm threshold (mapped into legend space), draw the alarm
    // color so the legend matches what's on the mesh.
    var bl = beamLegend();
    var lo = bl ? bl.min : vMin, hi = bl ? bl.max : vMax;
    var alarmActive = alarmEnabled && alarmThreshold > 0;
    var tAlarm = alarmActive
        ? (alarmThreshold - lo) / Math.max(hi - lo, 1e-6)
        : Infinity;
    var aRGB = 'rgb(' + alarmColor[0] + ',' + alarmColor[1] + ',' + alarmColor[2] + ')';
    // Orientation follows the canvas aspect: wide = horizontal bar with min
    // at the left, tall = vertical strip with max at the top. The CSS
    // decides, so this can never disagree with what is on screen.
    var horiz = w >= h;
    var n = horiz ? w : h;
    for (var i = 0; i < n; i++) {
        var t = horiz ? i / (n - 1) : 1 - i / (n - 1);
        if (t >= tAlarm) {
            ctx.fillStyle = aRGB;
        } else {
            var c = FEAShaders.sampleAnchors(anchors, t);
            ctx.fillStyle = 'rgb(' + (c[0] | 0) + ',' + (c[1] | 0) + ',' + (c[2] | 0) + ')';
        }
        if (horiz) ctx.fillRect(i, 0, 1, h);
        else ctx.fillRect(0, i, w, 1);
    }
    // Threshold tick so the alarm boundary is readable even when it sits
    // mid-gradient.
    if (alarmActive && tAlarm > 0 && tAlarm < 1) {
        ctx.fillStyle = '#fff';
        if (horiz) ctx.fillRect(Math.round(tAlarm * (w - 1)), 0, 1, h);
        else ctx.fillRect(0, Math.round((1 - tAlarm) * (h - 1)), w, 1);
    }
    elLegendMax.textContent = alarmActive && tAlarm <= 1
        ? '≥ ' + fmt(alarmThreshold, 4)
        : fmt(hi, 4);
    elLegendMid.textContent = fmt((lo + hi) / 2, 4);
    elLegendMin.textContent = fmt(lo, 4);
}

// ================================================================
// Point query / readout
//   hover  -> live readout (while not pinned)
//   click  -> pin (freeze readout + marker); click again -> release
//   Picking is disabled while the deformed shape is displayed (the
//   raycaster sees the undeformed positions).
// ================================================================
// Hidden elements (the section-cut isolate toggle drops them via the
// elemVis attribute) must not be pickable -- picking geometry that
// isn't drawn reads as a glitch. The attribute is the single source
// of truth for what's on screen, so test it rather than asking the
// feature that set it.
function faceIsVisible(faceIndex) {
    if (!mesh) return false;
    var vis = mesh.geometry.getAttribute('elemVis');
    if (!vis) return true;
    return vis.array[faceIndex * 3] > 0.5;   // 3 verts per render triangle
}

function feaPick(clientX, clientY) {
    var haveBeams = window.FEABeams && FEABeams.mesh();
    if (!mesh && !haveBeams) return null;      // beam-only files have no shell mesh
    var rect = renderer.domElement.getBoundingClientRect();
    var ndc = new THREE.Vector2(
        ((clientX - rect.left) / rect.width) * 2 - 1,
        -((clientY - rect.top) / rect.height) * 2 + 1
    );
    feaRaycaster.setFromCamera(ndc, camera);
    var shellHit = null;
    if (mesh) {
        var hits = feaRaycaster.intersectObject(mesh);
        for (var i = 0; i < hits.length; i++) {
            if (hits[i].faceIndex == null) continue;
            if (!faceIsVisible(hits[i].faceIndex)) continue;
            shellHit = { faceIndex: hits[i].faceIndex, point: hits[i].point, distance: hits[i].distance };
            break;
        }
    }
    var beamHit = window.FEABeams ? FEABeams.pick(feaRaycaster) : null;
    var hit = shellHit;
    if (beamHit && (!hit || beamHit.distance < hit.distance)) hit = beamHit;
    // overlays (model rail): nearest visible member wins
    var ovHit = window.FEAOverlays ? FEAOverlays.pick(feaRaycaster) : null;
    if (ovHit && (!hit || ovHit.distance < hit.distance)) hit = ovHit;
    return hit;
}

// Picked point -> readout text, adding the exporter's recenter offset (sidecar
// units.worldOffset) so the readout shows world/plant coordinates.
function roPosText(p) {
    var o = (window.FEAFeatures && FEAFeatures.worldOffset) ? FEAFeatures.worldOffset() : null;
    var r = viewRecenter;
    var x = p.x + (o ? o[0] : 0) + (r ? r[0] : 0);
    var y = p.y + (o ? o[1] : 0) + (r ? r[1] : 0);
    var z = p.z + (o ? o[2] : 0) + (r ? r[2] : 0);
    return x.toFixed(2) + ', ' + y.toFixed(2) + ', ' + z.toFixed(2);
}

// Collapsible readout: the header toggles (remembered); collapsed, the value sits
// beside the title (2026-10-02).
(function () {
    var panel = document.getElementById('roPanel'), head = document.getElementById('roHead');
    var mini = document.getElementById('roMini'), tog = document.getElementById('roTog');
    if (!panel || !head) return;
    var collapsed = true;
    try { collapsed = localStorage.getItem('pluto.roCollapsed') !== '0'; } catch (e) {}
    function apply() {
        panel.classList.toggle('collapsed', collapsed);
        tog.innerHTML = collapsed ? '&#9656;' : '&#9662;';
    }
    head.addEventListener('click', function () {
        collapsed = !collapsed;
        try { localStorage.setItem('pluto.roCollapsed', collapsed ? '1' : '0'); } catch (e) {}
        apply();
    });
    var val = document.getElementById('roValue');
    function mirror() { mini.textContent = val.textContent === '—' ? '' : val.textContent; }
    if (val && window.MutationObserver) new MutationObserver(mirror).observe(val, { childList: true, characterData: true, subtree: true });
    apply();
})();

function clearReadout() {
    elRoValue.textContent = '—';
    elRoValue.className = 'ro-value';
    elRoElem.textContent = '—';
    elRoNode.textContent = '—';
    elRoCorners.textContent = '—';
    elRoUV.textContent = '—';
    elRoPos.textContent = '—';
    lastQuery = null;
    if (elRoControllingRow) elRoControllingRow.style.display = 'none';
}

// Query under (x,y) and fill the readout. Returns the hit, or null.
function showReadout(clientX, clientY) {
    if (!feaModel) return null;
    var inDsr = inDsrMode();
    var inStr = inStrMode();
    if (inStr && !feaModel.strData) return null;
    // Geometry-only files have no LC data; still pickable (beams draw neutral).
    var beamsOnly = window.FEABeams && FEABeams.view();
    if (!inDsr && !inStr && !feaLCData && !beamsOnly) return null;

    var hit = feaPick(clientX, clientY);
    if (!hit) { clearReadout(); return null; }
    if (hit.overlay) { FEAOverlays.fillReadout(hit); return hit; }
    if (hit.beam) { FEABeams.fillReadout(hit); return hit; }
    var q = FEAQuery.query({
        model: feaModel, buildResult: feaBuild,
        lcData: inStr ? feaModel.strData : feaLCData,
        compIndex: inStr ? currentStr : currentComp,
        compStride: inStr ? strengthComponents().length : null,
        smoothing: smoothing,
        dsrMode: inDsr ? {
            value: envGlobalDsrValue,
            source: envGlobalDsrSource,
            lc: envGlobalDsrLC
        } : null
    }, hit.faceIndex, hit.point);
    lastQuery = q;

    var v = q.value;
    var comp = activeComponent();
    var unit = (inDsr || !comp || !comp.unit) ? '' : comp.unit;
    if (!inDsr && absValue && v === v) v = Math.abs(v);
    elRoValue.textContent = q.noData ? 'no data'
        : (absValue && !inDsr ? '|' + fmt(v, 6) + '|' : fmt(v, 6)) + (unit ? ' ' + unit : '');
    elRoValue.className = 'ro-value' + (q.noData ? ' ro-nodata' : '');
    var grp = window.FEAFeatures ? FEAFeatures.groupOf('shell', q.element) : null;
    var lbl = feaModel.labels ? feaModel.labels.get(q.element) : '';
    elRoElem.textContent = q.elementId + (lbl ? ' [' + lbl + ']' : '') + '  (idx ' + q.element + ', ' +
        (q.ncount === 4 ? 'quad' : 'tri') + (grp ? ', group: ' + grp : '') + ')';
    var nIdx = feaBuild.elemCorners[q.element * 4 + q.nearestCorner];
    var nlbl = feaModel.nodeLabels ? feaModel.nodeLabels.get(nIdx) : '';
    elRoNode.textContent = q.nearestNodeId + (nlbl ? ' [' + nlbl + ']' : '');
    elRoCorners.textContent = q.cornerNodeIds.join(', ');
    elRoUV.textContent = q.u.toFixed(4) + ', ' + q.v.toFixed(4);
    elRoPos.textContent = roPosText(q.point);

    if (q.isDsr && q.cornerSources && elRoControllingRow) {
        var comps = feaModel.meta.components;
        elRoControlling.textContent = q.cornerSources.map(function (s) {
            return comps[s] ? comps[s].name : '?';
        }).join(', ');
        elRoControllingRow.style.display = '';
    } else if (currentEnvelope && envelopeLcArray() && elRoControllingRow) {
        // Which LC produced this envelope value at the nearest corner.
        var arr = envelopeLcArray();
        var idx = FEABinary.fieldIndex(feaModel.header, q.element, q.nearestCorner, currentComp);
        elRoControlling.textContent = lcFullName(arr[idx]) +
            ' (corner ' + q.nearestNodeId + ')';
        elRoControlling.title = feaSet ? feaSet.lcLongName(arr[idx]) : '';
        elRoControllingRow.style.display = '';
    } else if (elRoControllingRow) {
        elRoControllingRow.style.display = 'none';
    }
    return hit;
}

function envelopeLcArray() {
    if (!envBufs) return null;
    return currentEnvelope === 'min' ? envBufs.minLC
         : currentEnvelope === 'max' ? envBufs.maxLC
         : currentEnvelope === 'abs' ? envBufs.absLC
         : null;
}

function updateRoMode() {
    elRoMode.textContent = deformBlocked()
        ? 'picking disabled while deformed'
        : pinned
            ? 'pinned — click to release'
            : 'hover = live · click to pin · ctrl+click = focus · dblclick = fly';
}

function setPinned(on) {
    pinned = on;
    if (feaMarker) feaMarker.visible = on;
    updateRoMode();
    if (!on) hideCalcCard();
    needsRender = true;
}

renderer.domElement.addEventListener('pointerdown', function (e) {
    pointerDown = { x: e.clientX, y: e.clientY };
});

renderer.domElement.addEventListener('pointermove', function (e) {
    if (pinned || e.buttons !== 0 || deformBlocked()) return;
    showReadout(e.clientX, e.clientY);
});

renderer.domElement.addEventListener('pointerup', function (e) {
    var dn = pointerDown;
    pointerDown = null;
    if (!dn || e.button !== 0) return;
    if (Math.hypot(e.clientX - dn.x, e.clientY - dn.y) > 5) return;   // camera drag
    if (deformBlocked()) return;

    if (e.ctrlKey) {
        var hitPt = feaPick(e.clientX, e.clientY);
        if (hitPt) {
            // An armed tool steals the Ctrl+click (arming one disarms the other).
            if (window.FEAPredicates && FEAPredicates.wantsPick()) {
                FEAPredicates.placePick(hitPt);
            } else if (window.FEASectionCut && FEASectionCut.wantsPick()) {
                FEASectionCut.placeCenter(hitPt);
            } else {
                tweenFocusTo(hitPt.point);
            }
        }
        return;
    }

    if (pinned) {
        setPinned(false);
        showReadout(e.clientX, e.clientY);     // resume live under the cursor
    } else {
        var hit = showReadout(e.clientX, e.clientY);
        if (hit) {
            feaMarker.position.copy(hit.point);
            var s = orbScale();
            if (s > 0) feaMarker.scale.setScalar(s);
            setPinned(true);
            if (lastQuery) {
                if (inDsrMode()) showCalcCard(lastQuery);       // includes strengths
                else showPinCard(lastQuery);
            }
        }
    }
});

renderer.domElement.addEventListener('dblclick', function (e) {
    if (e.button !== 0 || deformBlocked()) return;
    var hit = feaPick(e.clientX, e.clientY);
    if (hit) flyFocusTo(hit.point);
});

// ================================================================
// Calc-review card: pinned in Global DSR, show the controlling check
// + controlling LC at the nearest corner, that LC's full DSR-check
// set and constituent stress components at that corner, and the
// controlling LC's fields at that corner grouped by kind. The record
// is one element-record slice (a few hundred bytes) -- nothing extra
// is resident.
// ================================================================
var calcCardToken = 0;

function hideCalcCard() {
    calcCardToken++;
    if (elCalcCard) { elCalcCard.style.display = 'none'; elCalcCard.innerHTML = ''; }
}

function ccRow(tbl, label, value, cls, win) {
    var tr = document.createElement('tr');
    if (win) tr.className = 'cc-win';
    var td1 = document.createElement('td');
    td1.textContent = label;
    var td2 = document.createElement('td');
    td2.className = 'cc-num' + (cls ? ' ' + cls : '');
    td2.textContent = value;
    tr.appendChild(td1);
    tr.appendChild(td2);
    tbl.appendChild(tr);
}

function ccSub(host, text) {
    var d = document.createElement('div');
    d.className = 'cc-sub';
    d.textContent = text;
    host.appendChild(d);
}

// Design strengths (phi-factored) at one corner. LC-independent, so
// there is no per-LC dimension to pick. Appended to every pin card
// when the file has a strength block; the block is lazily read on
// first use.
async function appendStrengthsTable(host, elem, k, token) {
    var strs = strengthComponents();
    if (!strs) return;
    var data;
    try {
        data = await FEABinary.readStrengths(feaModel);
    } catch (err) {
        var d = document.createElement('div');
        d.className = 'fea-hint';
        d.textContent = 'Strength read failed: ' + err.message;
        host.appendChild(d);
        return;
    }
    if (token !== calcCardToken) return;   // pin changed while reading
    ccSub(host, 'Design Strengths (LC-independent)');
    var t = document.createElement('table');
    var base = (elem * feaModel.header.maxCorners + k) * strs.length;
    strs.forEach(function (c, i) {
        ccRow(t, c.name + (c.unit ? ' [' + c.unit + ']' : ''), fmt(data[base + i], 4));
    });
    host.appendChild(t);
}

// Pin card outside DSR views: every stress value at this corner for
// the current view's data (the selected LC, or the active envelope's
// per-component values), plus the design-strength summary. Values come
// straight from the resident block -- no extra reads.
async function showPinCard(q) {
    if (!feaModel) return;
    var comps = feaModel.meta.components;
    var hasStress = comps.some(function (c) { return c.kind === 'stress'; });
    if (!hasStress && !strengthComponents()) return;
    var token = ++calcCardToken;
    elCalcCard.style.display = '';
    elCalcCard.innerHTML = '';
    var head = document.createElement('div');
    head.className = 'cc-head';
    head.textContent = 'Corner node ' + q.cornerNodeIds[q.nearestCorner] +
        '  (element ' + q.elementId + ')';
    elCalcCard.appendChild(head);
    if (smoothing) {
        var note = document.createElement('div');
        note.className = 'fea-hint';
        note.textContent = 'Smoothed view — card shows raw corner values.';
        elCalcCard.appendChild(note);
    }

    if (hasStress && feaLCData) {
        var h = feaModel.header;
        var base = q.element * h.maxCorners * h.cornerComponents +
                   q.nearestCorner * h.cornerComponents;
        var t = document.createElement('table');
        comps.forEach(function (c, ci) {
            if (c.kind !== 'stress') return;
            ccRow(t, c.name + (c.unit ? ' [' + c.unit + ']' : ''),
                fmt(feaLCData[base + ci], 4));
        });
        ccSub(elCalcCard, 'Stress @ ' +
            (currentEnvelope ? envelopeLabel(currentEnvelope) : lcFullName(currentLC)));
        elCalcCard.appendChild(t);
    }

    await appendStrengthsTable(elCalcCard, q.element, q.nearestCorner, token);
}

async function showCalcCard(q) {
    if (!feaModel || !envGlobalDsrValue) return;
    var token = ++calcCardToken;
    var comps = feaModel.meta.components;
    var h = feaModel.header;
    var k = q.nearestCorner;
    var elem = q.element;
    var checkIdx = q.cornerSources[k];
    var lcIdx = q.cornerLCs ? q.cornerLCs[k] : 0;
    var val = q.cornerValues[k];

    elCalcCard.style.display = '';
    elCalcCard.innerHTML = '';
    var head = document.createElement('div');
    head.className = 'cc-head';
    head.textContent = (val === val)
        ? (comps[checkIdx] ? comps[checkIdx].name : '?') + ' @ ' + lcFullName(lcIdx) +
          ' — ' + fmt(val, 4) + '  (corner node ' + q.cornerNodeIds[k] + ')'
        : 'No DSR data at this corner.';
    if (val === val && feaSet) head.title = feaSet.lcLongName(lcIdx);
    elCalcCard.appendChild(head);
    if (!(val === val)) {
        appendStrengthsTable(elCalcCard, elem, k, token);
        return;
    }
    if (smoothing) {
        var note = document.createElement('div');
        note.className = 'fea-hint';
        note.textContent = 'Smoothed view — card shows raw corner records.';
        elCalcCard.appendChild(note);
    }

    var loading = document.createElement('div');
    loading.className = 'fea-hint';
    loading.textContent = 'Reading controlling LC record…';
    elCalcCard.appendChild(loading);

    var rec;
    try {
        rec = await feaSet.readElementRecord(lcIdx, elem);
    } catch (err) {
        loading.textContent = 'Record read failed: ' + err.message;
        return;
    }
    if (token !== calcCardToken) return;   // pin changed while reading
    loading.remove();

    var cc = h.cornerComponents;
    var base = k * cc;

    ccSub(elCalcCard, 'DSR checks @ ' + lcFullName(lcIdx));
    var t1 = document.createElement('table');
    dsrIndices.forEach(function (ci) {
        var v = rec[base + ci];
        ccRow(t1, comps[ci].name, fmt(v, 4),
            (v === v && v >= 1.0) ? 'cc-alarm' : '', ci === checkIdx);
    });
    elCalcCard.appendChild(t1);

    // Every other field at this corner in the controlling LC, one table
    // per kind -- stresses, displacements, and any generic kinds (e.g.
    // constituent preDSR checks), so combined checks can be traced back
    // to whatever the writer recorded alongside them.
    orderedKinds(comps).forEach(function (kind) {
        if (kind === 'dsr') return;   // already shown above
        var t = document.createElement('table');
        var any = false;
        comps.forEach(function (c, ci) {
            if ((c.kind || 'unknown') !== kind) return;
            ccRow(t, c.name + (c.unit ? ' [' + c.unit + ']' : ''), fmt(rec[base + ci], 4));
            any = true;
        });
        if (!any) return;
        ccSub(elCalcCard, kindLabel(kind) + ' @ ' + lcFullName(lcIdx));
        elCalcCard.appendChild(t);
    });

    await appendStrengthsTable(elCalcCard, elem, k, token);
}

// ================================================================
// Deformed shape
// ================================================================
function deformAvailable() {
    return !!(feaModel && feaModel.meta.dispVector && !currentEnvelope && feaLCData);
}

function updateDeformAvailability() {
    var ok = deformAvailable();
    elDeformSection.classList.toggle('fea-disabled', !ok);
    if (!ok && deform.enabled) setDeformEnabled(false);
    var hint = !feaModel ? '' :
        !feaModel.meta.dispVector ? 'No displacement vector in this file (metadata displacementVector / Translation X-Y-Z names).' :
        currentEnvelope ? 'Envelopes mix LCs per corner — no coherent displacement field to deform by. Pick a load case.' :
        '';
    if (hint) elDeformSection.title = hint; else elDeformSection.removeAttribute('title');
}

// (Re)write the dispVec attributes from the current LC.
function refreshDispVecs() {
    if (!deformAvailable()) return;
    var edgeAttr = feaEdges ? feaEdges.geometry.getAttribute('dispVec') : null;
    deformMaxDisp = FEAAttributes.updateDispVecs(
        feaBuild, feaModel, feaLCData, feaModel.meta.dispVector, edgeAttr);
    if (window.FEABeams) deformMaxDisp = Math.max(deformMaxDisp, FEABeams.refreshDispVecs());
    deformLCLoaded = currentLC;
}

function currentDispScale(now) {
    if (!deform.enabled) return 0;
    if (!deform.animating) return deform.scale;
    return deform.scale * Math.sin(2 * Math.PI * deform.speed * now / 1000);
}

function setDispUniforms(v) {
    if (feaMaterial) feaMaterial.uniforms.dispScale.value = v;
    if (feaEdgeMaterial) feaEdgeMaterial.uniforms.dispScale.value = v;
    if (flashMesh) flashMesh.material.uniforms.dispScale.value = v;
    if (window.FEABeams) FEABeams.setDispScale(v);
}

function setDeformEnabled(on) {
    if (on && !deformAvailable()) on = false;
    deform.enabled = on;
    elDefEnable.checked = on;
    if (on) {
        if (deformLCLoaded !== currentLC) refreshDispVecs();
        if (deformMaxDisp === 0) log('Deformation: displacements are all zero in this LC.');
        // First enable (until the user edits the scale): pick a sensible
        // exaggeration automatically instead of a fixed default.
        if (deformScaleAuto && deformMaxDisp > 0) autoDeformScale();
        setPinned(false);
        clearReadout();
    }
    setDispUniforms(currentDispScale(performance.now()));
    updateRoMode();
    updateViewCaption();
    needsRender = true;
}

function setDeformScale(v) {
    if (!(v === v) || v < 0) return;
    deform.scale = v;
    elDefScale.value = String(v);
    // Slider range grows to fit out-of-range entries ("variable max").
    var max = parseFloat(elDefSlider.max);
    if (v > max) elDefSlider.max = String(Math.ceil(v * 1.5));
    elDefSlider.value = String(v);
    if (deform.enabled && !deform.animating)
        setDispUniforms(currentDispScale(performance.now()));
    updateViewCaption();
    needsRender = true;
}

function autoDeformScale() {
    if (!feaModel) return;
    if (deformLCLoaded !== currentLC && deformAvailable()) refreshDispVecs();
    var sph = modelSphere();
    if (!sph || deformMaxDisp <= 0) { log('Auto scale: no displacements in this LC.'); return; }
    var s = 0.05 * sph.radius / deformMaxDisp;
    // round to 2 significant figures
    var mag = Math.pow(10, Math.floor(Math.log10(s)) - 1);
    s = Math.round(s / mag) * mag;
    setDeformScale(s);
    log('Deformation scale ×' + fmt(s, 2) + '  (peak |d| = ' + fmt(deformMaxDisp, 4) + ').');
}

// ================================================================
// Alarm + flash
// ================================================================
function applyAlarmUniform() {
    var cat = inCatMode();
    if (feaMaterial) {
        // Category ordinals must never trip the alarm compare.
        feaMaterial.uniforms.alarmThreshold.value =
            (alarmEnabled && !cat) ? alarmThreshold : 0;
    }
    if (flashMesh) {
        flashMesh.material.uniforms.alarmThreshold.value =
            alarmEnabled ? alarmThreshold : 0;
    }
    elBtnFlash.disabled = !alarmEnabled || cat;
    drawLegend();
    needsRender = true;
}

function startFlash() {
    if (!flashMesh || !alarmEnabled || inCatMode()) return;
    flashMesh.material.uniforms.alarmThreshold.value = alarmThreshold;
    flashMesh.material.uniforms.uAbs.value = absValue ? 1 : 0;
    flashMesh.material.uniforms.dispScale.value = currentDispScale(performance.now());
    flashUntil = performance.now() + FLASH_MS;
    flashMesh.visible = true;
    needsRender = true;
}

function updateFlash(now) {
    // Three raised-cosine pulses over FLASH_MS, peaking at 0.85 alpha.
    var t = 1 - (flashUntil - now) / FLASH_MS;
    var a = 0.85 * (0.5 - 0.5 * Math.cos(2 * Math.PI * 3 * t));
    flashMesh.material.uniforms.uFlashAlpha.value = a;
    if (deform.enabled)
        flashMesh.material.uniforms.dispScale.value = currentDispScale(now);
}

// ================================================================
// Panel controls
// ================================================================
// The file input is driven by files.js (FEAFiles): one picker for .bin + .json,
// File System Access handles when available, "Save features" writes back.

elBtnDemo.addEventListener('click', function () {
    log('Generating demo models...');
    loadDemo();
});

// Two variant models: identical geometry/strengths/LC names, different
// field values -- exercises the multi-model path out of the box.
async function loadDemo() {
    // Two v4 files (plate shells + beam frame) with identical geometry
    // and different field values -- exercises the multi-model path.
    log('Generating demo models...');
    var soft = await FEASample.buildSampleBlobV4(0);
    var stiff = await FEASample.buildSampleBlobV4(1);
    await loadModels([
        { file: soft,  name: 'SyntheticDemo_Soft' },
        { file: stiff, name: 'SyntheticDemo_Stiff' }
    ]);
    // say so: its made-up fields and the shell left blank on purpose (sampleModel.js) read like a model problem
    log('Demo: a SYNTHETIC plate and frame with made-up result fields (one shell is left blank on purpose to show ' +
        'missing data). Not an analysis. Open a .bin to see a real model.');
    if (feaModel && feaModel.unified && window.FEAFeatures) {
        FEAFeatures.setEnvelope(FEASample.buildSampleFeatures(feaModel.unified), 'PlateDemo.features.json');
    }
}

elBtnFit.addEventListener('click', fitView);
elOrtho.addEventListener('change', function () { setProjection(this.checked); });
if (elZUp) elZUp.addEventListener('change', function () { setZUp(this.checked); });

elBtnFind.addEventListener('click', doFind);
elFindId.addEventListener('keydown', function (e) {
    if (e.key === 'Enter') doFind();
});

elBtnComputeEnv.addEventListener('click', computeEnvelopes);
elBtnEnvMin.addEventListener('click', function () { currentEnvelope === 'min' ? selectLC(currentLC) : selectEnvelope('min'); });
elBtnEnvMax.addEventListener('click', function () { currentEnvelope === 'max' ? selectLC(currentLC) : selectEnvelope('max'); });
elBtnEnvAbs.addEventListener('click', function () { currentEnvelope === 'abs' ? selectLC(currentLC) : selectEnvelope('abs'); });
elBtnEnvDsr.addEventListener('click', function () {
    (currentEnvelope === 'dsr' && !dsrCategorical) ? selectLC(currentLC) : selectEnvelope('dsr', false);
});
elBtnEnvCat.addEventListener('click', function () {
    (currentEnvelope === 'dsr' && dsrCategorical) ? selectEnvelope('dsr', false) : selectEnvelope('dsr', true);
});

elLC.addEventListener('change', function () {
    selectLC(parseInt(this.value, 10));
});
elBtnLCPrev.addEventListener('click', function () { stepLC(-1); });
elBtnLCNext.addEventListener('click', function () { stepLC(1); });

elKind.addEventListener('change', function () {
    kindFilter = this.value;
    populateComponentSelect();
    // Keep the displayed field selected if the filter still lists it;
    // otherwise switch to the first field of the chosen type.
    var want = inStrMode() ? 'str:' + currentStr : String(currentComp);
    var present = Array.prototype.some.call(elComp.options, function (o) {
        return o.value === want;
    });
    if (present) {
        elComp.value = want;
    } else if (elComp.options.length > 0) {
        elComp.value = elComp.options[0].value;
        elComp.dispatchEvent(new Event('change'));
    }
});

elComp.addEventListener('change', function () {
    // Design-strength options are 'str:<i>'; they have their own entry path.
    if (this.value.indexOf('str:') === 0) {
        enterStrengthView(parseInt(this.value.slice(4), 10));
        return;
    }
    currentComp = parseInt(this.value, 10);
    var wasCap = currentStr >= 0;
    currentStr = -1;
    // Picking a component drops out of Global DSR mode -- it's a
    // component-less view, so component selection means "go back to
    // the regular flow on the most-recent LC".
    if (inDsrMode()) {
        currentEnvelope = null;
        dsrCategorical = false;
        setEnvActive(null);
        selectLC(currentLC);
        return;
    }
    if (wasCap) updateLCDim();
    applyComponentAndRange();
    var cc = feaModel && feaModel.meta.components[currentComp];
    if (cc) log('Component: ' + cc.name);
});


elColormap.addEventListener('change', function () {
    colormapName = this.value;
    if (feaMaterial) {
        var old = texScalar;
        texScalar = FEAShaders.makeColormapTexture(colormapName);
        if (!inCatMode()) feaMaterial.uniforms.colormap.value = texScalar;
        if (old) old.dispose();
    }
    drawLegend();
    needsRender = true;
});

elAuto.addEventListener('change', function () {
    autoRange = this.checked;
    updateRangeInputsDisabled();
    if (autoRange) applyComponentAndRange();
});

function applyManualRange() {
    if (autoRange || inCatMode()) return;
    var lo = parseFloat(elMin.value.replace(/,/g, ''));
    var hi = parseFloat(elMax.value.replace(/,/g, ''));
    if (isNaN(lo) || isNaN(hi) || hi <= lo) {
        elMin.value = fmt(vMin, 4);
        elMax.value = fmt(vMax, 4);
        return;
    }
    vMin = lo; vMax = hi;
    if (feaMaterial) {
        feaMaterial.uniforms.vMin.value = vMin;
        feaMaterial.uniforms.vMax.value = vMax;
    }
    drawLegend();
    needsRender = true;
}
elMin.addEventListener('change', applyManualRange);
elMax.addEventListener('change', applyManualRange);

elEdges.addEventListener('change', function () {
    if (feaEdges) feaEdges.visible = this.checked;
    needsRender = true;
});

elSmooth.addEventListener('change', function () {
    smoothMode = this.value;
    smoothing = smoothMode !== 'none';
    applyComponentAndRange();
});

elAbs.addEventListener('change', function () {
    absValue = this.checked;
    applyComponentAndRange();
});

elAlarm.addEventListener('change', function () {
    alarmEnabled = this.checked;
    elAlarmVal.disabled = !alarmEnabled;
    if (!alarmEnabled && isDsrView()) dsrAlarmOptOut = true;
    alarmAutoOn = false;
    applyAlarmUniform();
});
elAlarmVal.addEventListener('change', function () {
    var t = parseFloat(this.value);
    if (!isNaN(t) && isFinite(t)) {
        alarmThreshold = t;
        alarmAutoOn = false;    // user took over; don't auto-restore
        applyAlarmUniform();
        } else {
        this.value = alarmThreshold;
    }
});
elBtnFlash.addEventListener('click', startFlash);

// ---- deformation controls ----------------------------------------
elDefEnable.addEventListener('change', function () {
    setDeformEnabled(this.checked);
});
elDefScale.addEventListener('change', function () {
    var v = parseFloat(this.value.replace(/,/g, ''));
    if (isNaN(v) || v < 0) { this.value = String(deform.scale); return; }
    deformScaleAuto = false;
    setDeformScale(v);
});
elDefSlider.addEventListener('input', function () {
    deformScaleAuto = false;
    setDeformScale(parseFloat(this.value));
});
elBtnDefAuto.addEventListener('click', autoDeformScale);
elDefAnimate.addEventListener('change', function () {
    deform.animating = this.checked;
    if (!deform.animating) setDispUniforms(currentDispScale(performance.now()));
    updateViewCaption();
    needsRender = true;
});
elDefSpeed.addEventListener('change', function () {
    var v = parseFloat(this.value);
    if (isNaN(v) || v <= 0 || v > 10) { this.value = String(deform.speed); return; }
    deform.speed = v;
});

// ---- keyboard -----------------------------------------------------
window.addEventListener('keydown', function (e) {
    var tag = e.target && e.target.tagName;
    if (tag === 'INPUT' || tag === 'SELECT' || tag === 'TEXTAREA') return;
    if (e.key === 'ArrowLeft')  { stepLC(-1); e.preventDefault(); }
    if (e.key === 'ArrowRight') { stepLC(1);  e.preventDefault(); }
    if (e.key === 'Escape' && pinned) setPinned(false);
});

// ================================================================
// Startup: auto-load the demo model so the viewer is never blank.
// ================================================================
elMin.disabled = true;
elMax.disabled = true;
elAlarmVal.disabled = true;
elBtnFlash.disabled = true;
setPinned(false);
clearReadout();
drawLegend();
updateViewCaption();

// URL-driven load: index.html?bin=<url>[&features=<url>] (same-origin fetch;
// run_sap.py serves the repo root and opens this). Falls back to the demo.
(function () {
    var q = new URLSearchParams(window.location.search);
    var binUrl = q.get('bin');
    if (!binUrl) { loadDemo(); return; }
    var base = function (u) { return decodeURIComponent(u.split('/').pop()); };
    log('Loading ' + base(binUrl) + ' ...');
    fetch(binUrl).then(function (r) {
        if (!r.ok) throw new Error(r.status + ' ' + r.statusText + ' for ' + binUrl);
        return r.blob();
    }).then(function (blob) {
        return loadModels([{ file: blob, name: trimExt(base(binUrl)) }]);
    }).then(function () {
        var fUrl = q.get('features');
        if (!fUrl || !window.FEAFeatures) return;
        return fetch(fUrl).then(function (r) {
            if (!r.ok) throw new Error(r.status + ' ' + r.statusText + ' for ' + fUrl);
            return r.blob();
        }).then(function (blob) {
            return FEAFeatures.loadFile(new File([blob], base(fUrl), { type: 'application/json' }));
        });
    }).then(async function () {
        // overlays: &ov=<bin url>[,<features url>] (repeatable)
        var ovs = q.getAll('ov');
        for (var i = 0; i < ovs.length && window.FEAOverlays; i++) {
            var parts = ovs[i].split(',');
            var bin = await (await fetch(parts[0])).blob();
            var js = parts[1] ? new File([await (await fetch(parts[1])).blob()], base(parts[1])) : null;
            await FEAOverlays.add(bin, js, trimExt(base(parts[0])));
        }
        if (ovs.length && window.FEAOverlays) FEAOverlays.render();
    }).catch(function (err) {
        log('URL load failed: ' + err.message);
        loadDemo();
    });
})();
