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

using System.Collections.Concurrent;
using Moq;
using NINA.Core.Utility;
using NINA.Image.Interfaces;
using NINA.INDI.Client;
using NINA.INDI.Devices;
using NINA.INDI.Protocol;
using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>The hour angle the Focus Stars page poses a GEM's RA axis with.
///
/// Field case: an HEQ5 on EQMod at latitude -35.36, parked on the south
/// celestial pole, then jogged in RA. EQMod published RA 1.298h, Dec -90 of
/// date and LST 21.434h (hour angle -3.86h, the RA motor 32° off its park
/// position), while ITelescope.RightAscension, J2000, sat at 12.107h, Dec
/// -89.854 and did not move: every RA of date at Dec -90 is the same J2000
/// point. The 3D model therefore never turned.</summary>
[TestFixture]
public class MountHourAngleTests {
    private static void SetNumber(IndiClient client, string device, string property,
                                  params (string Name, double Value)[] elements) {
        var props = client.Devices.GetOrAdd(device, _ => new ConcurrentDictionary<string, IndiProperty>());
        var values = new Dictionary<string, IndiNumberElement>();
        foreach (var (name, value) in elements) values[name] = new IndiNumberElement { Value = value };
        props[property] = new IndiNumberProperty { Device = device, Name = property, Values = new(values) };
    }

    private static void SetSwitch(IndiClient client, string device, string property, params string[] elements) {
        var props = client.Devices.GetOrAdd(device, _ => new ConcurrentDictionary<string, IndiProperty>());
        props[property] = new IndiSwitchProperty {
            Device = device, Name = property, Values = elements.ToDictionary(e => e, _ => false)
        };
    }

    /// <summary>What: EQMod's encoder steps to hour angle and Dec.
    /// Why: its park position is published only as encoder steps, and the
    /// Focus Stars sky marks where it is. The first case is a live HEQ5 in the
    /// south: the driver read RA 6.4100 h at LST 5.0590 h (HA -1.3510 h),
    /// Dec -52.706°, pier west, for these steps.</summary>
    [Test]
    public void Eqmod_EncodersGiveTheDriversHourAngleAndDec() {
        var live = IndiTelescope.EqmodEncodersToHaDec(6640600, 11579435, 9024000, 9024000, south: true);
        var park = IndiTelescope.EqmodEncodersToHaDec(8388608, 10644608, 9024000, 9024000, south: true);
        var parkNorth = IndiTelescope.EqmodEncodersToHaDec(8388608, 10644608, 9024000, 9024000, south: false);
        Assert.Multiple(() => {
            Assert.That(live.HaHours, Is.EqualTo(5.0589557 - 6.4099976).Within(1e-4));
            Assert.That(live.DecDeg, Is.EqualTo(-52.70637).Within(1e-3));
            Assert.That(live.PierEast, Is.False, "pier west");
            Assert.That(park.DecDeg, Is.EqualTo(-90).Within(1e-9), "default park: the south pole");
            Assert.That(park.HaHours, Is.EqualTo(-6).Within(1e-9), "counterweight down");
            Assert.That(parkNorth.DecDeg, Is.EqualTo(90).Within(1e-9), "the north pole in the north");
        });
    }

    /// <summary>What: the park position is read from EQMod's encoder steps
    /// and from an alt-az driver's degrees, and not from other units.
    /// Why: a guess at a driver's PARK_RA (RA or hour angle) would draw the
    /// park mark in the wrong place.</summary>
    [Test]
    public void Indi_ParkPositionWhereItsUnitsAreKnown() {
        using var client = new IndiClient();
        var scope = new IndiTelescope(client, "EQMod Mount");
        Assert.That(scope.ParkHaDec, Is.Null, "nothing published");
        SetNumber(client, "EQMod Mount", "TELESCOPE_PARK_POSITION", ("PARK_RA", 8388608), ("PARK_DEC", 10644608));
        Assert.That(scope.ParkHaDec, Is.Null, "PARK_RA in unknown units");
        SetNumber(client, "EQMod Mount", "STEPPERS", ("RASteps360", 9024000), ("DESteps360", 9024000));
        var props = client.Devices["EQMod Mount"];
        props["HEMISPHERE"] = new IndiSwitchProperty { Device = "EQMod Mount", Name = "HEMISPHERE",
            Values = new Dictionary<string, bool> { ["NORTH"] = false, ["SOUTH"] = true } };
        Assert.That(scope.ParkHaDec, Is.Null, "steps, but not from the EQMod driver");
        props["DRIVER_INFO"] = new IndiTextProperty { Device = "EQMod Mount", Name = "DRIVER_INFO",
            Values = new Dictionary<string, string> { ["DRIVER_NAME"] = "EQMod Mount", ["DRIVER_EXEC"] = "indi_eqmod_telescope" } };
        Assert.That(scope.ParkHaDec?.DecDeg, Is.EqualTo(-90).Within(1e-9), "EQMod steps");
        props["DRIVER_INFO"] = new IndiTextProperty { Device = "EQMod Mount", Name = "DRIVER_INFO",
            Values = new Dictionary<string, string> { ["DRIVER_EXEC"] = "indi_azgti_telescope" } };
        Assert.That(scope.ParkHaDec, Is.Null, "a look-alike driver is not assumed to count like EQMod");

        var altaz = new IndiTelescope(client, "AltAz Mount");
        SetNumber(client, "AltAz Mount", "TELESCOPE_PARK_POSITION", ("PARK_AZ", 0), ("PARK_ALT", 20));
        SetNumber(client, "AltAz Mount", "GEOGRAPHIC_COORD", ("LAT", -35), ("LONG", 149), ("ELEV", 600));
        Assert.Multiple(() => {
            Assert.That(altaz.ParkHaDec?.HaHours, Is.EqualTo(0).Within(1e-9), "due north: on the upper meridian");
            Assert.That(altaz.ParkHaDec?.DecDeg, Is.EqualTo(35).Within(1e-9), "20° up in the north at -35°: 70° north of the zenith");
        });
        SetNumber(client, "AltAz Mount", "TELESCOPE_PARK_POSITION", ("PARK_AZ", 8388608), ("PARK_ALT", 10644608));
        Assert.That(altaz.ParkHaDec, Is.Null, "encoder steps under PARK_AZ / PARK_ALT are not degrees");
    }

    /// <summary>Go-To Home on EQMod, which has no TELESCOPE_HOME: offered
    /// because the driver can park at its default (power-on) position.</summary>
    [Test]
    public void Indi_GoToHomeIsOfferedWithADefaultParkPosition() {
        using var client = new IndiClient();
        var scope = new IndiTelescope(client, "EQMod Mount");
        SetSwitch(client, "EQMod Mount", "TELESCOPE_PARK", "PARK", "UNPARK");
        Assert.That(scope.Capabilities.SupportsGoToHome, Is.False, "no park option yet");
        SetNumber(client, "EQMod Mount", "TELESCOPE_PARK_POSITION", ("PARK_RA", 8388608), ("PARK_DEC", 10644608));
        SetSwitch(client, "EQMod Mount", "TELESCOPE_PARK_OPTION",
                  "PARK_CURRENT", "PARK_DEFAULT", "PARK_WRITE_DATA", "PARK_PURGE_DATA");
        Assert.Multiple(() => {
            Assert.That(scope.Capabilities.SupportsGoToHome, Is.True);
            Assert.That(scope.Capabilities.SupportsFindHome, Is.False);
        });
    }

    [Test]
    public void Indi_GoToHomeIsOfferedWithAHomeProperty() {
        using var client = new IndiClient();
        SetSwitch(client, "Mount", "TELESCOPE_HOME", "FIND", "SET", "GO");
        Assert.That(new IndiTelescope(client, "Mount").Capabilities.SupportsGoToHome, Is.True);
    }

    [Test]
    public void Indi_GoToHomeWithNeitherIsNotSupported() {
        using var client = new IndiClient();
        SetSwitch(client, "Mount", "TELESCOPE_PARK", "PARK", "UNPARK");
        var scope = new IndiTelescope(client, "Mount");
        Assert.That(scope.Capabilities.SupportsGoToHome, Is.False);
        Assert.ThrowsAsync<NotSupportedException>(() => scope.GoToHomeAsync());
    }

    [Test]
    public void Indi_GoToHomeRefusesWhileParked() {
        using var client = new IndiClient();
        SetSwitch(client, "EQMod Mount", "TELESCOPE_PARK", "PARK", "UNPARK");
        ((IndiSwitchProperty)client.Devices["EQMod Mount"]["TELESCOPE_PARK"]).Values["PARK"] = true;
        SetNumber(client, "EQMod Mount", "TELESCOPE_PARK_POSITION", ("PARK_RA", 1), ("PARK_DEC", 2));
        SetSwitch(client, "EQMod Mount", "TELESCOPE_PARK_OPTION", "PARK_CURRENT", "PARK_DEFAULT");
        Assert.ThrowsAsync<InvalidOperationException>(() => new IndiTelescope(client, "EQMod Mount").GoToHomeAsync());
    }

    /// <summary>Go-To Home runs in the background: the move outlasts the
    /// browser's 15 s request timeout (27 s on the HEQ5 in the field).</summary>
    [Test]
    public async Task GoHomeRun_StartsWithoutWaitingAndReportsTheResult() {
        var gate = new TaskCompletionSource();
        var m = new Mock<ITelescope>();
        m.Setup(s => s.GoToHomeAsync(It.IsAny<CancellationToken>())).Returns(() => gate.Task);
        var run = new MountGoHomeRun(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        var arrived = 0;

        Assert.That(run.TryStart(m.Object, () => arrived++), Is.True, "returns before the mount is home");
        Assert.That(run.Running, Is.True);
        Assert.That(run.TryStart(m.Object, () => arrived++), Is.False, "one at a time");
        gate.SetResult();
        for (var i = 0; i < 100 && run.Running; i++) await Task.Delay(10);
        Assert.Multiple(() => {
            Assert.That(run.Running, Is.False);
            Assert.That(run.Run, Is.EqualTo(1));
            Assert.That(run.LastError, Is.Null);
            Assert.That(arrived, Is.EqualTo(1));
        });

        m.Setup(s => s.GoToHomeAsync(It.IsAny<CancellationToken>()))
         .ThrowsAsync(new OperationCanceledException("stopped"));
        Assert.That(run.TryStart(m.Object, () => arrived++), Is.True);
        for (var i = 0; i < 100 && run.Running; i++) await Task.Delay(10);
        Assert.Multiple(() => {
            Assert.That(run.Run, Is.EqualTo(2));
            Assert.That(run.LastError, Is.EqualTo("stopped"));
            Assert.That(arrived, Is.EqualTo(1));
        });
    }

    /// <summary>What: a mount with no home position goes to its park
    /// position and is unparked again, and the result says so.
    /// Why: the Focus Stars page's Home button must still bring such a mount to a
    /// known pose without leaving it parked.</summary>
    [Test]
    public async Task GoHomeRun_WithNoHomeGoesToTheParkPositionUnparked() {
        MountGoHomeRun.Poll = TimeSpan.FromMilliseconds(5);
        var parked = false;
        var calls = new List<string>();
        var m = new Mock<ITelescope>();
        m.SetupGet(s => s.Capabilities).Returns(new MountCapabilities(SupportsPark: true, SupportsTrackingToggle: true,
            SupportsSync: true, SupportsPierSide: true, SupportsManualJog: true));
        m.SetupGet(s => s.IsParked).Returns(() => parked);
        m.Setup(s => s.ParkAsync(It.IsAny<CancellationToken>())).Returns(() => { calls.Add("park"); parked = true; return Task.CompletedTask; });
        m.Setup(s => s.UnparkAsync(It.IsAny<CancellationToken>())).Returns(() => { calls.Add("unpark"); parked = false; return Task.CompletedTask; });
        var run = new MountGoHomeRun(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        Assert.That(MountGoHomeRun.CanGoHome(m.Object.Capabilities), Is.True);
        Assert.That(run.TryStart(m.Object, () => { }), Is.True);
        for (var i = 0; i < 200 && run.Running; i++) await Task.Delay(10);
        Assert.Multiple(() => {
            Assert.That(run.LastError, Is.Null);
            Assert.That(run.LastMethod, Is.EqualTo("park"));
            Assert.That(calls, Is.EqualTo(new[] { "park", "unpark" }));
            Assert.That(parked, Is.False, "left unparked");
        });
        m.Verify(s => s.GoToHomeAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public void Indi_HourAngleIsTheDriversLstMinusItsRaOfDate() {
        using var client = new IndiClient();
        SetNumber(client, "EQMod Mount", "EQUATORIAL_EOD_COORD", ("RA", 1.2983064715009647), ("DEC", -90));
        SetNumber(client, "EQMod Mount", "TIME_LST", ("LST", 21.43386517946466));
        var scope = new IndiTelescope(client, "EQMod Mount");
        Assert.That(scope.HourAngle, Is.EqualTo(-3.8644).Within(1e-4));
    }

    /// <summary>The field case itself: on the pole the RA axis still shows,
    /// where LST minus the J2000 RA does not move at all.</summary>
    [Test]
    public void Indi_OnThePoleTheHourAngleStillFollowsTheRaAxis() {
        using var client = new IndiClient();
        var scope = new IndiTelescope(client, "EQMod Mount");
        SetNumber(client, "EQMod Mount", "TIME_LST", ("LST", 21.43));

        SetNumber(client, "EQMod Mount", "EQUATORIAL_EOD_COORD", ("RA", 3.43), ("DEC", -90));
        var before = (Ha: scope.HourAngle!.Value, J2000Ra: scope.RightAscension);
        SetNumber(client, "EQMod Mount", "EQUATORIAL_EOD_COORD", ("RA", 1.30), ("DEC", -90));
        var after = (Ha: scope.HourAngle!.Value, J2000Ra: scope.RightAscension);

        Assert.That(after.Ha - before.Ha, Is.EqualTo(2.13).Within(1e-9), "32° of RA axis");
        Assert.That(after.J2000Ra, Is.EqualTo(before.J2000Ra).Within(1e-6),
            "the J2000 RA cannot see it, which is why the hour angle is needed");
    }

    [Test]
    public void Indi_WithoutTheDriversLstThereIsNoOwnHourAngle() {
        using var client = new IndiClient();
        SetNumber(client, "Mount", "EQUATORIAL_EOD_COORD", ("RA", 5), ("DEC", 20));
        Assert.That(new IndiTelescope(client, "Mount").HourAngle, Is.Null);
    }

    [Test]
    public void Indi_TheUnreadZeroPositionHasNoHourAngle() {
        using var client = new IndiClient();
        SetNumber(client, "Mount", "EQUATORIAL_EOD_COORD", ("RA", 0), ("DEC", 0));
        SetNumber(client, "Mount", "TIME_LST", ("LST", 4));
        Assert.That(new IndiTelescope(client, "Mount").HourAngle, Is.Null);
    }

    [Test]
    public void TheBackendsOwnHourAngleWins() {
        var m = new Mock<ITelescope>();
        m.SetupGet(s => s.HourAngle).Returns(-3.86);
        m.SetupGet(s => s.RightAscension).Returns(12.1);
        m.SetupGet(s => s.Declination).Returns(-89.85);
        Assert.That(MountHorizontal.HourAngle(m.Object, 149.08, DateTime.UtcNow), Is.EqualTo(-3.86));
    }

    /// <summary>Without one, LST minus the pointing of date, wrapped to
    /// -12..12.</summary>
    [Test]
    public void WithoutOneItIsTheSiteLstMinusThePointingOfDate() {
        var when = new DateTime(2026, 11, 23, 7, 20, 0, DateTimeKind.Utc);
        const double lon = 149.0821;
        var lst = PolarAlignmentMath.LocalSiderealHours(when, lon);
        var (raNow, _) = (lst - 2.5, 0.0);   // 2.5h west of the meridian, of date
        var j2000 = EquatorialEpoch.JNowToJ2000(((raNow % 24) + 24) % 24, 20, DateTime.UtcNow);

        var m = new Mock<ITelescope>();
        m.SetupGet(s => s.RightAscension).Returns(j2000.RaHours);
        m.SetupGet(s => s.Declination).Returns(j2000.DecDeg);

        Assert.That(MountHorizontal.HourAngle(m.Object, lon, when), Is.EqualTo(2.5).Within(1e-3));
    }

    [Test]
    public void NoPointingMeansNoHourAngle() {
        var m = new Mock<ITelescope>();
        m.SetupGet(s => s.RightAscension).Returns(double.NaN);
        m.SetupGet(s => s.Declination).Returns(double.NaN);
        Assert.That(MountHorizontal.HourAngle(m.Object, 0, DateTime.UtcNow), Is.Null);
    }
}
