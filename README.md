# GS Test Harness

Checks GSServer builds for ASCOM compliance and, for each problem, tells you what is wrong, what ASCOM expects,
how to fix it and where the code to change probably is.

A run tests one server's telescope through **ASCOM ITelescopeV4**. Each server is reached one way:

- **GSServer**, through its COM driver `ASCOM.GS.Sky.Telescope` (Windows only).
- **GreenSwampAlpaca**, over Alpaca. This also checks the Alpaca HTTP protocol itself: the management API,
  transaction IDs, the JSON reply shape, error numbers versus HTTP 400, parameter casing and UDP discovery.

Every run is saved, so two versions of the same server can be compared check by check: fixed, regressed, still
failing. Runs of different servers aren't compared.

## What gets checked

| Group | Checks |
|---|---|
| Connection | Connect / Connecting / Connected, Name, Description, DriverInfo, DriverVersion, InterfaceVersion |
| Protocol | Alpaca only: management API, ClientTransactionID / ServerTransactionID, reply JSON, ErrorNumber, HTTP 400, discovery |
| Common | SupportedActions, Action, DeviceState (compared with the individual properties) |
| Capabilities | Every `Can*` property and CanMoveAxis |
| Properties | Every property: implemented when required, value in range, SiderealTime and UTCDate against this computer's clock |
| Methods | AxisRates, DestinationSideOfPier |
| Settings | Writes each setting back unchanged, tries invalid values (which must be refused), tracking rates, targets |
| Motion | Slews (sync, async, to target, alt/az), syncs, AbortSlew, MoveAxis, PulseGuide, tracking, rate offsets, SideOfPier |
| ParkHome | FindHome / AtHome, Park / Unpark / AtPark, what a parked mount must refuse, SetPark |
| Tracking | Extended: tracking on holds RA/Dec, tracking off lets RA follow sidereal time, rate offsets move at the rate set |
| Clients | Extended: one client disconnecting leaves another working, parallel reads from four clients, ten connect/disconnect cycles |
| Registration | COM only, from the registry: ASCOM Profile entry, ProgID → CLSID, server for 64- and 32-bit clients |
| Performance | Property read times (slow reads flagged) and any call that never answered |
| Disconnected | Disconnect, then NotConnected errors (only when no other client holds the device) |

**Extended checks** (off by default) add a few minutes: tracking drift and several clients at once.

**Sites.** A run can be made at the server's own site, and/or at one northern and one southern site saved on the
Run page (name, latitude, longitude, elevation). For those, SiteElevation, SiteLatitude and SiteLongitude are set
after connecting and the server's own values are put back afterwards. Each site gets its own report; runs of the same
server at different sites can be compared.

**Motion is off by default.** Turn it on only for a simulator, or for a real mount that is clear to move.
Tracking and park state are put back afterwards. SetPark (which moves the saved park position) has its own switch.

## Running it

```
dotnet run --project GS.TestHarness
```

Then open http://localhost:5207, pick the server and start a run.

### From the command line

For scripts and build pipelines, `--run` checks one device and exits:

```
GS.TestHarness --run GreenSwampAlpaca --host localhost --port 31416 --device 0 [--motion] [--md report.md]
GS.TestHarness --run GSServer
```

Other options: `--setpark`, `--no-writes`, `--no-protocol`, `--no-disconnect`, `--extended`, `--drift-seconds <s>`, `--sites server,north,south`, `--slew-timeout <s>`, `--call-timeout <s>`.

The exit code is 0 when nothing failed, 1 when there were failures or errors, and 2 for bad arguments.

## Where the fixes go

`appsettings.json` → `Codebases` says where each server's source is. Folder names are looked for beside this
repository, so `GSServer` and `GreenSwampAlpaca` cloned next to it are found automatically. Findings then name a
file and line, such as `GS.Ascom.SkyTelescope/Telescope.cs:1265`.

Reports are saved to `Documents\GS.TestHarness\Reports` (setting `ReportsFolder`), as JSON, and can be downloaded
as Markdown, ready to paste into an issue.

## Tests

```
dotnet test
```

The tests run the checks against an in-memory mount (`FakeTelescope`). A compliant one must produce no problems,
and each deliberate defect must be caught.

## License

GNU General Public License v3.0 or later: see [LICENSE](LICENSE).
