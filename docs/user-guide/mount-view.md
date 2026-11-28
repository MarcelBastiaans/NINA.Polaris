# Focus Stars (FOCUS tab)

**Focus Stars** is the fourth page of the [FOCUS](focus.md) tab, after
Manual Assist, Auto V-curve and Filter offsets. It needs no focuser, only
a mount. It shows a live 3D model of the mount against the night sky,
with a list of bright stars to focus on. The model turns as the mount moves, so you can see
where the scope is pointing and which side of the pier it is on without
being at the rig.

## Layout

In landscape (a desktop browser, or a phone or tablet on its side) the
sky view and the focus star list sit side by side, the list in a column
on the right that scrolls on its own. In portrait, or a narrow window,
they stack: the sky on top, the list below, and the page scrolls.

The toolbar at the top of the sky view has **Mount**, which shows or
hides the floating Mount control, **Go-To Home**, then **Model**, **Grid**
(with the night sky) and **Background**.

## The model

- **German equatorial** mounts are drawn on a tripod with the polar axis
  tilted to your site latitude (Settings, Location), pointing at the pole
  above your horizon. The OTA sits on the side of the pier the mount
  reports, and the counterweight swings to the other side, so a meridian
  flip shows as the whole head turning over.
- **Alt-az** mounts are drawn as a single-arm mount with the OTA beside
  the arm.
- A dashed line runs from the front of the OTA along the direction it is
  pointing.
- The ground disc is marked N, E, S and W.

A German equatorial mount is posed from its RA (turned into an hour
angle with the local sidereal time for your site longitude), Dec and pier
side, so turning the RA axis shows even with the scope on the pole. An
alt-az mount is posed from its altitude and azimuth. Readings arrive once
a second from the status feed, and the model eases between them so a
slew looks continuous.

**Model** (in the view toolbar) picks which mount to draw. *Automatic*
draws an alt-az mount when the driver reports no pier side and a German
equatorial mount otherwise. Pick one by hand if your driver gets this
wrong. The choice is kept in the browser.

The compass round the base reads N (0°), E (90°), S (180°) and W (270°),
the azimuths the Az column uses.

With no mount connected the model rests in its home pose: a German
equatorial mount pointing at the pole, an alt-az mount level and facing
north.

## Turning the view

- Drag to turn the view around the mount.
- Scroll, or pinch on a touch screen, to zoom.
- **Reset view**, or a double click, goes back to the starting view
  (from the south-east in the northern hemisphere, from the north-west
  in the southern).

In night mode the model is drawn in red like the rest of the page.

## Controls

The mount controls are the floating Mount control, the same window as on
SKY, FOCUS, PREVIEW and LIVE: drag it by its title bar, close it with ×,
bring it back with **Mount** in the view toolbar (shift+click or
right-click there puts it back in its starting place).

- **N / S / E / W**: press and hold to move, release to stop that axis.
  **STOP** aborts any motion. The pad is replaced by a single STOP when
  the driver has no manual jog.
- **Slew rate**: the driver's rate steps, shown when it has more than
  one.
- **Start Track / Stop Track**, **Park**, **Unpark**.
- **Horizon point**: adds a custom-horizon point at the scope's current
  altitude and azimuth.

The readout shows RA, Dec, Alt, Az, tracking, pier side (equatorial
mounts only) and whether the mount is going home, parking, slewing or
parked.

**Go-To Home**, in the view toolbar, sends the mount back to where it
was when it was turned on (counterweight down, pointing at the pole),
with tracking off. A driver with a home property (INDI `TELESCOPE_HOME`,
ASCOM Find Home) runs its own home. A driver without one that can park
at a default position, such as EQMod, parks there, which is its power-on
position. Your own park position is then put back, and the mount is
unparked. It is shown only when the driver supports one of the two, and
reads "Going home..." until the mount gets there. STOP in the Mount
control cancels the move.

## Night sky background

With **Background** set to **Night sky** (the default), the model stands
at the bottom of a live view of the sky from your site: the stars to
magnitude 6, coloured by temperature, the constellation figures, grass
up to your custom horizon, and the horizon marked every 30° with N (0°),
E (90°), S (180°) and W (270°). A ruler down the left edge gives the
altitude along the middle of the view. The sky turns with the time.

You look at the sky as an observer standing close behind the mount,
with your eye level with the mount head. The mount stands on the grass
with its head on the horizon and its legs running off the bottom of the
view, and the sky and the mount are seen through the same
lens. That is what keeps the OTA and its dashed line in one straight
line to the reticle, wherever you look.

The starting view looks toward the visible pole and is as far out as the
view zooms: from a strip of grass at the bottom up to 110° on the
ruler. That is 20° past the zenith, so the zenith stays
clear of the toolbar at the top.

- Drag to look around; the sky follows the pointer. You can't pan below
  the starting strip of grass or above 110°.
- Scroll, or pinch, to zoom in on the point under the pointer (or
  between your fingers). The mount is magnified with the sky, so
  zooming in on it shows the RA and Dec axes close up. Zoomed in and
  looking up, the OTA is still in view while it points up there too.
- **Reset view** (or a double click) goes back to the starting view.
- While a mount is connected, a dashed line runs on from the OTA to a
  reticle where the scope points on the sky.
- **Grid** cycles the coordinate grid, as on the SKY tab: off, then
  **Alt-Az** (altitude every 10°, azimuth every 15°), then
  **Equatorial** (Dec every 10°, RA every hour, labelled on the
  celestial equator; it turns with the sky). The choice is kept in the
  browser.
- The current focus stars are ringed and named. Tap or click one to
  select it in the list; the selected star is ringed in orange.

Set **Background** to **None** for the model alone, as before.

## Focus stars

Below the model, **Focus stars** lists the bright stars best placed to
focus on right now, best first, much like the TPPA start targets on the
POLAR tab. The list comes from the Hipparcos stars the SKY map ships (all
stars to magnitude 4).

Two limits pick which stars show; both are remembered in your browser:

- **Magnitude limit** (slider, default 2.0): only stars of this magnitude
  or brighter. Lower numbers are brighter: Sirius is -1.5, the stars of
  the Southern Cross about 1 to 3.
- **Min altitude** (default 20°): the lowest a star may be, now and for
  the next 20 minutes. A star must also be clear of your custom horizon.

The stars that pass are ranked by:

- **Brightness**: brighter first.
- **Altitude**: higher is better, up to about 80°.
- **Meridian**: a star that crosses the meridian in the next 20 minutes
  is marked **flip**, because a German equatorial mount may flip while
  you focus.
- **Slew**: how far the mount has to move from where it points now.

Click the **Mag** or **Alt** header to sort the table by that column,
largest to smallest (▼). Click again for smallest to largest (▲), and a
third time to go back to the ranked order.

Each row shows the star's name, magnitude, RA and Dec (J2000), altitude,
azimuth and slew distance. Click a row (or tap the star on the sky) to
select it. The action bar under the list, which stays in view while you
scroll, acts on the selected star:

- **Center** turns the sky view to it.
- **Slew** is a plain GoTo to it.
- **Solve & Sync** needs no star: if the star is not in the field after
  the slew, it takes a frame, plate solves it and syncs the mount,
  without moving it (the same as on the SKY tab). Slew again afterwards
  to put the star in the middle.

**Refresh** recomputes the list; it also refreshes on its own
when you open the page and the list is more than 5 minutes old.
