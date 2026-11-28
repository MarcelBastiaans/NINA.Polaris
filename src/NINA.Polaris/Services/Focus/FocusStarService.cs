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


using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using CosineKitty;

namespace NINA.Polaris.Services.Focus;

/// <summary>The bright stars best placed right now to focus on, listed on
/// the Focus Stars page, the way PolarTppaTargetService picks where to start TPPA.
///
/// A focus star wants to be:
/// <list type="bullet">
/// <item>bright, within the caller's magnitude range (the Focus Stars page's
///   two-sided slider): brighter ranks higher, up to the bright end of the
///   range; brighter still counts against it, as it saturates.</item>
/// <item>high: above the caller's minimum altitude (20° by default) and the
///   custom horizon, for the next 20 minutes too, since seeing and
///   refraction blur a low star.</item>
/// <item>not about to cross the meridian, so a GEM does not flip mid-focus.</item>
/// <item>close to where the mount points now, so the slew is short.</item>
/// </list>
///
/// The stars come from the Hipparcos tiles the SKY map already ships
/// (wwwroot/sky/data/skydata/stars, Stellarium Web's EPH format). Order 0
/// holds every star down to mag 7, so all of the ~9,000 to mag 6.5 are in it.
/// Positions are J2000, as ITelescope slews take them.</summary>
public sealed class FocusStarService {
    /// <summary>Brightest and faintest a focus star can be: Sirius, and the
    /// naked-eye limit. The magnitude range is clamped to these.</summary>
    public const double MinMagnitude = -1.5, MaxMagnitude = 6.5;
    /// <summary>Defaults for the four limits' values (the API's; the MOUNT
    /// tab sends its own, a magnitude range of 2 to 4 by default).</summary>
    public const double DefaultMinMagnitude = MinMagnitude, DefaultMaxMagnitude = 2.0,
                        DefaultMinAltitude = 20.0, DefaultMoonDeg = 15.0, DefaultMaxSlew = 50.0;
    /// <summary>Limits applied as cut-offs unless the caller says otherwise.</summary>
    public const FocusLimit DefaultApplied = FocusLimit.Magnitude | FocusLimit.Altitude;
    /// <summary>How long a focus run is assumed to take: the star must stay up.</summary>
    public static readonly TimeSpan Session = TimeSpan.FromMinutes(20);

    private readonly ProfileService _profile;
    private readonly EquipmentManager? _equip;
    private readonly ILogger<FocusStarService> _log;
    private readonly string _tileDir;
    private readonly Lazy<IReadOnlyList<BrightStar>> _all;
    private readonly Lazy<IReadOnlyList<BrightStar>> _stars;
    private readonly Lazy<IReadOnlyList<int[]>> _lines;
    private readonly string? _cultureIndex;
    private readonly string? _iauNames;

    public FocusStarService(IWebHostEnvironment env, ProfileService profile,
                            ILogger<FocusStarService> log, EquipmentManager? equip = null)
        : this(Path.Combine(env.WebRootPath ?? "wwwroot", "sky", "data", "skydata", "stars", "Norder0"),
               profile, log, equip,
               Path.Combine(env.WebRootPath ?? "wwwroot", "sky", "data", "skydata", "skycultures", "western", "index.json"),
               Path.Combine(env.WebRootPath ?? "wwwroot", "data", "iau-star-names.json")) { }

    internal FocusStarService(string tileDir, ProfileService profile,
                              ILogger<FocusStarService> log, EquipmentManager? equip = null,
                              string? cultureIndex = null, string? iauNames = null) {
        _tileDir = tileDir;
        _iauNames = iauNames;
        _profile = profile;
        _log = log;
        _equip = equip;
        _cultureIndex = cultureIndex;
        _all = new Lazy<IReadOnlyList<BrightStar>>(Load);
        _stars = new Lazy<IReadOnlyList<BrightStar>>(() => _all.Value.Where(s => s.Magnitude <= MaxMagnitude).ToList());
        _lines = new Lazy<IReadOnlyList<int[]>>(LoadLines);
    }

    /// <summary>The focus candidates: every star to <see cref="MaxMagnitude"/>, brightest first.</summary>
    public IReadOnlyList<BrightStar> Stars => _stars.Value;

    /// <summary>Every star in the order-0 tiles (to mag 7), brightest first.</summary>
    public IReadOnlyList<BrightStar> AllStars => _all.Value;

    /// <summary>The western constellation figures as polylines of HIP numbers.</summary>
    public IReadOnlyList<int[]> ConstellationLines => _lines.Value;

    /// <summary>MOUNTVIEW: the sky behind the 3D model. Stars to
    /// <paramref name="maxMagnitude"/>, plus any fainter star a constellation
    /// line needs, and the lines. Compact rows: hip, RA h, Dec °, mag, B-V.</summary>
    public object Backdrop(double maxMagnitude = 6) {
        var lines = ConstellationLines;
        var needed = new HashSet<int>(lines.SelectMany(l => l));
        var stars = AllStars.Where(s => s.Magnitude <= maxMagnitude || needed.Contains(s.Hip))
            .Select(s => new object[] {
                s.Hip, Math.Round(s.RaHours, 5), Math.Round(s.DecDeg, 4), Math.Round(s.Magnitude, 2),
                double.IsFinite(s.BV) ? Math.Round(s.BV, 2) : 0.6
            }).ToList();
        return new { stars, lines };
    }

    private IReadOnlyList<int[]> LoadLines() {
        var list = new List<int[]>();
        if (_cultureIndex == null || !File.Exists(_cultureIndex)) return list;
        try {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(_cultureIndex));
            foreach (var con in doc.RootElement.GetProperty("constellations").EnumerateArray()) {
                if (!con.TryGetProperty("lines", out var ls)) continue;
                foreach (var l in ls.EnumerateArray()) {
                    var hips = l.EnumerateArray().Where(x => x.ValueKind == System.Text.Json.JsonValueKind.Number)
                                .Select(x => x.GetInt32()).ToArray();
                    if (hips.Length >= 2) list.Add(hips);
                }
            }
        } catch (Exception ex) {
            _log.LogWarning(ex, "Sky backdrop: could not read constellation lines from {File}", _cultureIndex);
        }
        return list;
    }

    /// <summary>The best <paramref name="limit"/> focus stars at
    /// <paramref name="nowUtc"/>, best first, for the Focus Stars page's list.
    /// <paramref name="fromRaHours"/> and <paramref name="fromDecDeg"/> are
    /// where the mount points (J2000); by default the connected mount's
    /// position, none when disconnected.
    ///
    /// Four limits, each with its value: the magnitude range
    /// <paramref name="minMagnitude"/> to <paramref name="maxMagnitude"/>,
    /// <paramref name="minAltitudeDeg"/> (now and in 20 minutes),
    /// <paramref name="moonDeg"/> (distance from the Moon while it is up)
    /// and <paramref name="maxSlewDeg"/> (from where the mount points). A
    /// limit in <paramref name="applied"/> is a cut-off and ranks at full
    /// weight; one left out cuts nothing but still ranks, at half weight,
    /// measured against its value (see <see cref="Score"/>). Stars always
    /// have to clear the custom horizon and be no fainter than
    /// <see cref="MaxMagnitude"/>.</summary>
    public IReadOnlyList<FocusStarSuggestion> Suggest(int limit = 12, DateTime? nowUtc = null,
                                                      double? fromRaHours = null, double? fromDecDeg = null,
                                                      double maxMagnitude = DefaultMaxMagnitude,
                                                      double minAltitudeDeg = DefaultMinAltitude,
                                                      double moonDeg = DefaultMoonDeg,
                                                      double maxSlewDeg = DefaultMaxSlew,
                                                      FocusLimit applied = DefaultApplied,
                                                      double minMagnitude = DefaultMinMagnitude) {
        minMagnitude = double.IsFinite(minMagnitude) ? Math.Clamp(minMagnitude, MinMagnitude, MaxMagnitude) : MinMagnitude;
        maxMagnitude = double.IsFinite(maxMagnitude) ? Math.Clamp(maxMagnitude, MinMagnitude, MaxMagnitude) : DefaultMaxMagnitude;
        if (minMagnitude > maxMagnitude) (minMagnitude, maxMagnitude) = (maxMagnitude, minMagnitude);
        minAltitudeDeg = Math.Clamp(minAltitudeDeg, 0, 89);
        moonDeg = double.IsFinite(moonDeg) ? Math.Clamp(moonDeg, 1, 90) : DefaultMoonDeg;
        maxSlewDeg = double.IsFinite(maxSlewDeg) ? Math.Clamp(maxSlewDeg, 1, 180) : DefaultMaxSlew;
        var p = _profile.Active;
        double lat = p.Latitude, lon = p.Longitude;
        var now = nowUtc ?? DateTime.UtcNow;
        var later = now + Session;
        var horizon = p.HorizonPoints ?? new List<HorizonPoint>();

        if (fromRaHours == null && _equip?.Telescope is { IsConnected: true } scope
            && double.IsFinite(scope.RightAscension) && double.IsFinite(scope.Declination)) {
            fromRaHours = scope.RightAscension;
            fromDecDeg = scope.Declination;
        }

        bool cutMag = applied.HasFlag(FocusLimit.Magnitude);
        double cutAlt = applied.HasFlag(FocusLimit.Altitude) ? minAltitudeDeg : 0;
        double lst = MeridianFlipService.ComputeLstHours(now, lon);
        // The Moon counts only while it is up; down, it neither cuts nor ranks.
        var moon = Moon(now);
        bool moonUp = moon.AltDeg > 0;
        var results = new List<FocusStarSuggestion>();
        foreach (var s in Stars) {
            if (cutMag && (s.Magnitude < minMagnitude || s.Magnitude > maxMagnitude)) continue;
            double? moonSep = moonUp ? Separation(moon.RaHours, moon.DecDeg, s.RaHours, s.DecDeg) : null;
            if (applied.HasFlag(FocusLimit.Moon) && moonSep < moonDeg) continue;
            double? sep = fromRaHours is double fr && fromDecDeg is double fd
                ? Separation(fr, fd, s.RaHours, s.DecDeg) : null;
            if (applied.HasFlag(FocusLimit.Slew) && sep > maxSlewDeg) continue;
            var (alt, az) = AltitudeService.RaDecToAltAz(s.RaHours, s.DecDeg, now, lat, lon);
            if (alt < Math.Max(cutAlt, HorizonProfile.AltitudeAt(horizon, az))) continue;
            var (alt2, az2) = AltitudeService.RaDecToAltAz(s.RaHours, s.DecDeg, later, lat, lon);
            if (alt2 < Math.Max(cutAlt, HorizonProfile.AltitudeAt(horizon, az2))) continue;

            double ha = Wrap12(lst - s.RaHours);
            bool crossesMeridian = ha < 0 && -ha < Session.TotalHours + 0.1;

            results.Add(new FocusStarSuggestion(
                Name: s.Name, Designation: s.Designation, Hip: s.Hip,
                RaHours: s.RaHours, DecDeg: s.DecDeg, Magnitude: Math.Round(s.Magnitude, 2),
                AltDeg: Math.Round(alt, 1), AzDeg: Math.Round(az, 1),
                HourAngleHours: Math.Round(ha, 2),
                SlewDeg: sep is double d ? Math.Round(d, 1) : null,
                CrossesMeridian: crossesMeridian,
                Score: Score(s.Magnitude, alt, sep, crossesMeridian, moonSep,
                             maxMagnitude, minAltitudeDeg, moonDeg, maxSlewDeg, applied, minMagnitude)));
        }
        return results.OrderByDescending(r => r.Score).ThenBy(r => r.Magnitude).Take(Math.Clamp(limit, 1, 50)).ToList();
    }

    /// <summary>0..100, the weighted mean of five parts, each 0..1:
    /// brightness (35), altitude (35), a short slew (20), distance from
    /// the Moon (10) and not crossing the meridian during the focus run
    /// (10). The first four are the list's limits: a limit not in
    /// <paramref name="applied"/> counts at half its weight, so an unticked
    /// limit still steers the order without leaving stars out.</summary>
    internal static int Score(double mag, double altDeg, double? slewDeg, bool crossesMeridian,
                              double? moonSepDeg = null,
                              double maxMagnitude = DefaultMaxMagnitude, double minAltitudeDeg = DefaultMinAltitude,
                              double moonDeg = DefaultMoonDeg, double maxSlewDeg = DefaultMaxSlew,
                              FocusLimit applied = DefaultApplied, double minMagnitude = DefaultMinMagnitude) {
        double W(FocusLimit l, double full) => applied.HasFlag(l) ? full : full / 2;
        // Brighter is better across the magnitude range, from its faint end
        // (0) to its bright end (1). Brighter than the range falls off again
        // at the same rate: it saturates and blooms.
        double span = Math.Max(0.1, maxMagnitude - minMagnitude);
        double bright = mag >= minMagnitude
            ? Math.Clamp((maxMagnitude - mag) / span, 0, 1)
            : Math.Clamp(1 - (minMagnitude - mag) / span, 0, 1);
        // From the minimum altitude up, 30° above it is as good as it gets;
        // near the zenith an alt-az slows in azimuth and a GEM's cables pull.
        double high = Math.Max(0, Math.Clamp((altDeg - minAltitudeDeg) / 30, 0, 1) - (altDeg > 82 ? 10.0 / 35 : 0));
        // Right where the mount points is best, the maximum slew or more is
        // worst; with no mount position every star is middling.
        double near = slewDeg is double d ? 1 - Math.Clamp(d / maxSlewDeg, 0, 1) : 0.5;
        // At the Moon distance or further is as good as it gets; the Moon
        // down (no separation) is the same as far away.
        double dark = moonSepDeg is double m ? Math.Clamp(m / moonDeg, 0, 1) : 1;
        double steady = crossesMeridian ? 0 : 1;

        double wB = W(FocusLimit.Magnitude, 35), wA = W(FocusLimit.Altitude, 35),
               wS = W(FocusLimit.Slew, 20), wM = W(FocusLimit.Moon, 10), wF = 10;
        double sum = wB * bright + wA * high + wS * near + wM * dark + wF * steady;
        return (int)Math.Round(100 * sum / (wB + wA + wS + wM + wF));
    }

    /// <summary>MOUNTVIEW: where the Moon is on the sky at <paramref name="nowUtc"/>
    /// (now by default) for the active site, for the Focus Stars page to draw it to
    /// scale with its phase and for <see cref="Suggest"/> to keep focus stars
    /// away from it. RA/Dec are topocentric J2000 like the stars (parallax
    /// moves the Moon up to a degree, two of its own widths); the Sun's
    /// position gives the direction of the bright limb.</summary>
    public MoonSky Moon(DateTime? nowUtc = null) {
        var p = _profile.Active;
        var t = new AstroTime(nowUtc ?? DateTime.UtcNow);
        var obs = new Observer(p.Latitude, p.Longitude, p.Altitude);
        var j2000 = Astronomy.Equator(Body.Moon, t, obs, EquatorEpoch.J2000, Aberration.Corrected);
        var ofDate = Astronomy.Equator(Body.Moon, t, obs, EquatorEpoch.OfDate, Aberration.Corrected);
        var hor = Astronomy.Horizon(t, obs, ofDate.ra, ofDate.dec, Refraction.Normal);
        var sun = Astronomy.Equator(Body.Sun, t, obs, EquatorEpoch.J2000, Aberration.Corrected);
        var illum = Astronomy.Illumination(Body.Moon, t);
        const double MoonRadiusKm = 1737.4, KmPerAu = 149597870.7;
        double radiusDeg = Math.Asin(MoonRadiusKm / (j2000.dist * KmPerAu)) * 180 / Math.PI;
        return new MoonSky(
            RaHours: Math.Round(j2000.ra, 5), DecDeg: Math.Round(j2000.dec, 4),
            AltDeg: Math.Round(hor.altitude, 2), AzDeg: Math.Round(hor.azimuth, 2),
            RadiusDeg: Math.Round(radiusDeg, 4),
            PhaseAngleDeg: Math.Round(illum.phase_angle, 2), Illumination: Math.Round(illum.phase_fraction, 3),
            SunRaHours: Math.Round(sun.ra, 5), SunDecDeg: Math.Round(sun.dec, 4));
    }

    private static double Wrap12(double h) { h %= 24; if (h > 12) h -= 24; if (h <= -12) h += 24; return h; }

    /// <summary>Angular separation in degrees.</summary>
    internal static double Separation(double ra1H, double dec1D, double ra2H, double dec2D) {
        double r = Math.PI / 180;
        double d1 = dec1D * r, d2 = dec2D * r, dra = (ra2H - ra1H) * 15 * r;
        double c = Math.Sin(d1) * Math.Sin(d2) + Math.Cos(d1) * Math.Cos(d2) * Math.Cos(dra);
        return Math.Acos(Math.Clamp(c, -1, 1)) / r;
    }

    /// <summary>HIP number to IAU-approved name, from wwwroot/data/iau-star-names.json
    /// (the IAU WGSN Catalog of Star Names, CC BY 4.0). Empty when missing.</summary>
    private Dictionary<int, string> LoadIauNames() {
        var map = new Dictionary<int, string>();
        if (_iauNames == null || !File.Exists(_iauNames)) return map;
        try {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(_iauNames));
            foreach (var p in doc.RootElement.GetProperty("names").EnumerateObject())
                if (int.TryParse(p.Name, out var hip) && p.Value.GetString() is { Length: > 0 } name) map[hip] = name;
        } catch (Exception ex) {
            _log.LogWarning(ex, "Focus stars: could not read IAU star names from {File}", _iauNames);
        }
        return map;
    }

    private IReadOnlyList<BrightStar> Load() {
        var list = new List<BrightStar>();
        if (!Directory.Exists(_tileDir)) {
            _log.LogWarning("Focus stars: no star tiles at {Dir}", _tileDir);
            return list;
        }
        foreach (var path in Directory.EnumerateFiles(_tileDir, "*.eph", SearchOption.AllDirectories)) {
            try {
                list.AddRange(EphStarTile.Read(File.ReadAllBytes(path)));
            } catch (Exception ex) {
                _log.LogWarning(ex, "Focus stars: could not read {Tile}", path);
            }
        }
        _log.LogInformation("Focus stars: {Count} stars loaded", list.Count);
        // The tiles name only the best-known stars; the IAU's list adds the
        // rest of its approved names (Azha, Zaurak...) and wins where both
        // have one, being the official spelling.
        var iau = LoadIauNames();
        return list.Select(s => iau.TryGetValue(s.Hip, out var n) ? s with { Name = n } : s)
                   .OrderBy(s => s.Magnitude).ToList();
    }
}

/// <summary>The focus star list's limits, for <see cref="FocusStarService.Suggest"/>:
/// the ones set are cut-offs at full ranking weight, the rest rank at half.</summary>
[Flags]
public enum FocusLimit { None = 0, Magnitude = 1, Altitude = 2, Moon = 4, Slew = 8 }

public record BrightStar(int Hip, string Name, string? Designation, double RaHours, double DecDeg, double Magnitude,
                         double BV = double.NaN);

/// <summary>The Moon for the Focus Stars sky: topocentric J2000 position, altitude
/// and azimuth, apparent radius, phase angle (0 full, 180 new), lit fraction,
/// and the Sun's J2000 position for the bright limb.</summary>
public record MoonSky(double RaHours, double DecDeg, double AltDeg, double AzDeg, double RadiusDeg,
                      double PhaseAngleDeg, double Illumination, double SunRaHours, double SunDecDeg);

public record FocusStarSuggestion(
    string Name, string? Designation, int Hip,
    double RaHours, double DecDeg, double Magnitude,
    double AltDeg, double AzDeg, double HourAngleHours,
    double? SlewDeg, bool CrossesMeridian, int Score);

/// <summary>Reader for a Stellarium Web Engine EPH star tile: an "EPHE"
/// file of chunks (type, length, data, crc); the "STAR" chunk holds a tile
/// header (version, ?, order, pix), a table header (row size, columns,
/// rows), the column list (name, type, unit, offset, size), then the rows,
/// zlib-compressed and byte-shuffled (byte j of every row together).</summary>
internal static class EphStarTile {
    public static List<BrightStar> Read(byte[] file) {
        var stars = new List<BrightStar>();
        if (file.Length < 8 || Encoding.ASCII.GetString(file, 0, 4) != "EPHE")
            throw new InvalidDataException("not an EPH file");
        int o = 8;
        while (o + 8 <= file.Length) {
            var type = Encoding.ASCII.GetString(file, o, 4);
            int len = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(o + 4));
            var data = file.AsSpan(o + 8, len);
            o += 8 + len + 4;
            if (type == "STAR") ReadTable(data, stars);
        }
        return stars;
    }

    private static int I32(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadInt32LittleEndian(s[at..]);

    private static void ReadTable(ReadOnlySpan<byte> d, List<BrightStar> stars) {
        int p = 16;   // tile header: version, ?, order, pix
        int rowSize = I32(d, p), nCol = I32(d, p + 4), nRows = I32(d, p + 8);
        p += 12;
        var cols = new Dictionary<string, (char Type, int Start, int Size)>();
        for (int i = 0; i < nCol; i++, p += 20) {
            var name = Encoding.ASCII.GetString(d.Slice(p, 4)).TrimEnd('\0');
            cols[name] = ((char)d[p + 4], I32(d, p + 12), I32(d, p + 16));
        }
        int rawSize = I32(d, p), compSize = I32(d, p + 4);
        p += 8;
        var shuffled = new byte[rawSize];
        using (var z = new ZLibStream(new MemoryStream(d.Slice(p, compSize).ToArray()), CompressionMode.Decompress))
            z.ReadExactly(shuffled);
        var raw = new byte[rawSize];
        for (int j = 0; j < rowSize; j++)
            for (int i = 0; i < nRows; i++)
                raw[i * rowSize + j] = shuffled[j * nRows + i];

        float F(int row, string c) => BinaryPrimitives.ReadSingleLittleEndian(raw.AsSpan(row * rowSize + cols[c].Start));
        for (int i = 0; i < nRows; i++) {
            int hip = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(i * rowSize + cols["hip"].Start));
            var ids = cols.TryGetValue("ids", out var ic)
                ? Encoding.UTF8.GetString(raw, i * rowSize + ic.Start, ic.Size).Split('\0')[0] : "";
            double ra = F(i, "ra") * 180 / Math.PI / 15, de = F(i, "de") * 180 / Math.PI;
            ra = ((ra % 24) + 24) % 24;
            var (name, designation) = Names(ids, hip);
            double bv = cols.ContainsKey("bv") ? F(i, "bv") : double.NaN;
            stars.Add(new BrightStar(hip, name, designation, ra, de, F(i, "vmag"), bv));
        }
    }

    private static readonly Dictionary<string, string> Greek = new() {
        ["alf"] = "α", ["bet"] = "β", ["gam"] = "γ", ["del"] = "δ", ["eps"] = "ε", ["zet"] = "ζ",
        ["eta"] = "η", ["tet"] = "θ", ["iot"] = "ι", ["kap"] = "κ", ["lam"] = "λ", ["mu."] = "μ",
        ["nu."] = "ν", ["ksi"] = "ξ", ["omi"] = "ο", ["pi."] = "π", ["rho"] = "ρ", ["sig"] = "σ",
        ["tau"] = "τ", ["ups"] = "υ", ["phi"] = "φ", ["chi"] = "χ", ["psi"] = "ψ", ["ome"] = "ω",
    };

    /// <summary>"Pollux|* bet Gem|* 78 Gem" gives ("Pollux", "β Gem");
    /// "* eps Gru" gives ("ε Gru", "ε Gru"); nothing gives "HIP n".</summary>
    internal static (string Name, string? Designation) Names(string ids, int hip) {
        var parts = ids.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string? proper = parts.FirstOrDefault(x => !x.StartsWith('*') && !x.StartsWith("V*")
                                                  && !x.StartsWith("HD ") && !x.StartsWith("HIP "));
        string? designation = null;
        foreach (var x in parts.Where(x => x.StartsWith("* ") || x.StartsWith("V* "))) {
            var t = x[(x.IndexOf(' ') + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (t.Length != 2) continue;
            var letter = t[0].Length >= 3 ? t[0][..3] : t[0];
            if (Greek.TryGetValue(letter, out var g)) {
                var suffix = t[0].Length > 3 ? t[0][3..].TrimStart('0') : "";
                designation = g + suffix + " " + t[1];
                break;
            }
            designation ??= t[0] + " " + t[1];   // Flamsteed, e.g. "78 Gem"
        }
        return (proper ?? designation ?? $"HIP {hip}", designation);
    }
}
