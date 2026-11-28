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


using Microsoft.Extensions.Logging;
using NINA.Image.Interfaces;

namespace NINA.Polaris.Services;

/// <summary>MOUNTVIEW: one Go-To Home at a time, run in the background.
/// The move takes as long as a full slew (27 s on an HEQ5 from a short
/// jog, minutes from the far side), longer than the browser waits for an
/// HTTP request, so the endpoint starts it and returns. The status socket
/// reports <see cref="Running"/> each tick, and <see cref="Run"/> with
/// <see cref="LastError"/> and <see cref="LastMethod"/> once it ends,
/// which the page turns into the "Mount is home" or failure message.
///
/// Home is the power-on position (counterweight down, OTA on the pole).
/// A mount with a home position, or a driver that can park at that
/// position (EQMod), goes there. One with neither goes to its park
/// position instead and is unparked again, so it ends up there without
/// being left parked.</summary>
public sealed class MountGoHomeRun {
    /// <summary>How long a move to home or park may take.</summary>
    internal static TimeSpan MoveTimeout = TimeSpan.FromMinutes(5);
    /// <summary>Poll interval while waiting on the mount; tests shorten it.</summary>
    internal static TimeSpan Poll = TimeSpan.FromMilliseconds(250);

    private readonly ILogger _log;
    private int _running;

    public MountGoHomeRun(ILogger log) { _log = log; }

    public bool Running => Volatile.Read(ref _running) == 1;
    /// <summary>Counts finished runs, so the page can tell a new result from one it has shown.</summary>
    public int Run { get; private set; }
    /// <summary>Why the last run failed; null when it got home.</summary>
    public string? LastError { get; private set; }
    /// <summary>Where the last run went: "home", or "park" for a mount
    /// with no home position. Null before the first run.</summary>
    public string? LastMethod { get; private set; }

    /// <summary>True when <paramref name="caps"/> allow either way home.</summary>
    public static bool CanGoHome(MountCapabilities caps) =>
        caps.SupportsGoToHome || caps.SupportsFindHome || caps.SupportsPark;

    /// <summary>Starts a Go-To Home on <paramref name="scope"/>. False when
    /// one is already running. <paramref name="arrived"/> runs once the mount
    /// is there.</summary>
    public bool TryStart(ITelescope scope, Action arrived) {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return false;
        var caps = scope.Capabilities;
        var toHome = caps == null || caps.SupportsGoToHome || caps.SupportsFindHome;
        _ = Task.Run(async () => {
            string? error = null;
            try {
                if (toHome) {
                    await scope.GoToHomeAsync();
                    // Find Home returns once the move starts on most drivers;
                    // wait for it to stop so "Mount is home" is true.
                    await WaitAsync(() => !scope.IsSlewing, "reach home");
                } else {
                    await ParkPositionAsync(scope);
                }
                arrived();
                _log.LogInformation("Go-To Home: the mount is at its {Where} position", toHome ? "home" : "park");
            } catch (Exception ex) {
                error = ex.Message;
                _log.LogWarning(ex, "Go-To Home failed");
            } finally {
                LastError = error;
                LastMethod = toHome ? "home" : "park";
                Run++;
                Volatile.Write(ref _running, 0);
            }
        });
        return true;
    }

    /// <summary>No home position: park, wait till parked, then unpark,
    /// so the mount stands at its park position, free to move.</summary>
    private static async Task ParkPositionAsync(ITelescope scope) {
        await scope.ParkAsync();
        await WaitAsync(() => scope.IsParked && !scope.IsParking && !scope.IsSlewing, "reach the park position");
        await scope.UnparkAsync();
        await WaitAsync(() => !scope.IsParked, "unpark", TimeSpan.FromSeconds(30));
    }

    private static async Task WaitAsync(Func<bool> done, string what, TimeSpan? timeout = null) {
        // Give the driver a moment to report the move before reading it.
        await Task.Delay(Poll * 2);
        var deadline = DateTime.UtcNow + (timeout ?? MoveTimeout);
        while (!done()) {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"The mount did not {what} within {(timeout ?? MoveTimeout).TotalMinutes:0.#} minutes.");
            await Task.Delay(Poll);
        }
    }
}
