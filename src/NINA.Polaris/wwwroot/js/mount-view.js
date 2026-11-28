// Polaris Astro Controller
// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or (at your
// option) any later version.
//
// This program is distributed in the hope that it will be useful, but WITHOUT
// ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or
// FITNESS FOR A PARTICULAR PURPOSE. See the GNU Affero General Public License
// for more details. You should have received a copy of the license along with
// this program. If not, see <https://www.gnu.org/licenses/>.

// MOUNTVIEW: live 3D model of the mount for the Focus Stars page.
//
// No 3D library: the app has no build pipeline and runs offline on the rig,
// and a few hundred flat-shaded triangles sorted back to front on a 2D canvas
// are plenty for one mount. Redraws happen only while the model is moving or
// the view is being turned, so an idle tab costs nothing.
//
// World frame, millimetres: x = East, y = Up, z = South (right-handed).
//
// The pose comes from the mount's own alt/az (already on the status socket)
// plus the site latitude and the reported pier side, so the drawing follows
// what the mount says rather than re-deriving it from RA and sidereal time.
//
// Exposed as window.PolarisMountView: create(canvas) returns a viewer, and
// .math holds the pure pose functions (also module.exports, for node).
(function (root) {
    'use strict';

    // ---- vectors -------------------------------------------------------
    const add = (a, b) => [a[0] + b[0], a[1] + b[1], a[2] + b[2]];
    const sub = (a, b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];
    const mul = (a, s) => [a[0] * s, a[1] * s, a[2] * s];
    const dot = (a, b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
    const cross = (a, b) => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
    const len = a => Math.hypot(a[0], a[1], a[2]);
    const norm = a => { const l = len(a); return l < 1e-12 ? [0, 0, 0] : mul(a, 1 / l); };
    const rad = d => d * Math.PI / 180;

    const EAST = [1, 0, 0], UP = [0, 1, 0], NORTH = [0, 0, -1];

    // Rodrigues: v turned by angle a (radians) about unit axis k.
    function rotate(v, k, a) {
        const c = Math.cos(a), s = Math.sin(a);
        return add(add(mul(v, c), mul(cross(k, v), s)), mul(k, dot(k, v) * (1 - c)));
    }
    // Signed angle from a to b about unit axis k (both perpendicular to k).
    function angleAbout(a, b, k) {
        return Math.atan2(dot(cross(a, b), k), dot(a, b));
    }
    // Shortest signed difference b - a, wrapped to (-pi, pi].
    function wrap(d) {
        d = (d + Math.PI) % (2 * Math.PI);
        if (d < 0) d += 2 * Math.PI;
        return d - Math.PI;
    }

    // ---- pose math (pure) -------------------------------------------------

    /** Unit pointing vector for altitude/azimuth in degrees (azimuth from
     *  north through east). */
    function directionFromAltAz(altDeg, azDeg) {
        const alt = rad(altDeg), az = rad(azDeg);
        const h = Math.cos(alt);
        return add(add(mul(EAST, h * Math.sin(az)), mul(NORTH, h * Math.cos(az))), mul(UP, Math.sin(alt)));
    }

    /** Unit vector toward the north celestial pole (below the horizon in the
     *  southern hemisphere). */
    function northPole(latDeg) {
        const p = rad(latDeg);
        return add(mul(NORTH, Math.cos(p)), mul(UP, Math.sin(p)));
    }

    /** The polar axis points at the pole above the horizon. */
    function polarAxis(latDeg) {
        const p = northPole(latDeg);
        return latDeg >= 0 ? p : mul(p, -1);
    }

    // Reference for the RA angle: the Dec axis level.
    function decAxisZero(axis) {
        const d = cross(axis, UP);
        return len(d) < 1e-6 ? EAST : norm(d);
    }
    // Home: OTA on the pole, Dec axis straight up the meridian, so the
    // counterweight hangs down.
    function decAxisHome(axis) {
        const d = sub(UP, mul(axis, dot(axis, UP)));
        return len(d) < 1e-6 ? EAST : norm(d);
    }

    /** German equatorial pose. Returns the unit polar axis, Dec axis (from
     *  the RA axis toward the OTA; the counterweight is on the other side)
     *  and OTA direction, plus the two mechanical angles that produce them.
     *
     *  The Dec axis is perpendicular to both the polar axis and the target,
     *  so only its sign is free, and that sign is the pier side. ASCOM
     *  pierEast is the OTA on the east of the pier looking west, the normal
     *  state for a western target: with D+ = norm(NCP x target) that is +D+
     *  in both hemispheres. An unknown side takes the counterweight-down
     *  choice. */
    function gemPose(latDeg, altDeg, azDeg, pierSide) {
        const axis = polarAxis(latDeg);
        const ota = directionFromAltAz(altDeg, azDeg);
        let dPlus = cross(northPole(latDeg), ota);
        // On the pole the Dec axis can point anywhere: take the home pose.
        dPlus = len(dPlus) < 1e-6 ? decAxisHome(axis) : norm(dPlus);
        let sign;
        if (pierSide === 'pierEast') sign = 1;
        else if (pierSide === 'pierWest') sign = -1;
        else sign = dPlus[1] >= 0 ? 1 : -1;
        const dec = mul(dPlus, sign);
        return {
            axis, dec, ota,
            ra: angleAbout(decAxisZero(axis), dec, axis),
            decAngle: angleAbout(axis, ota, dec)
        };
    }

    // Equatorial frame at a latitude: m is the equator on the meridian
    // (HA 0), e is east, p the north celestial pole; (m, e, p) is right-handed.
    function eqFrame(latDeg) {
        const f = rad(latDeg);
        return { m: add(mul([0, 0, 1], Math.sin(f)), mul(UP, Math.cos(f))), e: EAST, p: northPole(latDeg) };
    }

    /** Unit pointing vector for hour angle (hours, positive west) and Dec. */
    function directionFromHaDec(latDeg, haHours, decDeg) {
        const { m, e, p } = eqFrame(latDeg);
        const h = rad(haHours * 15), d = rad(decDeg);
        return add(add(mul(m, Math.cos(d) * Math.cos(h)), mul(e, -Math.cos(d) * Math.sin(h))), mul(p, Math.sin(d)));
    }

    /** Precession between J2000 and the equator and equinox of the date
     *  `ms` (IAU 1976; well under an arcsecond for decades either side of
     *  2000). The catalogue, the Moon and the mount's RA/Dec are J2000; the
     *  sky is placed by sidereal time and the mount's hour angle, both of
     *  date. Mixing the two put the map ~0.36° off the real sky (2026), and
     *  on the pole, where RA turns fast, read hours out.
     *  toDate / toJ2000 take and return { ra (hours), dec (degrees) }. */
    function precession(ms) {
        const T = (ms / 86400000 + 2440587.5 - 2451545.0) / 36525;
        const as = Math.PI / 180 / 3600;
        const zeta = (2306.2181 * T + 0.30188 * T * T + 0.017998 * T * T * T) * as;
        const z = (2306.2181 * T + 1.09468 * T * T + 0.018203 * T * T * T) * as;
        const th = (2004.3109 * T - 0.42665 * T * T - 0.041833 * T * T * T) * as;
        const ct = Math.cos(th), sn = Math.sin(th);
        const out = (ra, dec) => ({ ra: ((ra * 12 / Math.PI) % 24 + 24) % 24,
                                    dec: Math.asin(Math.max(-1, Math.min(1, dec))) * 180 / Math.PI });
        return {
            toDate(raH, decD) {
                const a = raH * Math.PI / 12 + zeta, d = rad(decD), cd = Math.cos(d), sd = Math.sin(d);
                const A = cd * Math.sin(a), B = ct * cd * Math.cos(a) - sn * sd, C = sn * cd * Math.cos(a) + ct * sd;
                return out(Math.atan2(A, B) + z, C);
            },
            toJ2000(raH, decD) {
                const a = raH * Math.PI / 12 - z, d = rad(decD), cd = Math.cos(d), sd = Math.sin(d);
                const A = cd * Math.sin(a), B = ct * cd * Math.cos(a) + sn * sd, C = -sn * cd * Math.cos(a) + ct * sd;
                return out(Math.atan2(A, B) - zeta, C);
            }
        };
    }

    /** Hour angle (hours, -12..12) and Dec (degrees) of a world direction:
     *  the inverse of directionFromHaDec. */
    function haDecFromDirection(latDeg, d) {
        const { m, e, p } = eqFrame(latDeg);
        const dec = Math.asin(Math.max(-1, Math.min(1, dot(d, p)))) * 180 / Math.PI;
        const ha = Math.atan2(-dot(d, e), dot(d, m)) * 12 / Math.PI;
        return { ha, dec };
    }

    /** German equatorial pose from hour angle and Dec: what the axes are
     *  actually doing. Preferred over gemPose because the Dec axis direction,
     *  norm(NCP x target) = cos(h) e + sin(h) m, depends on the hour angle
     *  alone. Near the pole the pointing barely changes while the RA axis
     *  turns, so alt/az cannot show RA motion there and cannot tell where
     *  the counterweight is; the hour angle can. Same pier side rule as
     *  gemPose. */
    function gemPoseEq(latDeg, haHours, decDeg, pierSide) {
        const axis = polarAxis(latDeg);
        const { m, e } = eqFrame(latDeg);
        const h = rad(haHours * 15);
        const dPlus = norm(add(mul(e, Math.cos(h)), mul(m, Math.sin(h))));
        let sign;
        if (pierSide === 'pierEast') sign = 1;
        else if (pierSide === 'pierWest') sign = -1;
        else sign = dPlus[1] >= 0 ? 1 : -1;
        const dec = mul(dPlus, sign);
        const ota = directionFromHaDec(latDeg, haHours, decDeg);
        return {
            axis, dec, ota,
            ra: angleAbout(decAxisZero(axis), dec, axis),
            decAngle: angleAbout(axis, ota, dec)
        };
    }

    /** Rebuild the GEM vectors from the two mechanical angles: what the
     *  animation interpolates, so a meridian flip swings the way a real
     *  mount does instead of cutting straight through it. */
    function gemFromAngles(latDeg, ra, decAngle) {
        const axis = polarAxis(latDeg);
        const dec = rotate(decAxisZero(axis), axis, ra);
        return { axis, dec, ota: rotate(axis, dec, decAngle) };
    }

    /** Alt-az pose from the two axis angles (degrees). The altitude axis is
     *  level, to the right of the azimuth the scope faces. */
    function altAzPose(altDeg, azDeg) {
        const front = directionFromAltAz(0, azDeg);
        return { front, side: norm(cross(front, UP)), ota: directionFromAltAz(altDeg, azDeg) };
    }

    /** Alt-az pose from the mechanical angles the viewer stores and eases,
     *  [az, alt] in radians (the alt-az counterpart of gemFromAngles).
     *  altAzPose takes degrees; passing it these radians unconverted drew
     *  the scope nearly level and facing north whatever the mount said. */
    function altAzFromAngles(az, alt) {
        return altAzPose(alt * 180 / Math.PI, az * 180 / Math.PI);
    }

    const math = { directionFromAltAz, directionFromHaDec, haDecFromDirection, precession, northPole, polarAxis, gemPose, gemPoseEq, gemFromAngles,
                   altAzPose, altAzFromAngles, wrap };

    // ---- geometry ---------------------------------------------------------
    //
    // A part is a list of triangles in its own frame; each frame maps local
    // (u, v, w) onto three world axes plus an origin, so the rotating groups
    // are built once and only their frames change.

    const SEG = 20;

    function makeFrame(origin, u, v, w) { return { o: origin, u, v, w }; }
    const IDENTITY = makeFrame([0, 0, 0], [1, 0, 0], [0, 1, 0], [0, 0, 1]);
    function toWorld(f, p) {
        return add(f.o, add(add(mul(f.u, p[0]), mul(f.v, p[1])), mul(f.w, p[2])));
    }

    function tri(list, a, b, c, color) { list.push({ a, b, c, color }); }
    function quad(list, a, b, c, d, color) { tri(list, a, b, c, color); tri(list, a, c, d, color); }

    // Any two unit vectors perpendicular to unit k.
    function perpendiculars(k) {
        const t = Math.abs(k[1]) < 0.9 ? [0, 1, 0] : [1, 0, 0];
        const a = norm(cross(k, t));
        return [a, cross(k, a)];
    }

    /** Closed frustum from p0 (radius r0) to p1 (radius r1); caps take their
     *  own colours so the OTA's objective can read as glass. */
    function tube(list, p0, p1, r0, r1, color, cap0 = color, cap1 = color, seg = SEG) {
        const k = norm(sub(p1, p0));
        const [a, b] = perpendiculars(k);
        const ring = (p, r) => Array.from({ length: seg }, (_, i) => {
            const t = 2 * Math.PI * i / seg;
            return add(p, add(mul(a, r * Math.cos(t)), mul(b, r * Math.sin(t))));
        });
        const s0 = ring(p0, r0), s1 = ring(p1, r1);
        for (let i = 0; i < seg; i++) {
            const j = (i + 1) % seg;
            quad(list, s0[i], s0[j], s1[j], s1[i], color);
            if (cap0) tri(list, p0, s0[j], s0[i], cap0);
            if (cap1) tri(list, p1, s1[i], s1[j], cap1);
        }
    }

    /** Box from local corner lo to hi. */
    function box(list, lo, hi, color) {
        const [x0, y0, z0] = lo, [x1, y1, z1] = hi;
        const p = (x, y, z) => [x, y, z];
        quad(list, p(x0, y0, z1), p(x1, y0, z1), p(x1, y1, z1), p(x0, y1, z1), color); // +z
        quad(list, p(x1, y0, z0), p(x0, y0, z0), p(x0, y1, z0), p(x1, y1, z0), color); // -z
        quad(list, p(x1, y0, z1), p(x1, y0, z0), p(x1, y1, z0), p(x1, y1, z1), color); // +x
        quad(list, p(x0, y0, z0), p(x0, y0, z1), p(x0, y1, z1), p(x0, y1, z0), color); // -x
        quad(list, p(x0, y1, z1), p(x1, y1, z1), p(x1, y1, z0), p(x0, y1, z0), color); // +y
        quad(list, p(x0, y0, z0), p(x1, y0, z0), p(x1, y0, z1), p(x0, y0, z1), color); // -y
    }

    // Colour roles; the viewer maps each to the theme (red in night mode).
    const C = {
        leg: 'leg', head: 'head', body: 'body', dark: 'dark', steel: 'steel',
        weight: 'weight', ota: 'ota', shield: 'shield', glass: 'glass', mark: 'mark'
    };

    // Heights shared by both models: a tripod whose head sits at 1 m.
    const TRIPOD_TOP = 1000;
    const BASE_TOP = 1060;

    function buildTripod() {
        const t = [];
        for (let i = 0; i < 3; i++) {
            const a = rad(90 + 120 * i);
            const top = [70 * Math.cos(a), TRIPOD_TOP - 30, 70 * Math.sin(a)];
            const foot = [560 * Math.cos(a), 0, 560 * Math.sin(a)];
            tube(t, top, foot, 20, 16, C.leg, null, C.dark, 8);
            // Spreader, a third of the way down.
            const mid = add(top, mul(sub(foot, top), 0.55));
            tube(t, [0, mid[1], 0], mid, 7, 7, C.dark, null, null, 6);
        }
        tube(t, [0, TRIPOD_TOP - 60, 0], [0, TRIPOD_TOP, 0], 95, 95, C.head);
        tube(t, [0, TRIPOD_TOP, 0], [0, BASE_TOP, 0], 80, 80, C.body);
        return t;
    }

    // The OTA along local +u, centred on the u axis: a refractor with a dew
    // shield, so the open end is obvious from any angle.
    function buildOta(t, centreV) {
        const c = (u) => [u, centreV, 0];
        tube(t, c(-330), c(380), 62, 62, C.ota, C.dark, null);
        tube(t, c(380), c(560), 70, 70, C.shield, C.mark, C.glass);
        tube(t, c(-410), c(-330), 28, 28, C.dark);
        tube(t, c(-470), c(-410), 34, 34, C.dark);
        // Rings and dovetail.
        for (const u of [-120, 170]) tube(t, c(u - 14), c(u + 14), 69, 69, C.dark, C.dark, C.dark, 16);
        box(t, [-200, centreV - 62 - 24, -22], [260, centreV - 62, 22], C.dark);
        // Finder, on top.
        tube(t, [60, centreV + 98, 0], [260, centreV + 98, 0], 16, 16, C.dark, C.dark, C.glass, 10);
    }

    // German equatorial: fixed polar housing in a frame along the polar axis,
    // an RA group in (Dec axis, polar axis, Dec x polar), a Dec group in
    // (OTA, Dec axis, OTA x Dec).
    const GEM = {
        hinge: [0, 1120, 0],
        decAlongRa: 190,     // where the Dec axis crosses the polar axis
        saddle: 150          // saddle face, up the Dec axis
    };
    // Saddle plate 14, dovetail 24, tube radius 62: the OTA's centre line.
    GEM.otaOffset = GEM.saddle + 14 + 24 + 62;

    function buildGem() {
        const fixed = [];
        // Latitude hinge cheeks.
        box(fixed, [-84, BASE_TOP, -70], [-64, GEM.hinge[1] + 30, 70], C.body);
        box(fixed, [64, BASE_TOP, -70], [84, GEM.hinge[1] + 30, 70], C.body);
        const polar = [];
        // Ends just short of the Dec housing (radius 56 at decAlongRa).
        tube(polar, [0, -150, 0], [0, GEM.decAlongRa - 56 - 4 - 90, 0], 62, 62, C.body, C.dark, C.body);
        const ra = [];
        tube(ra, [-110, 0, 0], [GEM.saddle - 30, 0, 0], 56, 56, C.body, C.body, C.body);
        tube(ra, [GEM.saddle - 30, 0, 0], [GEM.saddle, 0, 0], 44, 44, C.dark);
        tube(ra, [-110, 0, 0], [-470, 0, 0], 11, 11, C.steel, null, C.steel, 10);
        tube(ra, [-320, 0, 0], [-395, 0, 0], 78, 78, C.weight);
        tube(ra, [-460, 0, 0], [-480, 0, 0], 22, 22, C.dark, C.dark, C.dark, 10);
        const dec = [];
        box(dec, [-130, 0, -55], [130, 14, 55], C.dark);   // saddle, along the OTA
        buildOta(dec, GEM.otaOffset - GEM.saddle);
        return { fixed, polar, ra, dec };
    }

    // Single-arm alt-az (AZ-GTi style): the arm on the left, OTA on the right.
    const ALTAZ = { base: [0, BASE_TOP, 0], hubV: 330, otaSide: 95 };

    function buildAltAz() {
        const az = [];
        tube(az, [0, 0, 0], [0, 40, 0], 85, 85, C.body);
        box(az, [-45, 40, -115], [45, ALTAZ.hubV + 40, -55], C.body);
        tube(az, [0, ALTAZ.hubV, -115], [0, ALTAZ.hubV, -20], 48, 48, C.body, C.body, C.dark);
        const alt = [];
        box(alt, [-130, -24, -20], [130, 24, 10], C.dark);   // dovetail clamp face
        // The OTA is built dovetail-down; turn it a quarter about its own
        // axis (v -> w, w -> -v, a rotation, so the faces keep their winding)
        // to put the dovetail against the arm, then out along the altitude axis.
        const ota = [];
        buildOta(ota, 0);
        for (const t of ota) {
            for (const k of ['a', 'b', 'c']) t[k] = [t[k][0], -t[k][2], t[k][1] + ALTAZ.otaSide];
        }
        return { az, alt: alt.concat(ota) };
    }

    // ---- viewer -----------------------------------------------------------

    const PALETTE = {
        leg: [70, 76, 86], head: [52, 56, 64], body: [214, 218, 224], dark: [34, 36, 40],
        steel: [170, 176, 184], weight: [96, 100, 108], ota: [236, 238, 240],
        shield: [42, 44, 48], glass: [24, 40, 70], mark: null
    };

    function parseColor(s, fallback) {
        s = (s || '').trim();
        let m = s.match(/^#([0-9a-f]{3})$/i);
        if (m) return m[1].split('').map(h => parseInt(h + h, 16));
        m = s.match(/^#([0-9a-f]{6})$/i);
        if (m) return [0, 2, 4].map(i => parseInt(m[1].substr(i, 2), 16));
        m = s.match(/^rgba?\(([^)]+)\)$/i);
        if (m) return m[1].split(',').slice(0, 3).map(x => parseFloat(x));
        return fallback;
    }

    function create(canvas) {
        const ctx = canvas.getContext('2d');
        const model = { gem: buildGem(), altaz: buildAltAz(), tripod: buildTripod() };
        // The camera looks out at the sky (look.alt/az, degrees,
        // fov the vertical field) with the mount drawn at the bottom front,
        // seen from behind along the same azimuth.
        const look = { alt: 0, az: 180, fov: 154 };
        // One camera for the sky and the mount: the observer's eye is level
        // with the mount head, VIEW_DIST behind it along the look azimuth.
        // The head is then always on the horizon, seen at eye level, and the
        // OTA and its dashed line to the sky are one straight line.
        //
        // The sky is drawn stereographically, as Stellarium draws it: shapes
        // stay true out to 200° and more. It starts at eye level (horizon
        // straight across, flat ground below) and tilts freely like the SKY
        // tab; tilted, the horizon bends, up at the sides looking up and
        // down at the sides looking down. It may look down to 45° below the
        // horizon, so Reset view can put a low scope's reticle in the middle.
        //
        // The view's centre (where it looks, and where a level horizon runs)
        // sits VIEW_CENTRE of the way down the screen, so at eye level three
        // quarters of it is sky. fov is measured over the middle half-height,
        // so the top edge reaches further than fov/2: at the default 154° it
        // is at about 100° altitude, the zenith plus room for the toolbar.
        // The catch is that it cannot tilt far enough to bring the horizon
        // behind down to the top edge.
        const VIEW_DIST = 1600, MIN_FOV = 6;
        const MAX_FOV = 200, DEFAULT_FOV = 154, MIN_ALT = -45, VIEW_CENTRE = 0.75;
        const defaultLook = () => ({ alt: 0, az: st.lat >= 0 ? 0 : 180, fov: DEFAULT_FOV });
        function clampLook(o) {
            o.fov = Math.max(MIN_FOV, Math.min(MAX_FOV, o.fov));
            o.alt = Math.max(MIN_ALT, Math.min(89, o.alt));
            return o;
        }
        const sky = {
            stars: [],      // { hip, ra, dec, mag, bv, d: world unit vector }
            byHip: new Map(), lines: [], focus: [], horizon: [],
            moon: null,     // { ra, dec, radius, phase, sunRa, sunDec, avoid, d, sun } from /api/focus/moon
            at: 0           // when the star vectors were last turned to the sky
        };
        const st = {
            lon: 0, lookWant: null,
            kind: 'gem', lat: 45, connected: false,
            // Shown and wanted mechanical angles: GEM (ra, dec), alt-az (az, alt).
            cur: null, want: null,
            theme: null, raf: 0, last: 0, w: 0, h: 0, dpr: 1, drag: null, pointers: new Map(),
            viewMoved: false, lastTap: null,   // lastTap: the last single tap, to spot a double tap
            grid: 'none'    // coordinate grid on the sky: 'none' | 'altaz' | 'eq'
        };

        function readTheme() {
            const css = getComputedStyle(canvas);
            const v = n => css.getPropertyValue(n);
            const night = document.documentElement.getAttribute('data-theme') === 'night';
            st.theme = {
                night,
                label: parseColor(v('--text-secondary'), [170, 176, 188]),
                accent: parseColor(v('--accent'), [80, 140, 255]),
                ray: parseColor(v('--accent'), [80, 140, 255]),
                warn: parseColor(v('--warning'), [255, 152, 0])
            };
        }

        // Base colour for a role, then lit. Night mode keeps the luminance and
        // drops the hue to red, the same way the rest of the page does.
        function shade(role, light) {
            const th = st.theme;
            const base = role === 'mark' || !PALETTE[role] ? th.accent : PALETTE[role];
            const f = 0.38 + 0.62 * light;
            if (th.night) {
                const lum = (0.3 * base[0] + 0.59 * base[1] + 0.11 * base[2]) / 255;
                const r = Math.round(60 + 170 * lum * f);
                return `rgb(${r},${Math.round(r * 0.12)},${Math.round(r * 0.12)})`;
            }
            return `rgb(${Math.round(base[0] * f)},${Math.round(base[1] * f)},${Math.round(base[2] * f)})`;
        }

        function frames() {
            const s = st.cur;
            if (st.kind === 'altaz') {
                const p = altAzFromAngles(s[0], s[1]);
                const azF = makeFrame(ALTAZ.base, p.front, UP, p.side);
                const hub = toWorld(azF, [0, ALTAZ.hubV, 0]);
                const up = cross(p.side, p.ota);
                return [[model.altaz.az, azF], [model.altaz.alt, makeFrame(hub, p.ota, up, p.side)]];
            }
            const p = gemFromAngles(st.lat, s[0], s[1]);
            const k = cross(p.dec, p.axis);
            const hinge = GEM.hinge;
            const raCentre = add(hinge, mul(p.axis, GEM.decAlongRa));
            const saddle = add(raCentre, mul(p.dec, GEM.saddle));
            const zero = decAxisZero(p.axis);
            return [
                [model.gem.fixed, IDENTITY],
                [model.gem.polar, makeFrame(add(hinge, mul(p.axis, 90)), zero, p.axis, cross(zero, p.axis))],
                [model.gem.ra, makeFrame(raCentre, p.dec, p.axis, k)],
                [model.gem.dec, makeFrame(saddle, p.ota, p.dec, cross(p.ota, p.dec))]
            ];
        }

        function otaTip() {
            if (st.kind === 'altaz') {
                const p = altAzFromAngles(st.cur[0], st.cur[1]);
                const hub = add(ALTAZ.base, [0, ALTAZ.hubV, 0]);
                return { from: add(add(hub, mul(p.side, ALTAZ.otaSide)), mul(p.ota, 560)), dir: p.ota };
            }
            const p = gemFromAngles(st.lat, st.cur[0], st.cur[1]);
            const centre = add(add(GEM.hinge, mul(p.axis, GEM.decAlongRa)), mul(p.dec, GEM.otaOffset));
            return { from: add(centre, mul(p.ota, 560)), dir: p.ota };
        }

        // ---- sky ----------------------------------------------------------

        // Local sidereal time (hours) now, from the site longitude.
        function lstHours(ms) {
            const jd = ms / 86400000 + 2440587.5;
            const g = 18.697374558 + 24.06570982441908 * (jd - 2451545.0);
            return ((g + st.lon / 15) % 24 + 24) % 24;
        }
        // Turn every star to where it is on the sky now. The sky moves 15"
        // a second, so this runs at most every few seconds, not per frame.
        function turnSky(force) {
            const now = Date.now();
            if (!force && now - sky.at < 5000) return;
            sky.at = now;
            const lst = lstHours(now), P = precession(now);
            // J2000 positions, precessed to the date, then placed by the
            // hour angle of date, as the mount's own pose is.
            const place = (ra, dec) => { const q = P.toDate(ra, dec); return directionFromHaDec(st.lat, lst - q.ra, q.dec); };
            for (const s of sky.stars) s.d = place(s.ra, s.dec);
            for (const f of sky.focus) f.d = place(f.ra, f.dec);
            const m = sky.moon;
            if (m) {
                m.d = place(m.ra, m.dec);
                m.sun = Number.isFinite(m.sunRa) && Number.isFinite(m.sunDec) ? place(m.sunRa, m.sunDec) : null;
            }
        }
        // Custom horizon altitude at an azimuth, linear between points and
        // wrapping round 360 (the same rule as HorizonProfile.AltitudeAt).
        function horizonAt(az) {
            const p = sky.horizon;
            if (!p.length) return 0;
            if (p.length === 1) return p[0].altitude;
            az = ((az % 360) + 360) % 360;
            let lo = p[p.length - 1], hi = p[0];
            for (const q of p) { if (q.azimuth <= az) lo = q; if (q.azimuth >= az) { hi = q; break; } }
            let span = hi.azimuth - lo.azimuth; if (span <= 0) span += 360;
            let off = az - lo.azimuth; if (off < 0) off += 360;
            return span === 0 ? lo.altitude : lo.altitude + (hi.altitude - lo.altitude) * off / span;
        }
        function altAzOf(d) {
            return { alt: Math.asin(Math.max(-1, Math.min(1, d[1]))) * 180 / Math.PI,
                     az: ((Math.atan2(d[0], -d[2]) * 180 / Math.PI) + 360) % 360 };
        }
        // Star colour from B-V: blue-white through white to orange.
        function starColor(bv, a) {
            const th = st.theme;
            if (th.night) return `rgba(255,${Math.round(40 + 30 * a)},${Math.round(40 + 30 * a)},${a})`;
            const t = Math.max(-0.3, Math.min(1.8, bv));
            const stops = [[-0.3, [160, 185, 255]], [0.3, [235, 240, 255]], [0.8, [255, 240, 210]], [1.4, [255, 205, 150]], [1.8, [255, 175, 120]]];
            let k = 0; while (k < stops.length - 2 && t > stops[k + 1][0]) k++;
            const [t0, c0] = stops[k], [t1, c1] = stops[k + 1], u = (t - t0) / (t1 - t0);
            return `rgba(${[0, 1, 2].map(i => Math.round(c0[i] + (c1[i] - c0[i]) * u)).join(',')},${a})`;
        }

        // The sky camera: orientation only, the sky being at infinity.
        // Stereographic: a direction at angle t from the centre lands
        // 2·focal·tan(t/2) out, focal being the scale at the centre (px per
        // radian); directions more than ~154° from the centre are dropped
        // (they run off to infinity at 180°). project() takes a camera-space
        // point (right, up, forward), for the mount model; proj() a world
        // direction; unproj() the direction drawn at a screen point.
        function skyCamera() {
            const { w, h } = st;
            const fwd = directionFromAltAz(look.alt, look.az);
            const right = norm(cross(fwd, UP));
            const up = cross(right, fwd);
            const cy = h * VIEW_CENTRE, cx = w / 2;
            const focal = (h / 2) / (2 * Math.tan(rad(look.fov / 4)));
            const project = c => {
                const L = Math.hypot(c[0], c[1], c[2]) || 1;
                if (c[2] / L < -0.9) return null;
                const f = 2 * focal / (L + c[2]);
                return [cx + c[0] * f, cy - c[1] * f];
            };
            const proj = d => project([dot(d, right), dot(d, up), dot(d, fwd)]);
            // The mount model's points, which the caller has already kept in
            // front of the eye (z >= 1): no cut-off. project()'s cut-off is
            // for directions on the sky; applied to the model it dropped the
            // tripod's near legs and they were drawn as spikes to the middle
            // of the screen.
            const projectNear = c => {
                const L = Math.hypot(c[0], c[1], c[2]) || 1;
                const f = 2 * focal / (L + c[2]);
                return [cx + c[0] * f, cy - c[1] * f];
            };
            const unproj = (x, y) => {
                const X = (x - cx) / focal, Y = (cy - y) / focal, s = X * X + Y * Y, q = 4 / (4 + s);
                return norm(add(add(mul(right, X * q), mul(up, Y * q)), mul(fwd, (4 - s) / (4 + s))));
            };
            return { fwd, right, up, focal, proj, project, projectNear, unproj, cx, cy };
        }


        // Grass for the ground, drawn once into a small tile: short blades in
        // a few greens over a dark base (reds in night mode).
        function grassPattern() {
            const night = st.theme.night;
            if (st.grass && st.grass.night === night) return st.grass.pat;
            const t = document.createElement('canvas');
            t.width = t.height = 96;
            const g = t.getContext('2d');
            const tint = (r, gr, b) => night
                ? `rgb(${Math.round((0.3 * r + 0.59 * gr + 0.11 * b) * 0.9)},${Math.round(0.08 * gr)},${Math.round(0.06 * gr)})`
                : `rgb(${r},${gr},${b})`;
            g.fillStyle = tint(24, 46, 20);
            g.fillRect(0, 0, 96, 96);
            let seed = 7;
            const rnd = () => (seed = (seed * 16807) % 2147483647) / 2147483647;
            const greens = [[34, 66, 26], [48, 88, 34], [62, 108, 40], [20, 40, 18], [74, 120, 46]];
            g.lineCap = 'round';
            for (let i = 0; i < 700; i++) {
                const x = rnd() * 96, y = rnd() * 96, l = 2 + rnd() * 5, lean = (rnd() - 0.5) * 2.2;
                const c = greens[Math.floor(rnd() * greens.length)];
                g.strokeStyle = tint(c[0], c[1], c[2]);
                g.globalAlpha = 0.55 + rnd() * 0.45;
                g.lineWidth = 0.8 + rnd() * 0.7;
                // Draw each blade at x and x±96 so the tile wraps without seams.
                for (const dx of [-96, 0, 96]) for (const dy of [-96, 0, 96]) {
                    g.beginPath(); g.moveTo(x + dx, y + dy); g.lineTo(x + dx + lean, y + dy - l); g.stroke();
                }
            }
            st.grass = { night, pat: ctx.createPattern(t, 'repeat') };
            return st.grass.pat;
        }

        // Coordinate grid, as on the SKY tab: alt-az (altitude every 10°,
        // azimuth every 15°) or equatorial (Dec every 10°, RA every hour,
        // turning with the sky). Drawn under the stars and the grass.
        function renderGrid(sc, onScreen) {
            if (st.grid !== 'altaz' && st.grid !== 'eq') return;
            const { w, h } = st, th = st.theme;
            const eq = st.grid === 'eq';
            const lst = lstHours(sky.at || Date.now());
            const dirOf = eq ? (a, b) => directionFromHaDec(st.lat, lst - a / 15, b)   // a = RA in degrees
                             : (a, b) => directionFromAltAz(b, a);                       // a = azimuth
            const far = 2 * Math.max(w, h);
            const col = th.night ? [200, 40, 40] : eq ? [90, 160, 255] : [120, 205, 140];
            const labels = [];
            // One line through samples of (a, b); returns its points on screen.
            const line = (pts, major) => {
                ctx.strokeStyle = `rgba(${col.join(',')},${major ? 0.5 : 0.24})`;
                ctx.lineWidth = major ? 1.3 : 0.8;
                ctx.beginPath();
                let prev = null;
                const out = [];
                for (const [a, b] of pts) {
                    const d = dirOf(a, b);
                    const p = d[1] > -0.3 ? sc.proj(d) : null;
                    if (p && prev && Math.hypot(p[0] - prev[0], p[1] - prev[1]) < far && (onScreen(p) || onScreen(prev))) {
                        ctx.moveTo(prev[0], prev[1]); ctx.lineTo(p[0], p[1]);
                    }
                    if (p && onScreen(p)) out.push(p);
                    prev = p;
                }
                ctx.stroke();
                return out;
            };
            const nearestMiddle = ps => ps.reduce((m, p) => !m || Math.abs(p[0] - w / 2) + Math.abs(p[1] - h / 2) < Math.abs(m[0] - w / 2) + Math.abs(m[1] - h / 2) ? p : m, null);
            // Circles of constant altitude / Dec.
            for (let b = eq ? -80 : 10; b <= 80; b += 10) {
                const pts = [];
                for (let a = 0; a <= 360; a += 2) pts.push([a, b]);
                const on = line(pts, b === 0);
                const at = nearestMiddle(on.filter(p => p[0] > 40 && p[0] < w - 40 && p[1] > 20 && p[1] < h - 20));
                if (at) labels.push([eq ? (b > 0 ? '+' : '') + b + '°' : b + '°', at]);
            }
            // Lines of constant azimuth / RA, pole to pole (horizon to zenith).
            for (let a = 0; a < 360; a += 15) {
                const pts = [];
                for (let b = eq ? -88 : 0; b <= (eq ? 88 : 88); b += 2) pts.push([a, b]);
                const major = eq ? a === 0 : a % 90 === 0;
                line(pts, major);
                if (eq) {
                    // Hour labels where the RA line crosses the celestial equator.
                    const p = sc.proj(dirOf(a, 0));
                    if (p && onScreen(p) && dirOf(a, 0)[1] > 0) labels.push([(a / 15) + 'h', p]);
                }
            }
            ctx.save();
            ctx.font = '600 10px system-ui, sans-serif';
            ctx.textAlign = 'left';
            ctx.textBaseline = 'bottom';
            ctx.shadowColor = 'rgba(0,0,0,0.9)';
            ctx.shadowBlur = 3;
            ctx.fillStyle = `rgba(${col.join(',')},0.9)`;
            for (const [t, p] of labels) ctx.fillText(t, p[0] + 3, p[1] - 2);
            ctx.restore();
        }

        // The Moon to scale with its phase: the lit side faces the Sun, the
        // terminator an ellipse as wide as cos(phase angle). Never smaller
        // than a few pixels, or it would vanish zoomed out. With a Moon
        // distance set for the focus stars, a dashed circle shows it.
        function renderMoon(sc, onScreen) {
            const m = sky.moon;
            if (!m || !m.d || m.d[1] < -0.05) return;
            const th = st.theme;
            const p = sc.proj(m.d);
            // A direction `deg` from the Moon toward `toward`.
            const along = (deg, toward) => {
                const t = norm(sub(toward, mul(m.d, dot(toward, m.d))));
                return add(mul(m.d, Math.cos(rad(deg))), mul(t, Math.sin(rad(deg))));
            };
            const other = Math.abs(m.d[1]) < 0.9 ? UP : NORTH;
            if (m.avoid > 0) {
                const u = norm(sub(other, mul(m.d, dot(other, m.d)))), v = cross(m.d, u);
                const ca = Math.cos(rad(m.avoid)), sa = Math.sin(rad(m.avoid));
                ctx.save();
                ctx.setLineDash([4, 5]);
                ctx.lineWidth = 1;
                ctx.strokeStyle = th.night ? 'rgba(220,60,50,0.55)' : 'rgba(255,230,150,0.5)';
                ctx.beginPath();
                let prev = null;
                for (let k = 0; k <= 96; k++) {
                    const a = 2 * Math.PI * k / 96;
                    const d = add(mul(m.d, ca), add(mul(u, sa * Math.cos(a)), mul(v, sa * Math.sin(a))));
                    const q = d[1] > -0.3 ? sc.proj(d) : null;
                    if (q && prev && Math.hypot(q[0] - prev[0], q[1] - prev[1]) < st.w) { ctx.moveTo(prev[0], prev[1]); ctx.lineTo(q[0], q[1]); }
                    prev = q;
                }
                ctx.stroke();
                ctx.restore();
            }
            if (!onScreen(p)) return;
            const edge = sc.proj(along(m.radius, other));
            const r = Math.max(3, edge ? Math.hypot(edge[0] - p[0], edge[1] - p[1]) : 3);
            const toSun = m.sun ? sc.proj(along(Math.min(5, m.radius * 4), m.sun)) : null;
            const ang = toSun ? Math.atan2(toSun[1] - p[1], toSun[0] - p[0]) : 0;
            const lit = th.night ? 'rgb(235,70,60)' : 'rgb(246,242,226)';
            const dark = th.night ? 'rgba(60,10,8,0.85)' : 'rgba(70,74,86,0.85)';
            ctx.save();
            ctx.translate(p[0], p[1]);
            ctx.rotate(ang);
            ctx.shadowColor = th.night ? 'rgba(200,40,30,0.5)' : 'rgba(255,250,220,0.55)';
            ctx.shadowBlur = Math.min(30, 4 + r);
            ctx.fillStyle = dark;
            ctx.beginPath(); ctx.arc(0, 0, r, 0, 2 * Math.PI); ctx.fill();
            ctx.shadowBlur = 0;
            // Lit half toward +x (the Sun), closed by the terminator.
            const c = Math.cos(rad(m.phase));
            ctx.fillStyle = lit;
            ctx.beginPath();
            ctx.arc(0, 0, r, -Math.PI / 2, Math.PI / 2, false);
            ctx.ellipse(0, 0, r * Math.abs(c), r, 0, Math.PI / 2, -Math.PI / 2, c < 0);
            ctx.fill();
            ctx.restore();
            ctx.save();
            ctx.font = '600 12px system-ui, sans-serif';
            ctx.textAlign = 'left';
            ctx.textBaseline = 'middle';
            ctx.shadowColor = 'rgba(0,0,0,0.9)';
            ctx.shadowBlur = 3;
            ctx.fillStyle = lit;
            ctx.fillText(st.moonLabel || 'Moon', p[0] + r + 6, p[1]);
            ctx.restore();
        }

        // Altitude ruler down the left edge, read along the view centre's
        // azimuth (a vertical line through the middle of the view).
        function renderRuler(sc) {
            const { h } = st, th = st.theme;
            ctx.save();
            ctx.font = '600 11px system-ui, sans-serif';
            ctx.textAlign = 'left';
            ctx.textBaseline = 'middle';
            ctx.shadowColor = 'rgba(0,0,0,0.85)';
            ctx.shadowBlur = 3;
            const col = th.night ? [230, 60, 60] : th.label;
            ctx.strokeStyle = `rgba(${col.join(',')},0.75)`;
            ctx.fillStyle = `rgb(${col.join(',')})`;
            ctx.lineWidth = 1;
            // Past 90° the line runs on over the zenith and down the far
            // side; 100° there is 80° up, and is labelled 80°.
            for (let alt = 0; alt <= 110; alt += 5) {
                if (Math.abs(alt - look.alt) >= 150) continue;
                const p = sc.proj(directionFromAltAz(alt, look.az));
                if (!p || p[1] < 6 || p[1] > h - 6) continue;
                const major = alt % 10 === 0;
                ctx.beginPath(); ctx.moveTo(0, p[1]); ctx.lineTo(major ? 9 : 5, p[1]); ctx.stroke();
                if (major) ctx.fillText((alt > 90 ? 180 - alt : alt) + '°', 12, p[1]);
            }
            ctx.restore();
        }

        function renderSky(sc) {
            const { w, h } = st, th = st.theme;
            const grad = ctx.createLinearGradient(0, 0, 0, h);
            if (th.night) { grad.addColorStop(0, '#050000'); grad.addColorStop(1, '#140303'); }
            else { grad.addColorStop(0, '#02050d'); grad.addColorStop(1, '#0c1a33'); }
            ctx.fillStyle = grad;
            ctx.fillRect(0, 0, w, h);
            turnSky(false);
            const scale = Math.sqrt(70 / look.fov);
            const margin = 40;
            const onScreen = p => p && p[0] > -margin && p[0] < w + margin && p[1] > -margin && p[1] < h + margin;

            // Constellation figures, faint.
            ctx.lineWidth = 1;
            ctx.strokeStyle = th.night ? 'rgba(200,40,40,0.28)' : `rgba(${th.accent.join(',')},0.30)`;
            ctx.beginPath();
            for (const line of sky.lines) {
                let prev = null;
                for (const hip of line) {
                    const s = sky.byHip.get(hip);
                    const p = s && s.d && s.d[1] > -0.2 ? sc.proj(s.d) : null;
                    if (p && prev && (onScreen(p) || onScreen(prev))) { ctx.moveTo(prev[0], prev[1]); ctx.lineTo(p[0], p[1]); }
                    prev = p;
                }
            }
            ctx.stroke();

            renderGrid(sc, onScreen);

            // Stars, faint first so the bright ones sit on top.
            for (let i = sky.stars.length - 1; i >= 0; i--) {
                const s = sky.stars[i];
                if (!s.d || s.d[1] < -0.05) continue;
                const p = sc.proj(s.d);
                if (!onScreen(p)) continue;
                const r = Math.max(0.6, Math.min(4.5, (6.8 - s.mag) * 0.5)) * scale;
                const a = Math.max(0.35, Math.min(1, (7 - s.mag) / 5));
                ctx.fillStyle = starColor(s.bv, a);
                ctx.beginPath(); ctx.arc(p[0], p[1], r, 0, 2 * Math.PI); ctx.fill();
            }

            renderMoon(sc, onScreen);

            // Ground: everything below the custom horizon (or 0°).
            const LIM = 1e5;
            const groundStyle = () => {
                ctx.fillStyle = grassPattern();
                ctx.fill('evenodd');
                // Darker toward the foreground, for depth.
                const hp = sc.proj(directionFromAltAz(0, look.az));
                const top = Math.max(0, Math.min(h - 1, hp ? hp[1] : 0));
                const shadeG = ctx.createLinearGradient(0, top, 0, h);
                shadeG.addColorStop(0, 'rgba(0,0,0,0)');
                shadeG.addColorStop(1, 'rgba(0,0,0,0.45)');
                ctx.fillStyle = shadeG;
                ctx.fill('evenodd');
            };
            const horizonStroke = pts => {
                ctx.strokeStyle = th.night ? 'rgba(90,14,12,0.95)' : 'rgba(10,26,8,0.95)';
                ctx.lineWidth = 1.5;
                ctx.beginPath();
                pts.forEach((p, i) => i ? ctx.lineTo(p[0], p[1]) : ctx.moveTo(p[0], p[1]));
                ctx.stroke();
            };
            // Stereographic with no culling: the point straight behind the
            // view is at infinity, so points near it are pulled in along
            // their direction from the middle, which keeps the line's shape.
            const farProj = d => {
                const z = Math.max(-0.999999, dot(d, sc.fwd)), f = 2 * sc.focal / (1 + z);
                let x = dot(d, sc.right) * f, y = -dot(d, sc.up) * f;
                const r = Math.hypot(x, y);
                if (r > LIM) { x *= LIM / r; y *= LIM / r; }
                return [sc.cx + x, sc.cy + y];
            };
            const behindAz = look.az + 180, behindAlt = -look.alt;
            const behindGap = behindAlt - Math.max(0, horizonAt(behindAz));
            if (Math.abs(behindGap) < 0.5) {
                // At eye level the horizon runs left to right across the
                // whole view, through the point behind: the ground is the
                // band from it down past the bottom edge.
                const pts = [];
                for (let k = -179.5; k <= 179.5; k += 1) {
                    const az = look.az + k;
                    pts.push(farProj(directionFromAltAz(Math.max(0, horizonAt(az)), az)));
                }
                if (pts.length > 1) {
                    ctx.beginPath();
                    ctx.moveTo(pts[0][0], h + LIM);
                    for (const p of pts) ctx.lineTo(p[0], p[1]);
                    ctx.lineTo(pts[pts.length - 1][0], h + LIM);
                    ctx.closePath();
                    groundStyle();
                    horizonStroke(pts);
                }
            } else {
                // Tilted: the horizon is a closed curve all the way
                // round (a circle, for a flat horizon). The ground is outside
                // it when the point behind the view is below the horizon
                // (looking up: the horizon bends up at the sides) and inside
                // it otherwise (looking down: it bends down).
                const pts = [];
                for (let k = -180; k < 180; k += 1) {
                    const az = look.az + k;
                    pts.push(farProj(directionFromAltAz(Math.max(0, horizonAt(az)), az)));
                }
                ctx.beginPath();
                pts.forEach((p, i) => i ? ctx.lineTo(p[0], p[1]) : ctx.moveTo(p[0], p[1]));
                ctx.closePath();
                if (behindGap < 0) ctx.rect(-2 * LIM, -2 * LIM, 4 * LIM, 4 * LIM);
                groundStyle();
                horizonStroke(pts.concat([pts[0]]));
            }
            // Azimuth marks on the horizon: cardinals with degrees, ticks every 30°.
            ctx.save();
            ctx.font = '600 13px system-ui, sans-serif';
            ctx.textAlign = 'center';
            ctx.textBaseline = 'top';
            ctx.shadowColor = 'rgba(0,0,0,0.9)';
            ctx.shadowBlur = 3;
            for (let az = 0; az < 360; az += 30) {
                // Just below the horizon, on the ground side wherever the
                // horizon bends in a tilted view.
                const base = sc.proj(directionFromAltAz(0, az));
                const below = sc.proj(directionFromAltAz(-1, az));
                if (!onScreen(base) || !below) continue;
                const dx = below[0] - base[0], dy = below[1] - base[1], n = Math.hypot(dx, dy) || 1;
                const card = { 0: 'N', 90: 'E', 180: 'S', 270: 'W' }[az];
                ctx.fillStyle = card === 'N' ? `rgb(${th.accent.join(',')})` : `rgba(${th.label.join(',')},${card ? 1 : 0.7})`;
                ctx.textBaseline = 'middle';
                ctx.fillText(card ? `${card} (${az}°)` : `${az}°`, base[0] + dx / n * 13, base[1] + dy / n * 13);
            }
            ctx.restore();
            renderRuler(sc);

            // Focus stars: ringed and named. The ring grows with the star's
            // brightness against the rest of the list, from 7 px for the
            // faintest to 17 px for the brightest; magnitude is already the
            // eye's (logarithmic) scale, so it maps straight to the radius.
            ctx.font = '600 12px system-ui, sans-serif';
            ctx.textAlign = 'left';
            ctx.textBaseline = 'middle';
            const mags = sky.focus.map(f => f.mag).filter(Number.isFinite);
            const faint = mags.length ? Math.max(...mags) : 0, bright = mags.length ? Math.min(...mags) : 0;
            const ringR = f => {
                if (!Number.isFinite(f.mag) || faint - bright < 0.05) return 11;
                return 7 + 10 * (faint - f.mag) / (faint - bright);
            };
            for (const f of sky.focus) {
                f.p = null;
                if (!f.d) continue;
                const p = sc.proj(f.d);
                if (!onScreen(p)) continue;
                f.p = p;   // where it was drawn, for a tap to find it
                const col = f.selected ? th.warn : th.accent;
                ctx.strokeStyle = `rgba(${col.join(',')},0.95)`;
                ctx.lineWidth = f.selected ? 2.2 : 1.5;
                const r = ringR(f) + (f.selected ? 3 : 0);
                ctx.beginPath(); ctx.arc(p[0], p[1], r, 0, 2 * Math.PI); ctx.stroke();
                ctx.fillStyle = `rgb(${col.join(',')})`;
                ctx.fillText(f.name, p[0] + r + 4, p[1]);
            }
            renderHome(sc, onScreen);
            renderPark(sc, onScreen);
        }

        // The park position, when known: a magenta X (a crosshair turned
        // 45°) with a small square, labelled below, so it still reads where
        // it sits on the home mark (EQMod's default park is home).
        function renderPark(sc, onScreen) {
            if (!st.park) return;
            const p = sc.proj(directionFromHaDec(st.lat, st.park.ha, st.park.dec));
            if (!onScreen(p)) return;
            const col = st.theme.night ? 'rgba(255,70,150,0.95)' : 'rgba(255,60,255,0.95)';
            ctx.save();
            ctx.strokeStyle = col;
            ctx.lineWidth = 1.5;
            ctx.beginPath();
            for (const [dx, dy] of [[1, 1], [-1, 1], [1, -1], [-1, -1]]) {
                ctx.moveTo(p[0] + dx * 5, p[1] + dy * 5); ctx.lineTo(p[0] + dx * 12, p[1] + dy * 12);
            }
            ctx.rect(p[0] - 3, p[1] - 3, 6, 6);
            ctx.stroke();
            ctx.font = '600 12px system-ui, sans-serif';
            ctx.textAlign = 'left';
            ctx.textBaseline = 'middle';
            ctx.shadowColor = 'rgba(0,0,0,0.9)';
            ctx.shadowBlur = 3;
            ctx.fillStyle = col;
            ctx.fillText(st.parkLabel || 'Park', p[0] + 19, p[1] + 10);
            ctx.restore();
        }

        // Where the mount's home is: the power-on pose, counterweight down
        // with the OTA along the polar axis, so on the celestial pole above
        // the horizon, at an altitude equal to the site's latitude, due north
        // (due south in the southern hemisphere). A yellow crosshair with a
        // small ring, so it is not taken for the red centre mark. Only for an
        // equatorial mount: an alt-az mount's home is not the pole.
        function renderHome(sc, onScreen) {
            if (st.kind === 'altaz') return;
            const p = sc.proj(polarAxis(st.lat));
            if (!onScreen(p)) return;
            const th = st.theme;
            const col = th.night ? 'rgba(255,150,40,0.95)' : 'rgba(255,221,0,0.95)';
            ctx.save();
            ctx.strokeStyle = col;
            ctx.lineWidth = 1.5;
            ctx.beginPath();
            for (const [dx, dy] of [[1, 0], [-1, 0], [0, 1], [0, -1]]) {
                ctx.moveTo(p[0] + dx * 6, p[1] + dy * 6); ctx.lineTo(p[0] + dx * 16, p[1] + dy * 16);
            }
            ctx.moveTo(p[0] + 3, p[1]); ctx.arc(p[0], p[1], 3, 0, 2 * Math.PI);
            ctx.stroke();
            ctx.font = '600 12px system-ui, sans-serif';
            ctx.textAlign = 'left';
            ctx.textBaseline = 'middle';
            ctx.shadowColor = 'rgba(0,0,0,0.9)';
            ctx.shadowBlur = 3;
            ctx.fillStyle = col;
            ctx.fillText(st.homeLabel || 'Home', p[0] + 19, p[1] - 10);
            ctx.restore();
        }

        // ---- the mount -----------------------------------------------------

        // The sky's own camera (same direction, zoom and centre) at the
        // observer's eye, level with the mount head and VIEW_DIST behind it.
        // Sharing the camera is what makes the OTA and its dashed line one
        // straight line to the reticle: every line along the tube meets the
        // sky at the point the scope points to. Zooming magnifies the mount
        // with the sky, so zooming in on it shows the RA and Dec axes close up.
        function mountCamera(sky) {
            const eye = add(GEM.hinge, mul(directionFromAltAz(0, look.az), -VIEW_DIST));
            // Camera-space points go through the sky's own projection, by
            // their direction from the eye.
            return { eye, fwd: sky.fwd, right: sky.right, up: sky.up, focal: sky.focal, cx: sky.cx, cy: sky.cy,
                     project: sky.projectNear };
        }

        function render() {
            if (!st.theme) readTheme();
            const { w, h, dpr } = st;
            if (!w || !h) return;
            ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
            ctx.clearRect(0, 0, w, h);
            const skyCam = skyCamera();
            renderSky(skyCam);

            const mc = mountCamera(skyCam);
            const { eye, fwd, right, up } = mc;
            const cam = p => { const d = sub(p, eye); return [dot(d, right), dot(d, up), dot(d, fwd)]; };
            const scr = mc.project;
            const tip = st.connected ? otaTip() : null;
            // Light from over the viewer's left shoulder, so the side facing
            // the viewer is always the lit one.
            const light = norm(add(add(mul(fwd, -1), mul(up, 0.8)), mul(right, -0.5)));

            const th = st.theme;

            // Triangles, culled, lit and painted back to front.
            const parts = [[model.tripod, IDENTITY]].concat(frames());
            const out = [];
            for (const [list, f] of parts) {
                for (const t of list) {
                    const a = toWorld(f, t.a), b = toWorld(f, t.b), c = toWorld(f, t.c);
                    const n = norm(cross(sub(b, a), sub(c, a)));
                    if (dot(n, sub(eye, a)) <= 0) continue;
                    const ca = cam(a), cb = cam(b), cc = cam(c);
                    if (ca[2] < 1 || cb[2] < 1 || cc[2] < 1) continue;
                    out.push({ z: ca[2] + cb[2] + cc[2], p: [scr(ca), scr(cb), scr(cc)], fill: shade(t.color, Math.max(0, dot(n, light))) });
                }
            }
            out.sort((x, y) => y.z - x.z);
            ctx.lineJoin = 'round';
            ctx.lineWidth = 0.6;
            for (const t of out) {
                ctx.beginPath();
                ctx.moveTo(t.p[0][0], t.p[0][1]);
                ctx.lineTo(t.p[1][0], t.p[1][1]);
                ctx.lineTo(t.p[2][0], t.p[2][1]);
                ctx.closePath();
                ctx.fillStyle = t.fill;
                ctx.strokeStyle = t.fill;   // hides the hairline seams between triangles
                ctx.fill();
                ctx.stroke();
            }

            // Pointing ray, only while a mount is reporting: from the OTA to
            // where the scope points on the sky, which is marked with a
            // reticle. It bends in the stereographic picture, so it is
            // drawn through points out along it, ever further. Pointing
            // behind the view, the part in front of the eye still shows.
            const a = st.connected ? cam(tip.from) : null;
            if (a && a[2] > 1) {
                const sb = skyCam.proj(tip.dir), onSky = !!sb;
                const path = [scr(a)];
                for (let t = 100; t < 1e7; t *= 1.6) {
                    const c = cam(add(tip.from, mul(tip.dir, t)));
                    if (c[2] > 1) path.push(scr(c));
                }
                if (onSky) path.push(sb);
                if (path.length > 1) {
                    ctx.save();
                    ctx.setLineDash([6, 6]);
                    ctx.strokeStyle = `rgba(${th.ray.join(',')},0.85)`;
                    ctx.lineWidth = 1.5;
                    ctx.beginPath();
                    path.forEach((p, i) => i ? ctx.lineTo(p[0], p[1]) : ctx.moveTo(p[0], p[1]));
                    ctx.stroke();
                    ctx.restore();
                    if (onSky) {
                        ctx.strokeStyle = `rgba(${th.ray.join(',')},0.95)`;
                        ctx.lineWidth = 1.5;
                        ctx.beginPath(); ctx.arc(sb[0], sb[1], 16, 0, 2 * Math.PI);
                        for (const [dx, dy] of [[1, 0], [-1, 0], [0, 1], [0, -1]]) {
                            ctx.moveTo(sb[0] + dx * 10, sb[1] + dy * 10); ctx.lineTo(sb[0] + dx * 22, sb[1] + dy * 22);
                        }
                        ctx.stroke();
                    }
                }
            }
            renderCentreMark();
            reportCentre();
        }

        // The sync target: a fixed mark in the middle of the screen (not the
        // view's lower turning point). Drag the sky to put it on what the
        // scope really points at, then Sync here tells the mount so. Red, like
        // the SKY map's centre FOV, so it is not taken for the mount's reticle.
        function renderCentreMark() {
            const x = st.w / 2, y = st.h / 2, th = st.theme;
            ctx.save();
            ctx.strokeStyle = th.night ? 'rgba(255,60,50,0.9)' : 'rgba(255,90,80,0.9)';
            ctx.lineWidth = 1.5;
            ctx.beginPath();
            for (const [dx, dy] of [[1, 0], [-1, 0], [0, 1], [0, -1]]) {
                ctx.moveTo(x + dx * 5, y + dy * 5); ctx.lineTo(x + dx * 14, y + dy * 14);
            }
            ctx.stroke();
            ctx.restore();
        }
        // The middle of the screen as J2000 RA (hours) / Dec: the reverse of
        // how the stars are placed (LST minus hour angle gives RA of date,
        // then precessed back to J2000, which is what a sync takes).
        function viewCentre() {
            const sc = skyCamera(), now = Date.now();
            const { ha, dec } = haDecFromDirection(st.lat, sc.unproj(st.w / 2, st.h / 2));
            const j = precession(now).toJ2000(((lstHours(now) - ha) % 24 + 24) % 24, dec);
            return { raHours: j.ra, decDeg: j.dec };
        }
        // Tell the page when the view centre moves (for its RA/Dec readout).
        function reportCentre() {
            if (!st.onView) return;
            const c = viewCentre(), l = st.lastCentre;
            if (l && Math.abs(l.raHours - c.raHours) < 1e-4 && Math.abs(l.decDeg - c.decDeg) < 1e-3) return;
            st.lastCentre = c;
            st.onView(c);
        }

        // ---- animation: ease the shown angles toward the reported ones. One
        // status tick a second; a 0.35 s time constant reads as continuous
        // motion during a slew without lagging a full tick behind.
        function step(now) {
            st.raf = 0;
            const dt = st.last ? Math.min(0.1, (now - st.last) / 1000) : 0.016;
            st.last = now;
            let moving = false;
            if (st.cur && st.want) {
                const k = 1 - Math.exp(-dt / 0.35);
                st.cur = st.cur.map((c, i) => {
                    const d = wrap(st.want[i] - c);
                    if (Math.abs(d) > 1e-4) moving = true;
                    return Math.abs(d) < 1e-4 ? st.want[i] : c + d * k;
                });
            }
            if (st.lookWant) {
                const k = 1 - Math.exp(-dt / 0.18);
                let dAz = ((st.lookWant.az - look.az) % 360 + 540) % 360 - 180;
                const dAlt = st.lookWant.alt - look.alt;
                if (Math.abs(dAz) < 0.02 && Math.abs(dAlt) < 0.02) { look.az = st.lookWant.az; look.alt = st.lookWant.alt; st.lookWant = null; }
                else { look.az = ((look.az + dAz * k) % 360 + 360) % 360; look.alt += dAlt * k; moving = true; }
            }
            render();
            if (moving) kick(); else st.last = 0;
        }
        function kick() { if (!st.raf) st.raf = requestAnimationFrame(step); }

        function resize() {
            const r = canvas.getBoundingClientRect();
            st.dpr = Math.min(2, window.devicePixelRatio || 1);
            st.w = r.width; st.h = r.height;
            canvas.width = Math.round(r.width * st.dpr);
            canvas.height = Math.round(r.height * st.dpr);
            kick();
        }
        const ro = typeof ResizeObserver !== 'undefined' ? new ResizeObserver(resize) : null;
        if (ro) ro.observe(canvas);
        const mo = new MutationObserver(() => { readTheme(); kick(); });
        mo.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });

        // ---- view: drag turns, wheel or pinch zooms, double click resets.
        canvas.style.touchAction = 'none';
        const pinchDist = () => { const p = [...st.pointers.values()]; return Math.hypot(p[0].x - p[1].x, p[0].y - p[1].y); };
        // Zooms about the point under the pointer (or between two fingers),
        // so zooming on the mount keeps it in view.
        const zoom = (f, cx, cy) => {
            st.viewMoved = true;
            st.lookWant = null;
            const r = canvas.getBoundingClientRect();
            const x = cx == null ? st.w / 2 : cx - r.left, y = cy == null ? st.h / 2 : cy - r.top;
            const sc = skyCamera();
            const dir = sc.unproj(x, y);
            const at = altAzOf(dir);
            look.fov *= f;
            clampLook(look);
            // Nudge the view until the point under the pointer is back
            // under it.
            const deg = r2 => r2 * 180 / Math.PI;
            const cosAlt = Math.max(0.3, Math.cos(rad(at.alt)));
            for (let i = 0; i < 4; i++) {
                const s2 = skyCamera(), p = s2.proj(dir);
                if (!p) break;
                look.az = ((look.az - deg((x - p[0]) / s2.focal) / cosAlt) % 360 + 360) % 360;
                look.alt += deg((y - p[1]) / s2.focal);
                clampLook(look);
            }
            kick();
        };
        canvas.addEventListener('pointerdown', e => {
            try { canvas.setPointerCapture?.(e.pointerId); } catch { /* pointer already gone */ }
            // A primary pointer starts a new gesture with nothing else down:
            // forget any pointer whose up or cancel never arrived, or every
            // later one-finger touch is taken for a pinch and never a tap.
            if (e.isPrimary) st.pointers.clear();
            st.pointers.set(e.pointerId, { x: e.clientX, y: e.clientY });
            // A tap (no drag) picks a focus star; remember where it began.
            st.tap = st.pointers.size === 1 ? { x: e.clientX, y: e.clientY, id: e.pointerId } : null;
            st.drag = st.pointers.size === 2 ? { pinch: pinchDist() } : { x: e.clientX, y: e.clientY };
        });
        canvas.addEventListener('pointermove', e => {
            if (!st.pointers.has(e.pointerId) || !st.drag) return;
            st.pointers.set(e.pointerId, { x: e.clientX, y: e.clientY });
            if (st.drag.pinch) {
                const d = pinchDist();
                const p = [...st.pointers.values()];
                if (d > 0) zoom(st.drag.pinch / d, (p[0].x + p[1].x) / 2, (p[0].y + p[1].y) / 2);
                st.drag.pinch = d;
                return;
            }
            st.viewMoved = true;
            // Grab the sky: it follows the pointer.
            const degPerPx = 180 / Math.PI / Math.max(1, skyCamera().focal);
            st.lookWant = null;
            look.az = ((look.az - (e.clientX - st.drag.x) * degPerPx) % 360 + 360) % 360;
            look.alt += (e.clientY - st.drag.y) * degPerPx;
            clampLook(look);
            st.drag = { x: e.clientX, y: e.clientY };
            kick();
        });
        const lift = e => {
            const tap = st.tap;
            if (e.type === 'pointerup' && tap && tap.id === e.pointerId
                && Math.hypot(e.clientX - tap.x, e.clientY - tap.y) < tapSlop(e)) {
                // A second tap close by within 350 ms is a double tap: reset
                // the view (it replaces the Reset view button). Detected here
                // rather than from dblclick, which iOS does not send for a
                // canvas, and the second tap picks nothing, so a double tap
                // on the selected star does not also clear it.
                const now = e.timeStamp, last = st.lastTap;
                if (last && now - last.t < 350 && Math.hypot(e.clientX - last.x, e.clientY - last.y) < 30) {
                    st.lastTap = null;
                    resetView();
                } else {
                    st.lastTap = { t: now, x: e.clientX, y: e.clientY };
                    pick(e);
                }
            }
            st.tap = null;
            st.pointers.delete(e.pointerId);
            const rest = [...st.pointers.values()][0];
            st.drag = rest ? { x: rest.x, y: rest.y } : null;
        };
        canvas.addEventListener('pointerup', lift);
        canvas.addEventListener('pointercancel', lift);
        canvas.addEventListener('wheel', e => { e.preventDefault(); zoom(Math.exp(e.deltaY * 0.001), e.clientX, e.clientY); }, { passive: false });

        // Nearest ringed focus star within 24 px of a tap.
        // How far a tap may wander and still be a tap, and how near a ring it
        // must land, in CSS px. A thumb on a phone rolls further than a
        // fingertip on a tablet or a mouse, and on a page Safari shows
        // zoomed out a CSS px is smaller on the glass, so both grow by the
        // zoom (a phone tap failed to pick at the old 6 and 24 px).
        const zoomOut = () => 1 / Math.min(1, window.visualViewport?.scale || 1);
        const tapSlop = e => (e.pointerType === 'mouse' ? 6 : 14) * zoomOut();
        const pickRadius = e => (e.pointerType === 'mouse' ? 24 : 36) * zoomOut();
        function pick(e) {
            if (!st.onPick) return;
            const r = canvas.getBoundingClientRect();
            const x = e.clientX - r.left, y = e.clientY - r.top;
            let best = null, bestD = pickRadius(e);
            for (const f of sky.focus) {
                if (!f.p) continue;
                const d = Math.hypot(f.p[0] - x, f.p[1] - y);
                if (d < bestD) { best = f; bestD = d; }
            }
            if (best) st.onPick(best.hip);
        }

        // Reset view (a double tap or click): with a mount reporting, turn
        // so the scope's reticle sits on the red centre mark, ready for Sync
        // here; the ground is level only if the scope happens to be. Zoom
        // goes back to the starting view's. With no mount, the starting view.
        function resetView() {
            st.viewMoved = false;
            const d = defaultLook();
            look.fov = d.fov;
            const dir = st.connected && st.want ? scopeDirection(st.want) : null;
            if (!dir) { st.lookWant = { alt: d.alt, az: d.az }; kick(); return; }
            const { alt, az } = altAzOf(dir);
            st.lookWant = { alt: clampLook({ alt: centreAlt(alt), fov: look.fov }).alt, az };
            kick();
        }
        // Where the scope points for a set of mechanical angles (the eased
        // st.cur is mid-move; Reset wants where it is going, st.want).
        function scopeDirection(angles) {
            const keep = st.cur;
            st.cur = angles;
            try { return otaTip().dir; } finally { st.cur = keep; }
        }
        // The look altitude that puts altitude altDeg in the middle of the
        // screen. The view's centre is low on the screen, so it aims below
        // by the angle from there up to mid-screen.
        function centreAlt(altDeg) {
            const f = (st.h / 2) / (2 * Math.tan(rad(look.fov / 4)));
            return altDeg - 2 * Math.atan(st.h * (VIEW_CENTRE - 0.5) / (2 * f)) * 180 / Math.PI;
        }

        // Home pose while no mount reports: GEM on the visible pole with the
        // counterweight down (HA +6h on the east side), alt-az level and
        // facing north.
        function restAngles(kind) {
            if (kind === 'altaz') return [0, 0];
            const p = gemPoseEq(st.lat, 6, st.lat >= 0 ? 90 : -90, 'pierEast');
            return [p.ra, p.decAngle];
        }

        /** Feed one status tick. s: { kind: 'gem'|'altaz', latitude, ha
         *  (hours, of date), ra and dec (J2000), alt, az, pierSide,
         *  connected }. A GEM is posed from ha/dec when given, else from
         *  alt/az; alt-az always from alt/az. */
        function update(s) {
            const kind = s.kind === 'altaz' ? 'altaz' : 'gem';
            const lat = Number.isFinite(s.latitude) ? Math.max(-89.9, Math.min(89.9, s.latitude)) : st.lat;
            // Jump, not ease, when the picture changes meaning: another
            // model, another site, or the first reading after a (re)connect.
            const snap = kind !== st.kind || Math.abs(lat - st.lat) > 1e-6 || !st.cur
                || (!!s.connected && !st.connected);
            const flipped = (lat >= 0) !== (st.lat >= 0);
            const moved = Math.abs(lat - st.lat) > 1e-6
                || (Number.isFinite(s.longitude) && Math.abs(s.longitude - st.lon) > 1e-6);
            st.kind = kind; st.lat = lat; st.connected = !!s.connected;
            // The park position (hour angle and Dec of date), when the
            // driver publishes one we can read.
            st.park = s.park && Number.isFinite(s.park.ha) && Number.isFinite(s.park.dec) ? { ha: s.park.ha, dec: s.park.dec } : null;
            if (Number.isFinite(s.longitude)) st.lon = s.longitude;
            if (moved) turnSky(true);
            if (flipped && !st.viewMoved) Object.assign(look, defaultLook());
            let want;
            const hasEq = Number.isFinite(s.ha) && Number.isFinite(s.dec);
            const hasHz = Number.isFinite(s.alt) && Number.isFinite(s.az);
            if (st.connected && kind === 'gem' && hasEq) {
                // The hour angle is of date; the page's Dec is J2000, so
                // precess it (with the J2000 RA) to the same date. On the
                // pole the two epochs differ by the whole offset of the pole.
                const dec = Number.isFinite(s.ra) ? precession(Date.now()).toDate(s.ra, s.dec).dec : s.dec;
                const p = gemPoseEq(lat, s.ha, dec, s.pierSide);
                want = [p.ra, p.decAngle];
            } else if (st.connected && hasHz) {
                if (kind === 'altaz') want = [rad(s.az), rad(s.alt)];
                else { const p = gemPose(lat, s.alt, s.az, s.pierSide); want = [p.ra, p.decAngle]; }
            } else {
                want = restAngles(kind);
            }
            st.want = want;
            if (snap) st.cur = want.slice();
            kick();
        }

        // Rest pose until the first update, so a draw that lands first (the
        // initial resize, a theme change) has something to draw.
        st.cur = restAngles(st.kind);
        st.want = st.cur.slice();
        Object.assign(look, defaultLook());
        resize();
        // The sky turns: redraw every 10 s while it is shown and visible.
        const clock = setInterval(() => {
            if (st.w && canvas.offsetParent !== null) { turnSky(true); kick(); }
        }, 10000);

        /** Look at a direction, eased. */
        function lookAt(altDeg, azDeg) {
            st.viewMoved = true;
            // As close to the star as the tilt limits (45° down to the
            // zenith) allow.
            altDeg = centreAlt(altDeg);
            st.lookWant = { alt: clampLook({ alt: altDeg, fov: look.fov }).alt, az: ((azDeg % 360) + 360) % 360 };
            kick();
        }
        return {
            update, resetView,
            redraw() { readTheme(); resize(); },
            /** Backdrop from /api/sky/backdrop: { stars: [[hip, raH, decDeg, mag, bv]], lines: [[hip...]] }. */
            setSky(data) {
                sky.stars = (data?.stars || []).map(r => ({ hip: r[0], ra: r[1], dec: r[2], mag: r[3], bv: r[4], d: null }))
                    .sort((a, b) => a.mag - b.mag);
                sky.byHip = new Map(sky.stars.map(x => [x.hip, x]));
                sky.lines = data?.lines || [];
                turnSky(true); kick();
            },
            /** Stars to ring: [{ hip, name, raHours, decDeg, magnitude }]; selectedHip marks one. */
            setFocusStars(list, selectedHip) {
                sky.focus = (list || []).map(f => ({ hip: f.hip, name: f.name, ra: f.raHours, dec: f.decDeg,
                                                    mag: Number(f.magnitude), selected: f.hip === selectedHip, d: null }));
                turnSky(true); kick();
            },
            /** The name drawn by the home crosshair, in the page's language. */
            setHomeLabel(label) { if (label) { st.homeLabel = String(label); kick(); } },
            /** The name drawn by the park mark, in the page's language. */
            setParkLabel(label) { if (label) { st.parkLabel = String(label); kick(); } },
            /** The Moon from /api/focus/moon, or null to hide it. avoidDeg
             *  (0 = off) draws the focus stars' Moon distance round it;
             *  label is its name in the page's language. */
            setMoon(m, avoidDeg, label) {
                sky.moon = m && Number.isFinite(m.raHours) && Number.isFinite(m.decDeg) ? {
                    ra: m.raHours, dec: m.decDeg, radius: Number(m.radiusDeg) || 0.26,
                    phase: Number.isFinite(m.phaseAngleDeg) ? m.phaseAngleDeg : 0,
                    sunRa: m.sunRaHours, sunDec: m.sunDecDeg,
                    avoid: Math.max(0, Number(avoidDeg) || 0), d: null, sun: null
                } : null;
                if (label) st.moonLabel = label;
                turnSky(true); kick();
            },
            /** Coordinate grid on the sky: 'none' | 'altaz' | 'eq'. */
            setGrid(mode) {
                st.grid = mode === 'altaz' || mode === 'eq' ? mode : 'none';
                kick();
            },
            /** Custom horizon points [{ azimuth, altitude }]. */
            setHorizon(points) {
                sky.horizon = (points || []).map(p => ({ azimuth: ((+p.azimuth % 360) + 360) % 360, altitude: +p.altitude }))
                    .filter(p => Number.isFinite(p.azimuth) && Number.isFinite(p.altitude))
                    .sort((a, b) => a.azimuth - b.azimuth);
                kick();
            },
            lookAt,
            /** The middle of the screen now: { raHours, decDeg } (J2000), the Sync here target. */
            viewCentre,
            /** fn({ raHours, decDeg }) whenever the view centre moves. */
            onView(fn) { st.onView = typeof fn === 'function' ? fn : null; st.lastCentre = null; kick(); },
            /** fn(hip) when a ringed focus star is tapped on the sky. */
            onPick(fn) { st.onPick = typeof fn === 'function' ? fn : null; },
            /** Centre the sky view on a J2000 RA (hours) / Dec (degrees) now. */
            lookAtRaDec(raHours, decDeg) {
                const now = Date.now(), q = precession(now).toDate(raHours, decDeg);
                const d = directionFromHaDec(st.lat, lstHours(now) - q.ra, q.dec);
                const p = altAzOf(d);
                lookAt(p.alt, p.az);
            },
            destroy() {
                if (st.raf) cancelAnimationFrame(st.raf);
                clearInterval(clock);
                ro?.disconnect(); mo.disconnect();
            }
        };
    }

    const api = { create, math };
    if (typeof module !== 'undefined' && module.exports) module.exports = api;
    if (root) root.PolarisMountView = api;
})(typeof window !== 'undefined' ? window : null);
