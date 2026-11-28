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


using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NINA.Polaris.Services;
using NINA.Polaris.Services.Focus;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>MOUNTVIEW: bright stars to focus on, read from the SKY map's
/// Hipparcos tiles and ranked for the site and time.</summary>
[TestFixture]
public class FocusStarServiceTests {
    private static string Here([CallerFilePath] string p = "") => p;
    private static string Tiles() => Path.Combine(Path.GetDirectoryName(Here())!, "..", "..", "src", "NINA.Polaris",
        "wwwroot", "sky", "data", "skydata", "stars", "Norder0");

    private string _dir = null!;
    private FocusStarService _svc = null!;

    [SetUp]
    public void SetUp() {
        _dir = Path.Combine(Path.GetTempPath(), "focus-stars-" + Guid.NewGuid().ToString("N"));
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Profiles:Directory"] = _dir }).Build();
        var profiles = new ProfileService(cfg, NullLogger<ProfileService>.Instance);
        profiles.Active.Latitude = -35.3589;
        profiles.Active.Longitude = 149.0821;
        _svc = new FocusStarService(Tiles(), profiles, NullLogger<FocusStarService>.Instance,
            cultureIndex: Path.Combine(Wwwroot(), "sky", "data", "skydata", "skycultures", "western", "index.json"),
            iauNames: Path.Combine(Wwwroot(), "data", "iau-star-names.json"));
    }

    [TearDown]
    public void TearDown() { try { Directory.Delete(_dir, true); } catch { } }

    /// <summary>What: the tile reader gets every star to mag 4 (about 540
    /// on the whole sky) with the right position. Why: a slip in the EPH
    /// layout reads garbage that still looks like numbers.</summary>
    [Test]
    public void TheTilesHoldTheBrightStars() {
        var stars = _svc.Stars;
        Assert.That(stars.Count(s => s.Magnitude <= 4), Is.InRange(500, 600));
        Assert.That(stars.Max(s => s.Magnitude), Is.LessThanOrEqualTo(FocusStarService.MaxMagnitude));
        Assert.That(stars.Count, Is.GreaterThan(5000), "every star to the naked-eye limit");
        var sirius = stars.Single(s => s.Hip == 32349);
        Assert.Multiple(() => {
            Assert.That(sirius.Name, Is.EqualTo("Sirius"));
            Assert.That(sirius.Designation, Is.EqualTo("α CMa"));
            Assert.That(sirius.RaHours, Is.EqualTo(6.7525).Within(0.001));   // 06h45m09s
            Assert.That(sirius.DecDeg, Is.EqualTo(-16.716).Within(0.01));
            Assert.That(sirius.Magnitude, Is.EqualTo(-1.46).Within(0.05));
            Assert.That(stars[0].Hip, Is.EqualTo(32349), "brightest first");
        });
    }

    /// <summary>What: the Focus Stars sky backdrop has the naked-eye stars and
    /// the constellation figures, and every star a figure needs. Why: a
    /// figure whose star is missing draws a broken line.</summary>
    [Test]
    public void TheBackdropHasTheStarsTheFiguresNeed() {
        var json = JsonSerializer.Serialize(_svc.Backdrop(6));
        using var doc = JsonDocument.Parse(json);
        var stars = doc.RootElement.GetProperty("stars").EnumerateArray()
            .Select(r => (Hip: r[0].GetInt32(), Mag: r[3].GetDouble())).ToList();
        var hips = stars.Select(x => x.Hip).ToHashSet();
        var lines = doc.RootElement.GetProperty("lines").EnumerateArray()
            .Select(l => l.EnumerateArray().Select(x => x.GetInt32()).ToArray()).ToList();
        var used = lines.SelectMany(l => l).Distinct().ToList();
        Assert.Multiple(() => {
            Assert.That(stars.Count(x => x.Mag <= 6), Is.InRange(4500, 6000), "naked-eye sky, about 5000 stars");
            Assert.That(hips, Does.Contain(32349), "Sirius");
            Assert.That(lines.Count, Is.GreaterThan(200), "88 figures, 214 polylines");
            Assert.That(used.Count(h => hips.Contains(h)) / (double)used.Count, Is.GreaterThan(0.98),
                "nearly every figure star is in the tiles (the rest are fainter than mag 7)");
        });
    }

    [TestCase("Pollux|* bet Gem|* 78 Gem", "Pollux", "β Gem")]
    [TestCase("* eps Gru", "ε Gru", "ε Gru")]
    [TestCase("Algieba|* gam01 Leo|* 41 Leo", "Algieba", "γ1 Leo")]
    [TestCase("* 78 Gem", "78 Gem", "78 Gem")]
    [TestCase("", "HIP 7", null)]
    public void StarNames(string ids, string name, string? designation) {
        Assert.That(EphStarTile.Names(ids, 7), Is.EqualTo((name, designation)));
    }

    /// <summary>What: every suggestion is above 30° now and in 20 minutes,
    /// best first. Why: a star that sinks mid-focus wastes the run.</summary>
    [Test]
    public void SuggestionsStayUpForTheFocusRun() {
        var now = new DateTime(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);
        var list = _svc.Suggest(8, now, maxMagnitude: 4);
        Assert.That(list, Has.Count.EqualTo(8));
        Assert.Multiple(() => {
            foreach (var s in list) {
                Assert.That(s.AltDeg, Is.GreaterThanOrEqualTo(30), s.Name);
                var (alt, _) = AltitudeService.RaDecToAltAz(s.RaHours, s.DecDeg,
                    now + FocusStarService.Session, -35.3589, 149.0821);
                Assert.That(alt, Is.GreaterThanOrEqualTo(30), s.Name + " in 20 min");
                Assert.That(s.Magnitude, Is.LessThanOrEqualTo(FocusStarService.MaxMagnitude));
                Assert.That(s.SlewDeg, Is.Null, "no mount, no slew distance");
            }
            Assert.That(list.Select(s => s.Score), Is.Ordered.Descending);
        });
    }

    /// <summary>What: with the mount's pointing given, a star right there
    /// gets a zero slew and outranks the same star seen from far away.</summary>
    [Test]
    public void AShortSlewRanksHigher() {
        var now = new DateTime(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);
        var pick = _svc.Suggest(20, now, maxMagnitude: 4)[5];
        var near = _svc.Suggest(50, now, pick.RaHours, pick.DecDeg, maxMagnitude: 4).Single(s => s.Hip == pick.Hip);
        var far = _svc.Suggest(50, now, Wrap(pick.RaHours + 12), -pick.DecDeg, maxMagnitude: 4)
            .SingleOrDefault(s => s.Hip == pick.Hip);
        Assert.That(near.SlewDeg, Is.EqualTo(0).Within(0.1));
        if (far != null) Assert.That(near.Score, Is.GreaterThan(far.Score));
    }

    private static double Wrap(double h) => ((h % 24) + 24) % 24;

    /// <summary>What: the Moon for the Focus Stars sky. Its apparent radius is
    /// about a quarter of a degree and its altitude agrees with its RA/Dec.
    /// Why: it is drawn to scale among the stars.</summary>
    [Test]
    public void TheMoonIsWhereItsCoordinatesSay() {
        var now = new DateTime(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);
        var m = _svc.Moon(now);
        var (alt, az) = AltitudeService.RaDecToAltAz(m.RaHours, m.DecDeg, now, -35.3589, 149.0821);
        Assert.Multiple(() => {
            Assert.That(m.RadiusDeg, Is.InRange(0.24, 0.28));
            Assert.That(m.Illumination, Is.InRange(0.0, 1.0));
            Assert.That(alt, Is.EqualTo(m.AltDeg).Within(1.0), "altitude (refraction aside)");
            Assert.That(Math.Abs(((az - m.AzDeg + 540) % 360) - 180), Is.LessThan(1.5 / Math.Max(0.2, Math.Cos(alt * Math.PI / 180))));
        });
    }

    /// <summary>What: with a Moon distance set, no suggestion is closer to
    /// the Moon than that while it is up, and with the Moon down the limit
    /// changes nothing. Why: the Moon's glare drowns a focus star.</summary>
    [Test]
    public void StarsNearTheMoonAreLeftOut() {
        var start = new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc);
        var up = Enumerable.Range(0, 24 * 14).Select(h => start.AddHours(h)).First(t => _svc.Moon(t).AltDeg > 40);
        var down = Enumerable.Range(0, 24 * 14).Select(h => start.AddHours(h)).First(t => _svc.Moon(t).AltDeg < -10);
        var moon = _svc.Moon(up);
        var all = _svc.Suggest(50, up, maxMagnitude: 4);
        var away = _svc.Suggest(50, up, maxMagnitude: 4, moonDeg: 40,
                                applied: FocusStarService.DefaultApplied | FocusLimit.Moon);
        Assert.Multiple(() => {
            Assert.That(all.Any(s => FocusStarService.Separation(moon.RaHours, moon.DecDeg, s.RaHours, s.DecDeg) < 40),
                Is.True, "some stars near the Moon to leave out");
            Assert.That(away.All(s => FocusStarService.Separation(moon.RaHours, moon.DecDeg, s.RaHours, s.DecDeg) >= 40),
                Is.True);
            // Mag 2 keeps the list under the 50 cap, so only the cut is compared.
            var downAll = _svc.Suggest(50, down).Select(s => s.Hip).ToList();
            Assert.That(downAll, Has.Count.LessThan(50));
            Assert.That(_svc.Suggest(50, down, moonDeg: 40,
                                     applied: FocusStarService.DefaultApplied | FocusLimit.Moon).Select(s => s.Hip),
                Is.EquivalentTo(downAll), "Moon down: nothing left out");
        });
    }

    /// <summary>What: the Focus Stars page's two limits. The defaults are mag 2 and
    /// 20°; nothing fainter or lower comes back, and raising the altitude
    /// only removes stars.</summary>
    [Test]
    public void TheMagnitudeAndAltitudeLimitsHold() {
        var now = new DateTime(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);
        var byDefault = _svc.Suggest(50, now);
        var strict = _svc.Suggest(50, now, maxMagnitude: 1.0, minAltitudeDeg: 60);
        Assert.Multiple(() => {
            Assert.That(byDefault, Is.Not.Empty);
            Assert.That(byDefault.All(s => s.Magnitude <= 2.0 && s.AltDeg >= 20), Is.True, "defaults mag 2, 20°");
            Assert.That(byDefault.Any(s => s.AltDeg < 30), Is.True, "20°, not 30°");
            Assert.That(strict.All(s => s.Magnitude <= 1.0 && s.AltDeg >= 60), Is.True);
            Assert.That(strict.Select(s => s.Hip), Is.SubsetOf(byDefault.Select(s => s.Hip)));
            Assert.That(_svc.Suggest(50, now, maxMagnitude: 4).Count, Is.GreaterThan(byDefault.Count));
        });
    }

    /// <summary>What: the max slew limit. Applied, nothing further than it
    /// from where the mount points comes back; not applied, the far stars
    /// come back too. Why: a long slew to focus wastes the night.</summary>
    [Test]
    public void TheMaxSlewLimitHolds() {
        var now = new DateTime(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);
        var pick = _svc.Suggest(20, now, maxMagnitude: 4)[0];
        var cut = _svc.Suggest(50, now, pick.RaHours, pick.DecDeg, maxMagnitude: 4, maxSlewDeg: 40,
                               applied: FocusStarService.DefaultApplied | FocusLimit.Slew);
        var all = _svc.Suggest(50, now, pick.RaHours, pick.DecDeg, maxMagnitude: 4, maxSlewDeg: 40);
        Assert.Multiple(() => {
            Assert.That(cut, Is.Not.Empty);
            Assert.That(cut.All(s => s.SlewDeg <= 40), Is.True);
            Assert.That(all.Any(s => s.SlewDeg > 40), Is.True, "unticked, far stars still listed");
        });
    }

    /// <summary>What: a limit left unticked still ranks, at half weight. A
    /// star that does well only on that limit gains less from it unticked
    /// than ticked, but still more than a star that does badly on it.</summary>
    [Test]
    public void AnUntickedLimitStillRanksAtHalfWeight() {
        const FocusLimit on = FocusStarService.DefaultApplied | FocusLimit.Slew;
        const FocusLimit off = FocusStarService.DefaultApplied;
        int nearOn = FocusStarService.Score(1.0, 60, 5, false, applied: on);
        int farOn = FocusStarService.Score(1.0, 60, 45, false, applied: on);
        int nearOff = FocusStarService.Score(1.0, 60, 5, false, applied: off);
        int farOff = FocusStarService.Score(1.0, 60, 45, false, applied: off);
        Assert.Multiple(() => {
            Assert.That(nearOff, Is.GreaterThan(farOff), "still counts unticked");
            Assert.That(nearOn - farOn, Is.GreaterThan(nearOff - farOff), "but counts less");
            Assert.That(FocusStarService.Score(1.0, 60, 5, false, moonSepDeg: 30, applied: off),
                Is.GreaterThan(FocusStarService.Score(1.0, 60, 5, false, moonSepDeg: 3, applied: off)), "Moon ranks unticked");
        });
    }

    /// <summary>What: the two-sided magnitude range. Applied, only stars
    /// between its ends come back; ranked, the bright end scores best and
    /// a star brighter than the range scores less, as it saturates.</summary>
    [Test]
    public void TheMagnitudeRangeHolds() {
        var now = new DateTime(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);
        var list = _svc.Suggest(50, now, minMagnitude: 2, maxMagnitude: 4);
        Assert.Multiple(() => {
            Assert.That(list, Is.Not.Empty);
            Assert.That(list.All(s => s.Magnitude >= 2 && s.Magnitude <= 4), Is.True);
            int atBright = FocusStarService.Score(2.0, 60, 10, false, maxMagnitude: 4, minMagnitude: 2);
            Assert.That(atBright, Is.GreaterThan(FocusStarService.Score(3.5, 60, 10, false, maxMagnitude: 4, minMagnitude: 2)));
            Assert.That(atBright, Is.GreaterThan(FocusStarService.Score(0.5, 60, 10, false, maxMagnitude: 4, minMagnitude: 2)),
                "brighter than the range counts against");
            Assert.That(_svc.Suggest(50, now, minMagnitude: 2, maxMagnitude: 4, applied: FocusLimit.Altitude)
                .Any(s => s.Magnitude < 2), Is.True, "unticked, the bright ones are back");
        });
    }

    /// <summary>What: the IAU names fill in what the star tiles leave
    /// out (η Eri is Azha, γ Eri Zaurak) and keep the ones they have.
    /// Why: below mag 2 the tiles name few stars, and the list showed
    /// mostly bare designations.</summary>
    [Test]
    public void IauNamesFillTheGaps() {
        var byHip = _svc.Stars.ToDictionary(s => s.Hip);
        Assert.Multiple(() => {
            Assert.That(byHip[13701].Name, Is.EqualTo("Azha"));
            Assert.That(byHip[13701].Designation, Is.EqualTo("η Eri"), "designation kept");
            Assert.That(byHip[18543].Name, Is.EqualTo("Zaurak"));
            Assert.That(byHip[32349].Name, Is.EqualTo("Sirius"));
            Assert.That(byHip[2081].Name, Is.EqualTo("Ankaa"));
        });
    }

    [TestCase(null, FocusLimit.Magnitude | FocusLimit.Altitude)]
    [TestCase("", FocusLimit.None)]
    [TestCase("slew, MOON,mag", FocusLimit.Slew | FocusLimit.Moon | FocusLimit.Magnitude)]
    [TestCase("alt,bogus", FocusLimit.Altitude)]
    public void TheApplyListParses(string? apply, FocusLimit expected) {
        Assert.That(NINA.Polaris.Endpoints.FocusEndpoints.ParseLimits(apply), Is.EqualTo(expected));
    }

    [Test]
    public void ScoreFavoursBrightHighAndNotFlipping() {
        Assert.Multiple(() => {
            Assert.That(FocusStarService.Score(-1.4, 65, 10, false),
                Is.GreaterThan(FocusStarService.Score(1.8, 65, 10, false)), "brighter");
            Assert.That(FocusStarService.Score(1.8, 65, 10, false),
                Is.GreaterThan(FocusStarService.Score(1.8, 35, 10, false)), "high");
            Assert.That(FocusStarService.Score(1.8, 65, 10, false),
                Is.GreaterThan(FocusStarService.Score(1.8, 65, 10, true)), "no flip mid-focus");
        });
    }

    private static string Wwwroot() => Path.Combine(Path.GetDirectoryName(Here())!, "..", "..", "src", "NINA.Polaris", "wwwroot");

    /// <summary>What: the Focus Stars page's focus star list is a drop-down over
    /// the sky, opened from the toolbar; choosing a star turns the view to
    /// it and closes the list, and Slew (the shared GoTo) is in the bottom
    /// bar. Why: the sky gets the whole width, and a second copy of the
    /// GoTo would drift from the one the rest of the app uses.</summary>
    [Test]
    public void TheFocusStarsListUsesTheSharedSlewAndSolve() {
        var html = File.ReadAllText(Path.Combine(Wwwroot(), "index.html"));
        var tab = html.IndexOf("<div x-show=\"focusTab === 'stars'\"", StringComparison.Ordinal);
        Assert.That(tab, Is.GreaterThanOrEqualTo(0), "Focus Stars page not found");
        var at = html.IndexOf("<section class=\"mountview-stars\"", StringComparison.Ordinal);
        Assert.That(at, Is.GreaterThan(tab));
        Assert.That(at, Is.LessThan(html.IndexOf("<!-- /focusTab === 'stars' -->", tab, StringComparison.Ordinal)),
            "on the Focus Stars page");
        var panel = html[at..html.IndexOf("</section>", at, StringComparison.Ordinal)];
        var stage = html[html.IndexOf("class=\"mountview-stage\"", tab, StringComparison.Ordinal)..at];
        Assert.Multiple(() => {
            Assert.That(panel, Does.Contain("x-show=\"mountViewStarsOpen\""), "a drop-down");
            Assert.That(stage, Does.Contain("@click=\"mountViewStarsOpen = !mountViewStarsOpen\""), "opened from the toolbar");
            foreach (var call in new[] { "loadFocusStars()", "@click=\"focusStarChoose(s)\"" })
                Assert.That(panel, Does.Contain(call), call);
            Assert.That(panel, Does.Not.Contain("mountViewCenterOn("), "no Center button: choosing centres");
            Assert.That(panel, Does.Contain("@click=\"focusStarSelect(null)\""), "Clear in the list");
            Assert.That(panel, Does.Not.Contain("focusStarSlew("), "Slew is in the bottom bar");
            Assert.That(stage, Does.Contain("focusStarSlew(focusStarSelectedItem())"), "Slew in the bottom bar");
        });
        var js = File.ReadAllText(Path.Combine(Wwwroot(), "js", "app.js"));
        Assert.That(js, Does.Contain("_mountViewer.onPick(hip => this.focusStarSelect(hip === this.focusStarSelected ? null : hip))"),
            "a tap on the sky selects; on the selected star, clears");
        Assert.That(js, Does.Contain("'/api/focus/stars?' + q"));
        Assert.Multiple(() => {
            foreach (var bit in new[] { "focusStarsSetLimit('minMag'", "focusStarsSetLimit('maxMag'", "focusStarsSetLimit('minAlt'",
                                        "focusStarsSetLimit('avoidMoon'", "focusStarsSetLimit('moonDeg'",
                                        "focusStarsSetLimit('useMag'", "focusStarsSetLimit('useAlt'",
                                        "focusStarsSetLimit('useSlew'", "focusStarsSetLimit('maxSlew'",
                                        "focusStarsSort('magnitude')", "focusStarsSort('altDeg')", "focusStarsSorted()" })
                Assert.That(panel, Does.Contain(bit), bit);
            // RA/Dec as table columns made the list too wide for an iPad.
            Assert.That(panel, Does.Not.Contain("formatRA(s.raHours)"), "no RA column");
            Assert.That(panel, Does.Not.Contain("formatDec(s.decDeg)"), "no Dec column");
        });
    }

    [Test]
    public void TheNewStringsAreTranslated() {
        var keys = new[] {
            "Focus stars", "Bright stars high in the sky now, best first",
            "Refresh for the current time, site and mount position", "Solve & Sync",
            "Capture a frame, plate solve it and sync the mount to it, without moving",
            "Stop the capture and plate solve", "Solving… Abort",
            "No star matches these limits right now. Lower the minimum altitude or raise the magnitude limit.",
            "Magnitude limit", "Show stars of this magnitude or brighter. Lower numbers are brighter.", "Min altitude",
            "Lowest altitude above the horizon a star may have, now and for the next 20 minutes",
            "Star", "Alt", "Az", "flip", "RA", "Dec",
            "Background", "Night sky", "Draw the night sky behind the mount, with the focus stars marked",
            "Could not load the sky", "Select a star in the list or on the sky",
            "Turn the sky view to the selected star", "Slew the mount to the selected star",
            "Sort by magnitude, largest to smallest; click again to reverse, then again for the ranked order",
            "Sort by altitude, largest to smallest; click again to reverse, then again for the ranked order",
            "Crosses the meridian within 20 minutes: a GEM may flip while you focus", "Could not load focus stars"
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
}
