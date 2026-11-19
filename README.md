# GS Test Harness

Checks GSServer builds for ASCOM compliance and, for each problem, tells you what is wrong, what ASCOM expects,
how to fix it and where the code to change probably is.

A run tests one server's telescope through **ASCOM ITelescopeV4**. Each server is reached one way:

- **GSServer**, through its COM driver `ASCOM.GS.Sky.Telescope` (Windows only).
- **GreenSwampAlpaca**, over Alpaca. This also checks the Alpaca HTTP protocol itself: the management API,
  transaction IDs, the JSON reply shape, error numbers versus HTTP 400, parameter casing and UDP discovery.

Every run is saved. The Compare tab shows a server's latest run at each site side by side, check by check: what
differs between sites, what fails at every site, and what some sites didn't check. Its Versions view compares two runs
of the same server, by default the newest against the last different DriverVersion: what was fixed, what regressed.

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
| ConformU | Only for a ConformU debug log: reproduced / not reproduced / not checked for each of its problems, generated checks, and diagnoses (rate offsets, pulse guiding) |

**Extended checks** (off by default) add a few minutes: tracking drift and several clients at once.

**Tabs.** Across the top: **ASCOM Conformance Check** (a full check, with Run, Reports, Compare and Recommendations
under it), **ConformU Debug Log Test** (following up a ConformU log, with Run, Reports and Recommendations showing only
its runs) and **Options** (what a run may do). Under the server on each test: its source code folder, the **Mount**
(simulator or real mount), the flip angle and the timeouts. Pressing Start lists those settings to check, with
a warning on any that look wrong.

**Sites.** A run is made at the server's own site. (Northern and southern sites, where SiteLatitude, SiteLongitude and
SiteElevation are set for the run and put back afterwards, are turned off for now: `SiteStore.HemisphereSites`.)

**Motion is off by default.** Turn it on only for a simulator, or for a real mount that is clear to move.
Tracking and park state are put back afterwards. SetPark (which moves the saved park position) has its own switch.

**ConformU Debug Log Test.** Takes a ConformU *debug* log by drag and drop (a normal log is turned away). Its errors,
issues and configuration alerts are grouped by ITelescopeV4 member; **Run ConformU checks** checks only those members:
the groups that cover them, only the parts of each group that touch them, and only findings about them. It then says
for each problem whether the harness reproduced it, didn't, or couldn't check it. A property no check reached is read by
a generated check; anything else nothing here can test is flagged to reproduce by hand.

**Diagnoses** (tiers 2 and 3). For RightAscensionRate / DeclinationRate and PulseGuide problems, the deeper tiers measure *why* ConformU saw
them (a rate that is wrong, or readings too coarse for ConformU's measurement; a stale position reading, a wrong guide
rate, or time lost per pulse) and recommends where to check GSServer's code, for its simulator or a real mount as set
by **Mount**. A conformance run's other problems have tier 2 diagnoses too (`Engine/Checks/AreaDiagnostics.cs`): invalid
arguments (axes, directions, coordinates and settings out of range, and HTTP 400 from the Alpaca server), preconditions
(tracking off, during a slew), rate offsets at tracking rates other than sidereal (still applied, or only read wrong),
members answering while disconnected, SlewSettleTime, AxisRates, DriverVersion and the COM registration. Each tests
around the problem and, with the server's source set, reviews its code (guards called, lines commented out, where a
setting is used) to name the core issue and the change. The source is only reviewed when it is the server answering's:
an Alpaca server whose name (from the management API) isn't in the source gets no code review and no file names. The
harness only recommends: it never changes the server's code.

**Diagnostic tiers.** Both tests go in three tiers, and tier 1 always runs first:

| Tier | ASCOM Conformance Check | ConformU Debug Log Test |
|---|---|---|
| 1 | Initial testing to identify problems with the known ASCOM standards | Initial testing to verify the problems already found in the ConformU log |
| 2 | Further tests within the problem area, with the source code reviewed, to identify the core issue and possible fixes | The same, running or creating tests for the log's problems |
| 3 | Deep dive tests to verify tier 2's recommendations: a final analysis, summary and recommended fixes | The same, for the log's problems |

After a run, the next tier is offered under the results for the problems it found that have a deeper test, and only
those members are tested; the run is linked to the one it follows up. Every check in the catalog (`Engine/TestCatalog.cs`)
says its tier and, for tiers 2 and 3, which checks of the tier below it follows up; the tests fail if a new check
doesn't. Each check also has its own number (`Engine/TestNumbers.cs`) whose first digit is its tier: 1001 on for
tier 1, 2001 on for tier 2, 3001 on for tier 3. A new check takes the next number of its tier, and numbers are never
reused; the help's Diagnostic tiers section lists which numbers follow up which. Tier 3 ends with a Summary finding: whether it confirms or overturns tier 2, and the final recommendation.
Compare uses tier 1 runs; Recommendations add each tier 1 run's deeper findings (the deepest tier's).

**The help** (the **?** at the top right) lists every check: its objective, how it's tested, how the result is
verified, and the ASCOM documentation or ConformU code it's based on.

**If the device stops answering** part way (a COM server crash, an Alpaca server gone), the run stops with one
`CommunicationLost` error and keeps what it found so far.

## Running it

```
dotnet run --project GS.TestHarness
```

Then open http://localhost:5207, drag a server onto the Server card and start a run. The **?** at the top right opens the
help.

### From the command line

For scripts and build pipelines, `--run` checks one device and exits:

```
GS.TestHarness --run GreenSwampAlpaca --host localhost --port 31416 --device 0 [--motion] [--md report.md]
GS.TestHarness --run GSServer
```

Other options: `--setpark`, `--no-writes`, `--no-protocol`, `--no-disconnect`, `--extended`, `--drift-seconds <s>`, `--sites server,north,south`, `--slew-timeout <s>`, `--call-timeout <s>`, `--flip-angle <degrees>` (0 to 90, default 0: not known, so the SideOfPier flip isn't checked), `--mount simulator|real`, `--source <folder or GitHub URL>` (the server's source code for this run; a GitHub repository is downloaded first). `--conformu <debug log>` runs the ConformU Debug Log Test instead (only the log's problem members, with the verdicts). `--follow <report id>` runs the next tier for what that saved run found (with the same `--conformu` log for a ConformU run); there is no other way to run tier 2 or 3, and a finished run prints the `--follow` command when a deeper tier is available.

The exit code is 0 when nothing failed, 1 when there were failures or errors, and 2 for bad arguments.

## Where to check the code

Under the server on each test, set the server's **source code** folder (paste it, or pick it with the folder button:
folders holding the driver file are marked). It's each person's own setting, saved in `source-folders.json` beside the
reports folder. It may be any server's code, or a GitHub URL (downloaded and read from a copy here). Left blank, there
is no code review: findings don't name files and tier 2 says the code wasn't reviewed. Findings then name the code to
check, such as `Driver/Telescope.cs:1265`.

Reports are saved to `Documents\GS.TestHarness\Reports` (setting `ReportsFolder`), as JSON, and can be downloaded
as Markdown (ready to paste into an issue), JSON or CSV, or printed / saved as PDF from the browser.

## Tests

```
dotnet test
```

The tests run the checks against an in-memory mount (`FakeTelescope`). A compliant one must produce no problems,
and each deliberate defect must be caught.

## License

GNU General Public License v3.0 or later: see [LICENSE](LICENSE).
