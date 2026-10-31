# Sir Correctifs Pulsar

A client-side plugin for Space Engineers, loaded by Pulsar. Part of Sirius Immersion.

On the player's machine, the game simulates the ship he pilots itself, so
that it answers at once. It does not do so for a wheeled vehicle, nor for a
grid that carries a rotor, a piston or a hinge: every command then waits for
the round trip to the server. Far from the server (300 ms and more), those
vehicles become very hard to drive.

The game knows how to predict them: its two switches exist, off by default,
and shown only in its debug screens ("Prediction" in the wheels screen,
"Client physics constraints" in the network screen). Sir Correctifs Pulsar
turns them on, on the player's machine only.

- **Wheeled vehicle prediction** (setting 1): the vehicle you drive and each
  of its wheels are simulated on your machine, with the snapshot profiles the
  game has for a predicted car and a predicted wheel. The game's safeguards
  apply to it as to any predicted ship.
- **Mechanical blocks prediction** (setting 2): client physics constraints are
  on, and the game no longer refuses the prediction to a piloted grid because
  it carries grids attached by rotors, advanced rotors, hinges or pistons.
  The attached grids follow the piloted grid, as the game does for any
  predicted grid.

Also:

- Nothing changes on the server. The only thing the plugin sends to it is its
  own error reports, and only on Sirius servers (see "Error reports"). A
  player without the plugin plays as usual, on the same server, with the same
  vehicles.
- The plugin only acts in a multiplayer game joined as a client. In a single
  player game, or in a game you host, the game is left as it is.
- The game's safeguards stay in place, for rovers too: the server can still
  refuse the prediction, the game still stops predicting a grid that keeps
  hitting moving grids (its predicted contacts counter, in the network
  screen), and its snapshot corrections and resets are untouched.
- With both settings off, the game is exactly the unmodified game.

## Usage

- **Settings**: Pulsar, Sir Correctifs Pulsar, settings button. Two
  checkboxes, **Wheeled vehicle prediction** and **Mechanical blocks
  prediction**, both checked on a new installation. Each one is turned on or
  off separately, and applies at once, without restarting the game.
- **Saved settings** in
  `%AppData%\SpaceEngineers\Storage\sir-correctifs-pulsar\settings.xml`, kept
  from one game to the next. A setting missing from the file is on.

What each combination does:

| Piloted grid | Setting 1 | Setting 2 | Predicted on your machine |
|---|---|---|---|
| Ship without attached grids | any | any | yes (the game does it already) |
| Rover (wheels only) | on | any | yes, car and wheel profiles, with the game's safeguards |
| Rover (wheels only) | off | any | no, as in the game |
| Ship with a rotor, piston or hinge | any | on | yes, attached grids with it |
| Ship with a rotor, piston or hinge | any | off | no, as in the game |
| Rover with a turret on a rotor | on | on | yes, car and wheel profiles |
| Rover with a turret on a rotor | off | any | no, as in the game |
| Rover with a turret on a rotor | any | off | no, as in the game |
| Docked to another ship by a connector | any | any | no, as in the game |

## How it works

Both settings work in `MyCubeGrid.UpdatePredictionFlag`, where the game
decides whether a grid is predicted. Its main branch predicts a piloted grid
that carries no other grid, with its safeguards: not anchored, no locked
connector, not too many predicted contacts with moving grids.

**Setting 1.** The plugin turns on the game's switch
`MyFakes.MULTIPLAYER_CLIENT_SIMULATE_CONTROLLED_CAR`. With it, the game
predicts a piloted grid whose attached grids are all wheels on suspensions,
but in a branch of its own that skips those safeguards. So the plugin also
answers "no attached grid" for such a rover (see below): the game's main
branch decides, safeguards included, and the wheels and the vehicle get the
game's wheel and car profiles (`IsClientPredictedWheel`,
`IsClientPredictedCar`), as with the game's own wheel prediction.

**Setting 2.** The plugin turns on the game's switch
`MyFakes.MULTIPLAYER_CLIENT_CONSTRAINTS`, so the joints between grids are
simulated on the player's machine. When it is turned on in a game, joints the
game had disabled while it was off are enabled; when it is turned off, the
game's own `UpdateConstraintsForceDisable` disables them again. Like the
game, only joints in the physics world are touched.

Then a Harmony transpiler on `MyCubeGrid.UpdatePredictionFlag` replaces the
single question that refuses the prediction,
`MyGridPhysicalHierarchy.HasChildren`. A piloted grid whose attached grids,
at any depth, are all wheels (setting 1 on) or attached by rotors, hinges or
pistons (setting 2 on) is answered "no attached grid". Everything
else is the game's own decision: anchored to a station, locked connector,
too many predicted contacts, prediction disabled by the server or by the
game's own safeguards. An attached grid docked elsewhere by a connector keeps
the game's refusal.

**Attached grids follow.** The game gives an attached grid the prediction of
its root, but only recomputes it on rare events. A Harmony postfix on the
same method checks, for the piloted grid, that every attached grid has the
prediction of its root; when one differs, the attached grids are recomputed
with the game's own method, on the main thread. Leaving the cockpit, or a
safeguard of the game, ends the prediction of the whole construction the
same way.

**Switching.** The values the game had are read when the plugin turns its
switches on, and given back when it turns them off: when a setting is turned
off, when leaving a multiplayer game, when the plugin stops. Every change
recomputes the prediction of every grid with the game's own method.

## Stops and coexistence

Every internal name of the game is resolved by reflection at startup, and the
patched method must ask its question exactly once (its compiled body is read
before patching, `MethodCalls`). A missing name, an
unexpected method, or a fault while the game decides a prediction stops the
plugin for the whole session (`SessionStop`): the game gets its own switches
and behavior back, a `[sir-correctifs-pulsar] ERROR` line goes to the game log
(and becomes an error report, see below), and the player gets a notification
as soon as a game is open.

Two plugins never fight over the same step. Before hooking in, then every ten
seconds, the plugin looks at who is hooked on `MyCubeGrid.UpdatePredictionFlag`
(`Harmony.GetPatchInfo`). If it finds another owner, it steps aside and tells
the player.

## Error reports

When the plugin fails, the studio hears about it, without anything about the
player. This follows the studio's rule for client plugins ("error reports").

- **Errors.** Every exception the plugin catches and every error line it
  writes to the game log (`[sir-correctifs-pulsar] ERROR ...`) gives an
  `erreur` report, a patch whose method is missing from the game included.
  The same error (same message, same top of the stack) leaves once per game
  session, with its count and its first and last times: when it has been
  quiet for 30 seconds, or at the latest 5 minutes after it first appeared.
  Whatever still waits when the game closes is kept for later.
- **Crashes.** While the game runs, a mark sits in the plugin's folder
  (`session.marker`); a clean exit erases it. Found at the next start, it
  means the previous session crashed: the plugin reads that session's game
  log again (`%AppData%\SpaceEngineers\SpaceEngineers_*.log`) and makes a
  `plantage` report with the game's CRASH INFO block, the exception that
  brought the game down and its stack, the game's last 20 error lines before
  the crash and the plugin's last 20 lines.
- **Nothing about the player.** Every line is cleaned before it is kept: a
  line that carries a Steam id, a player name, a grid name or a server
  address is removed, and the Windows user folder becomes `%USERPROFILE%`.
  Besides fixed patterns (long numbers, addresses, "Player entered:",
  "Name: ..."), the plugin learns the values to hide from the game (the
  player's name and Steam id, the world and server names) and from the log
  itself, where the game writes them in fixed lines.
- **Only on our servers.** Reports wait in
  `%AppData%\SpaceEngineers\Storage\sir-correctifs-pulsar\reports`, at most
  100 of them, one JSON file each. They leave, oldest first, only on PU or
  PTU, which the plugin recognizes on its own, without asking the server: a
  dedicated server joined as a client whose world loads the studio's own
  mods (once the game is loaded, one of their blocks or items is among the
  game's definitions: the "Contrats SIRIUS" block, the mission blocks, the
  reinforcement beacons, and so on; the list is `OurServer.Markers`). On any
  other server, in single player or in a hosted game, nothing leaves;
  reports wait for the next connection to PU or PTU.
- **Never a fault for the game.** Writing, reading or sending a report never
  throws: a failure is swallowed, the report is kept or dropped, the game
  goes on. After three failed sends in a row, the queue waits for the next
  connection.

What is sent, by `MyAPIGateway.Multiplayer.SendMessageToServer`, reliable: a
UTF-8 JSON object, without a byte order mark:

    {"greffon":"Sir Correctifs Pulsar","version":"1.1.0.0","genre":"erreur",
     "message":"...","pile":"   at ...\n   at ...",
     "premiere":"2026-10-28T09:00:00.000Z","derniere":"2026-10-28T09:00:10.000Z",
     "nombre":3,"session":"2026-10-28T08:55:12.345Z","lignes":["..."]}

`genre` is `erreur` or `plantage`, times are UTC, `session` is when the game
session concerned started, `version` is the assembly's, `lignes` are game log
lines. The rule names the channel 5465479001, but the game's network channels
are 16 bits wide: reports travel on network channel **46479**, the one the
studio's server plugin (Sir Studio Torch) listens to on PU and PTU. The plugin
only sends on it; it listens to nothing.

## Trial before a release

Before any release, a trial on a local server with about 300 ms of simulated
network delay: drive a rover, then a ship with a rotor, with and without each
setting, and note the corrections the game counts itself. A setting that does
worse than without it ships off by default, and the trial report says so.

## Name shown in Pulsar

Pulsar reads the displayed name from the plugin's sheet in the Sirius catalog,
not from this repository. The studio copies
`PluginHub/sir-correctifs-pulsar.xml` there at every release, with
`FriendlyName` set to "Sir Correctifs Pulsar" and the published commit in
`Commit`. `SourceDirectories` limits the compilation to the `Source` folder.

## Build and test

    dotnet build sir-correctifs-pulsar.csproj
    dotnet test tests/tests.csproj

The build looks for the game in the `Bin64` property, then in the `SE_BIN64`
environment variable, then in the most common Steam libraries. For another
location, see `Directory.Build.props.example`.

The tests cover the pure logic (`Source/Logic`): the settings and their file,
the rule for attached grids, the switching of the game's switches, the
reading of a compiled method body, session stops and coexistence, and the
error reports (their JSON, the count per session, the crash report read from
a game log, the cleaning of player data, the queue and the server gate).
`tests/Data` holds a game log in the exact shape the game writes it, with a
GPU crash. They need neither the game nor Pulsar.

This repository evolves through tickets in the sir-studio3 studio: `main`
carries the work accepted in qualification, `qualification` the work still
waiting for it. Every ticket that changes what the plugin does keeps this
README up to date.
