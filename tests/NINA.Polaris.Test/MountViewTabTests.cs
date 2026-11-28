// N.I.N.A. Polaris
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

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>The Focus Stars page: a live 3D model of the mount drawn by
/// js/mount-view.js beside the floating Mount control's controls.</summary>
[TestFixture]
public class MountViewTabTests {
    private static string Here([CallerFilePath] string p = "") => p;
    private static string Wwwroot() =>
        Path.Combine(Path.GetDirectoryName(Here())!, "..", "..", "src", "NINA.Polaris", "wwwroot");
    private static string Html() => File.ReadAllText(Path.Combine(Wwwroot(), "index.html"));
    private static string Js() => File.ReadAllText(Path.Combine(Wwwroot(), "js", "app.js"));
    private static string ViewerPath() => Path.Combine(Wwwroot(), "js", "mount-view.js");

    private static string Uncommented(string html) =>
        Regex.Replace(html, "<!--.*?-->", m => new string(' ', m.Length), RegexOptions.Singleline);

    private static string TabPanel(string html) => DivAt(html, "<div x-show=\"focusTab === 'stars'\"", "Focus Stars page");

    // The <div> that opens with `open`, found by counting nested <div> / </div> pairs.
    private static string DivAt(string html, string open, string what) {
        var at = html.IndexOf(open, StringComparison.Ordinal);
        Assert.That(at, Is.GreaterThanOrEqualTo(0), $"{what} not found");
        var depth = 0;
        foreach (Match m in Regex.Matches(html[at..], @"<div\b|</div>")) {
            depth += m.Value == "</div>" ? -1 : 1;
            if (depth == 0) return html.Substring(at, m.Index + m.Length);
        }
        Assert.Fail($"unclosed {what}");
        return "";
    }

    /// <summary>What: the page is the FOCUS tab's fourth sub-page, after
    /// Filter offsets, and there is no MOUNT tab left on the rail or in the
    /// nav list. Why: the page is where a session's focusing starts, so it
    /// lives with the other focus tools rather than as a tab of its own.</summary>
    [Test]
    public void ThePageIsOnTheFocusTab() {
        var html = Uncommented(Html());
        var focus = DivAt(html, "<div x-show=\"tab === 'focus'\"", "FOCUS tab panel");
        var js = Js();
        Assert.Multiple(() => {
            Assert.That(focus, Does.Contain("<div x-show=\"focusTab === 'stars'\""), "page inside the FOCUS tab");
            int filters = focus.IndexOf("@click=\"focusTab = 'filters'", StringComparison.Ordinal),
                stars = focus.IndexOf("@click=\"focusTab = 'stars'\"", StringComparison.Ordinal);
            Assert.That(filters, Is.GreaterThanOrEqualTo(0));
            Assert.That(stars, Is.GreaterThan(filters), "Focus Stars after Filter offsets");
            Assert.That(html, Does.Not.Contain("data-nav-id=\"mountview\""), "no MOUNT rail button");
            Assert.That(html, Does.Not.Contain("tab === 'mountview'"));
            Assert.That(js, Does.Not.Contain("'mountview'"), "no MOUNT tab in nav list, saved tabs or panel tabs");
        });
    }

    /// <summary>What: the viewer draws and refreshes only while the Focus
    /// Stars page is on screen, and entering it from the FOCUS tab or from
    /// its sub-page strip starts it. Opening FOCUS keeps Focus Stars, rather
    /// than jumping to Manual Assist or Auto V-curve. Why: the page needs no
    /// focuser, and a hidden canvas has no size to draw into.</summary>
    [Test]
    public void ThePageStartsWhenShown() {
        var js = Js();
        string Body(string head) {
            var at = js.IndexOf(head, StringComparison.Ordinal);
            Assert.That(at, Is.GreaterThanOrEqualTo(0), head);
            return js.Substring(at, js.IndexOf("\n        },", at, StringComparison.Ordinal) - at);
        }
        Assert.Multiple(() => {
            Assert.That(Body("_mountViewVisible() {"), Does.Contain("this.tab === 'focus' && this.focusTab === 'stars'"));
            Assert.That(Body("mountViewPush() {"), Does.Contain("!this._mountViewVisible()"));
            var page = Body("focusStarsPageEnter() {");
            Assert.That(page, Does.Contain("this.mountViewOnTabEnter()"));
            Assert.That(page, Does.Contain("this.focusStarsOnTabEnter()"));
            var enter = Body("manualFocusOnTabEnter() {");
            Assert.That(enter.IndexOf("if (this.focusTab === 'stars')", StringComparison.Ordinal),
                Is.GreaterThanOrEqualTo(0).And.LessThan(enter.IndexOf("this.focusTab = 'vcurve'", StringComparison.Ordinal)),
                "Focus Stars kept on entering FOCUS");
            Assert.That(enter, Does.Contain("this.focusStarsPageEnter()"));
            Assert.That(Body("manualFocusOnTabSwitch() {"), Does.Contain("this.focusStarsPageEnter()"));
        });
    }

    /// <summary>What: mount-view.js loads before app.js. Why: the tab
    /// builds the viewer through window.PolarisMountView, which is only
    /// there once the script has run.</summary>
    [Test]
    public void TheViewerScriptLoadsBeforeTheApp() {
        var html = Uncommented(Html());
        var viewer = html.IndexOf("<script src=\"/js/mount-view.js", StringComparison.Ordinal);
        var app = html.IndexOf("<script src=\"/js/app.js", StringComparison.Ordinal);
        Assert.That(viewer, Is.GreaterThanOrEqualTo(0), "mount-view.js is not loaded");
        Assert.That(viewer, Is.LessThan(app));
    }

    /// <summary>What: the tab's mount controls are the shared floating
    /// Mount control, opened from the view toolbar, exactly as on the other
    /// tabs. No Go-To Home: Park does the same job. Solve & Sync and Sync
    /// here sit in the sky view's bottom bar, with the Mount pill.
    /// Why: one copy of the d-pad, with its press-and-hold stop and
    /// escalating stop fallbacks, not two that drift.</summary>
    [Test]
    public void TheControlsAreTheFloatingPanels() {
        var html = Uncommented(Html());
        var panel = TabPanel(html);
        Assert.Multiple(() => {
            Assert.That(panel, Does.Contain("toggleMountPanel($event)"), "Mount button");
            var bar = panel[panel.IndexOf("mountview-bottombar\"", StringComparison.Ordinal)..];
            bar = bar[..bar.IndexOf("class=\"mountview-topleft\"", StringComparison.Ordinal)];
            int star = bar.IndexOf("focusStarSlew(focusStarSelectedItem())", StringComparison.Ordinal),
                centre = bar.IndexOf("mountViewSlewCentre()", StringComparison.Ordinal),
                home = bar.IndexOf("mountGoToHome()", StringComparison.Ordinal),
                solve = bar.IndexOf("solveAndSyncHere()", StringComparison.Ordinal),
                sync = bar.IndexOf("mountViewSyncCentre()", StringComparison.Ordinal),
                pill = bar.IndexOf("toggleMountPanel($event)", StringComparison.Ordinal);
            Assert.That(star, Is.GreaterThanOrEqualTo(0), "Slew Star in the bottom bar");
            Assert.That(new[] { star, centre, home, solve, sync, pill }, Is.Ordered.Ascending,
                "Slew Star, Slew Center, Home, Solve & Sync, Sync here, Mount");
            Assert.That(bar, Does.Not.Contain("Reset view"), "a double tap resets the view");
            var topLeft = panel[panel.IndexOf("class=\"mountview-topleft\"", StringComparison.Ordinal)..];
            topLeft = topLeft[..topLeft.IndexOf("class=\"mountview-toolbar\"", StringComparison.Ordinal)];
            int scope = topLeft.IndexOf("mountview-status-scope", StringComparison.Ordinal),
                mark = topLeft.IndexOf("mountview-status-centre", StringComparison.Ordinal),
                picked = topLeft.IndexOf("mountview-status-star", StringComparison.Ordinal);
            Assert.That(new[] { scope, mark, picked }, Is.Ordered.Ascending, "Scope, Centre, Star");
            Assert.That(scope, Is.GreaterThanOrEqualTo(0));
            var toolbar = panel[panel.IndexOf("class=\"mountview-toolbar\"", StringComparison.Ordinal)..];
            Assert.That(toolbar.IndexOf("cycleMountViewGrid()", StringComparison.Ordinal),
                Is.LessThan(toolbar.IndexOf("mountViewStarsOpen = !mountViewStarsOpen", StringComparison.Ordinal)), "Focus stars right of Grid");
            Assert.That(panel, Does.Not.Contain("cycleMountViewProjection()"), "one view: stereographic");
            Assert.That(panel, Does.Not.Contain("mountMoveStart("), "no second d-pad");
            Assert.That(Js(), Does.Contain("mountPanelTabs: ['sky', 'focus', 'preview', 'live']"));
            var at = html.IndexOf("<div class=\"mount-float-panel\"", StringComparison.Ordinal);
            Assert.That(at, Is.GreaterThanOrEqualTo(0));
            var floating = html[at..html.IndexOf("class=\"editor-export-backdrop\"", at, StringComparison.Ordinal)];
            foreach (var call in new[] { "mountMoveStart('n', $event)", "mountStop()", "toggleTracking()", "parkMount()",
                                         "unparkMount()", "horizonAddFromMount()" })
                Assert.That(floating, Does.Contain(call), call);
            Assert.That(floating, Does.Not.Contain("mountGoToHome()"), "the same panel as on every other tab");
        });
    }

    /// <summary>What: every status tick and a disconnect hand the viewer the
    /// new pose. Why: without the push the model only moves when the tab is
    /// re-entered.</summary>
    [Test]
    public void StatusTicksReachTheViewer() {
        var js = Js();
        // The status handler, from the mount state assignment to the
        // selectedTelescope mirror that always follows it. Fields can be
        // added to the assignment without moving either end.
        var assign = js.IndexOf("slewRates: eq.telescope.slewRates || []", StringComparison.Ordinal);
        Assert.That(assign, Is.GreaterThanOrEqualTo(0), "mount status assignment not found");
        var assignEnd = js.IndexOf("this.selectedTelescope = eq.telescope.connected", assign, StringComparison.Ordinal);
        Assert.That(assignEnd, Is.GreaterThan(assign), "end of the mount status handler not found");
        Assert.That(js[assign..assignEnd], Does.Contain("this.mountViewPush();"), "status tick does not push the pose");
        var disc = js.IndexOf("async equipDisconnectMount()", StringComparison.Ordinal);
        var discEnd = js.IndexOf("toast('Mount disconnected'", disc, StringComparison.Ordinal);
        Assert.That(js[disc..discEnd], Does.Contain("this.mountViewPush();"));
    }

    /// <summary>What: the tab's new strings are in every catalog, with no
    /// dashes. Why: CONTRIBUTING requires the translation in the same change.</summary>
    [Test]
    public void TheNewStringsAreTranslated() {
        var keys = new[] {
            "Focus Stars", "Bright stars to focus on, on a live view of the mount and the sky", "3D view of the mount",
            "Reset view", "Mount not connected",
            "Turn the view so the scope's reticle is on the red centre mark (double click does the same). With no mount, back to the starting view.",
            "GOING HOME", "PARKING",
            "Moon", "Away from the Moon",
            "Mount: EQ", "Mount: Alt-AZ", "Which kind of mount to draw: EQ (German equatorial) or Alt-AZ",
            "Magnitude range", "Brightest magnitude", "Faintest magnitude", "to",
            "Slew Star", "Slew Center", "Slew the mount to the red mark in the middle of the sky view", "Open the list of bright stars well placed to focus on now",
            "Select a focus star first", "Bright stars well placed to focus on now", "Focus stars", "Close",
            "The red mark is below the horizon. Tilt the view up to the sky to sync.",
            "Send the mount back to where it was when it was turned on: counterweight down, pointing at the pole. A mount with no home position goes to its park position and is left unparked.",
            "The mount will slew at full speed back to where it was when it was turned on: counterweight down, pointing at the pole. A mount with no home position goes to its park position instead and is left unparked. Tracking stops. Make sure the slew path is clear of obstructions.",
            "No home position: mount is at its park position, not parked",
            "Home", "Going home...", "Send the mount home?", "Mount going home", "Mount is home", "Go-To Home failed", "Sync here",
            "Show stars between these magnitudes. Lower numbers are brighter; the brightest stars saturate and bloom, so they rank lower than the bright end of the range.",
            "Tell the mount it is pointing at the red mark in the middle of the sky view, without moving. Drag the sky until the mark is on what the scope really points at first. Use it to recover pointing when you can't plate-solve. Confirms first.",
            "Mount synced to the view centre (no movement)", "The centre of the sky view", "Sky view not ready",
            "Scope", "Centre", "Clear", "Deselect the star",
            "Max slew", "Leave out stars further than this from where the mount points now. Needs a connected mount.",
            "Ticked limits leave stars out. Unticked ones leave none out but still count toward the order, at half weight.",
            "Switch the sky view: Perspective, like a camera; Stereographic, like the SKY tab, starts at eye level and zooms out past the zenith",
            "Leave out stars this close to the Moon while it is up. The distance is drawn round the Moon on the sky."
        };
        var dir = Path.Combine(Wwwroot(), "data", "locales");
        Assert.Multiple(() => {
            foreach (var lang in new[] { "_source", "pt-BR", "es", "fr", "de", "ru" }) {
                var cat = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    File.ReadAllText(Path.Combine(dir, lang + ".json")))!;
                foreach (var key in keys) {
                    Assert.That(cat.ContainsKey(key), $"{lang} has no entry for '{key}'");
                    if (lang == "_source") continue;
                    var value = cat.GetValueOrDefault(key) ?? "";
                    Assert.That(value, Is.Not.Empty, $"{lang}: '{key}'");
                    Assert.That(value, Does.Not.Contain("—").And.Not.Contain("–"), $"{lang}: '{key}'");
                }
            }
        });
    }

    /// <summary>What: the pose maths in mount-view.js. A target on the
    /// meridian puts the Dec axis due east for pierEast and due west for
    /// pierWest; the normal side keeps the counterweight down in both
    /// hemispheres; the mechanical angles rebuild the same pointing. Why:
    /// a sign slip here draws the OTA on the wrong side of the pier, which
    /// is the one thing the view is for. Needs node; skipped without it.</summary>
    [Test]
    public void ThePoseMathsHolds() {
        const string script = """
            const m = require(process.argv[1]).math;
            const dot = (a, b) => a[0]*b[0] + a[1]*b[1] + a[2]*b[2];
            const bad = [];
            const ok = (c, msg) => { if (!c) bad.push(msg); };
            ok(Math.abs(m.gemPose(45, 45, 180, 'pierEast').dec[0] - 1) < 1e-9, 'meridian pierEast');
            ok(Math.abs(m.gemPose(45, 45, 180, 'pierWest').dec[0] + 1) < 1e-9, 'meridian pierWest');
            ok(m.gemPose(45, 30, 250, 'pierEast').dec[1] > 0, 'north west target cw down');
            ok(m.gemPose(45, 30, 110, 'pierWest').dec[1] > 0, 'north east target cw down');
            ok(m.gemPose(-35, 30, 290, 'pierEast').dec[1] > 0, 'south west target cw down');
            const ax = m.polarAxis(-35);
            ok(ax[1] > 0 && ax[2] > 0, 'south polar axis up and south');
            for (const [lat, alt, az, side] of [[51.5, 20, 75, 'pierWest'], [-33.9, 60, 300, 'pierEast'],
                                                 [10, 80, 10, 'pierUnknown'], [65, -5, 200, 'pierWest']]) {
                const g = m.gemPose(lat, alt, az, side), r = m.gemFromAngles(lat, g.ra, g.decAngle);
                ok(dot(r.ota, m.directionFromAltAz(alt, az)) > 1 - 1e-9, `round trip ${lat},${alt},${az}`);
                ok(dot(r.dec, g.dec) > 1 - 1e-9, `dec round trip ${lat},${alt},${az}`);
            }
            // Hour angle pose: agrees with the alt/az pose off the pole, and
            // still turns the RA axis on it (where alt/az cannot show RA motion).
            for (const [lat, ha, dec, side] of [[51.5, 2.5, 20, 'pierEast'], [-35.4, -4, -30, 'pierWest'], [10, 7, 60, 'pierUnknown']]) {
                const d = m.directionFromHaDec(lat, ha, dec);
                const alt = Math.asin(d[1]) * 180 / Math.PI, az = (Math.atan2(d[0], -d[2]) * 180 / Math.PI + 360) % 360;
                const a = m.gemPoseEq(lat, ha, dec, side), b = m.gemPose(lat, alt, az, side);
                ok(dot(a.ota, b.ota) > 1 - 1e-9 && dot(a.dec, b.dec) > 1 - 1e-6, `ha pose ${lat},${ha},${dec}`);
            }
            ok(m.directionFromHaDec(-35, 6, 0)[0] < -0.999, 'HA +6h on the equator is due west');
            // Precession J2000 -> date: matches AstronomyEngine (whose
            // equator of date adds nutation, ~10") and round-trips exactly.
            // Without it the map sat ~0.36 deg off the sky and the view
            // centre read hours of RA out on the pole.
            const P = m.precession(Date.UTC(2026, 10, 24, 12));
            for (const [ra, dec, refRa, refDec] of [[6.7525, -16.716, 6.77265, -16.74395], [18, 0, 18.02312, -0.00151],
                                                     [2.530, 89.264, 3.11589, 89.37699]]) {
                const q = P.toDate(ra, dec), r = Math.PI / 180;
                const off = Math.acos(Math.min(1, Math.sin(q.dec*r)*Math.sin(refDec*r)
                    + Math.cos(q.dec*r)*Math.cos(refDec*r)*Math.cos((q.ra - refRa)*15*r))) / r * 3600;
                ok(off < 20, `precession ${ra},${dec} is ${off.toFixed(1)}" from AstronomyEngine`);
                const b = P.toJ2000(q.ra, q.dec);
                ok(Math.abs(((b.ra - ra + 36) % 24) - 12) < 1e-9 && Math.abs(b.dec - dec) < 1e-9, `precession round trip ${ra},${dec}`);
            }
            // The view centre's RA/Dec (Sync here's target) inverts the
            // stars' placement exactly.
            for (const [lat, ha, dec] of [[-33.9, 3.5, -60], [51.5, -9, 40], [0, 11, 5]]) {
                const r = m.haDecFromDirection(lat, m.directionFromHaDec(lat, ha, dec));
                ok(Math.abs(r.ha - ha) < 1e-9 && Math.abs(r.dec - dec) < 1e-9, `ha/dec round trip ${lat},${ha},${dec}`);
            }
            const p1 = m.gemPoseEq(-35.4, -6, -90, 'pierWest'), p2 = m.gemPoseEq(-35.4, -3, -90, 'pierWest');
            ok(Math.abs(Math.abs(p2.ra - p1.ra) * 180 / Math.PI - 45) < 1e-6, 'RA axis turns on the pole');
            // Alt-az: update() stores [az, alt] in radians, and the drawing
            // must rebuild the same pointing from them. Radians fed to the
            // degrees pose drew the scope level and facing north.
            for (const [alt, az] of [[33.9, 180], [45, 90], [60, 270], [10, 15]]) {
                const p = m.altAzFromAngles(az * Math.PI / 180, alt * Math.PI / 180);
                ok(dot(p.ota, m.directionFromAltAz(alt, az)) > 1 - 1e-9, `alt-az round trip ${alt},${az}`);
            }
            console.log(bad.length ? 'FAIL ' + bad.join('; ') : 'OK');
            """;
        string output;
        try {
            using var p = Process.Start(new ProcessStartInfo("node") {
                ArgumentList = { "-e", script, ViewerPath() },
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
            })!;
            output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
        } catch (System.ComponentModel.Win32Exception) {
            Assert.Ignore("node is not installed");
            return;
        }
        Assert.That(output.Trim(), Is.EqualTo("OK"));
    }
}
