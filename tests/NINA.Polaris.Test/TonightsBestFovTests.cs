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

using NUnit.Framework;
using NINA.Polaris.Services;

namespace NINA.Polaris.Test;

/// <summary>
/// "Fits my FOV" in Tonight's Best. The flag was hard-coded null on every
/// item, so the chip stayed greyed out ("connect a camera") with an ASI585MC
/// Pro connected and the FOV already computed (78.1' x 43.9'). The FOV below
/// is that rig's.
/// </summary>
[TestFixture]
public class TonightsBestFovTests {

    private const double W = 78.1, H = 43.9;

    [TestCase(30.0, true)]    // small: fits either way
    [TestCase(60.0, true)]    // between the sides: fits the long side
    [TestCase(78.1, true)]    // exactly the long side
    [TestCase(150.0, false)]  // M45: bigger than the frame
    public void Relaxed_UsesTheLongSide(double size, bool fits)
        => Assert.That(TonightsBestService.FitsFov(size, W, H), Is.EqualTo(fits));

    [TestCase(30.0, true)]
    [TestCase(43.9, true)]    // exactly the short side
    [TestCase(60.0, false)]   // fits relaxed, not strict
    public void Strict_UsesTheShortSide(double size, bool fits)
        => Assert.That(TonightsBestService.FitsFov(size, W, H, strict: true), Is.EqualTo(fits));

    [Test]
    public void PortraitFov_SameAnswer()
        => Assert.That(TonightsBestService.FitsFov(60, H, W), Is.True);

    [TestCase(0.0, 78.1, 43.9)]     // no catalogue size
    [TestCase(30.0, null, 43.9)]    // no FOV (no sensor or focal length)
    [TestCase(30.0, 78.1, null)]
    [TestCase(30.0, 0.0, 43.9)]
    public void UnknownSizeOrFov_IsNull(double size, double? w, double? h)
        => Assert.That(TonightsBestService.FitsFov(size, w, h), Is.Null);
}
