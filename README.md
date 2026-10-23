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
  game has for a predicted car and a predicted wheel.
- **Mechanical blocks prediction** (setting 2): client physics constraints are
  on, and the game no longer refuses the prediction to a piloted grid because
  it carries grids attached by rotors, advanced rotors, hinges or pistons.
  The attached grids follow the piloted grid, as the game does for any
  predicted grid.

Also:

- Nothing changes on the server, and nothing is sent to it. A player without
  the plugin plays as usual, on the same server, with the same vehicles.
- The plugin only acts in a multiplayer game joined as a client. In a single
  player game, or in a game you host, the game is left as it is.
- The game's safeguards stay in place: the server can still refuse the
  prediction, the game still stops predicting a grid when the server keeps
  correcting its contacts, and its snapshot corrections and resets are
  untouched.
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
| Rover (wheels only) | on | any | yes, car and wheel profiles |
| Rover (wheels only) | off | any | no, as in the game |
| Ship with a rotor, piston or hinge | any | on | yes, attached grids with it |
| Ship with a rotor, piston or hinge | any | off | no, as in the game |
| Rover with a turret on a rotor | on | on | yes, car and wheel profiles |
| Rover with a turret on a rotor | off | any | no, as in the game |
| Rover with a turret on a rotor | any | off | no, as in the game |
| Docked to another ship by a connector | any | any | no, as in the game |

## How it works

**Setting 1.** The plugin turns on the game's switch
`MyFakes.MULTIPLAYER_CLIENT_SIMULATE_CONTROLLED_CAR`. The game then predicts
a piloted grid whose attached grids are all wheels on suspensions, in
`MyCubeGrid.UpdatePredictionFlag`.

**Setting 2.** The plugin turns on the game's switch
`MyFakes.MULTIPLAYER_CLIENT_CONSTRAINTS`, so the joints between grids are
simulated on the player's machine. When it is turned on in a game, joints the
game had disabled while it was off are enabled; when it is turned off, the
game's own `UpdateConstraintsForceDisable` disables them again.

Then a Harmony transpiler on `MyCubeGrid.UpdatePredictionFlag` replaces the
single question that refuses the prediction,
`MyGridPhysicalHierarchy.HasChildren`. With setting 2 on, a piloted grid whose
attached grids, at any depth, are all attached by rotors, hinges or pistons
(and wheels, with setting 1 on) is answered "no attached grid". Everything
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
and behavior back, a `[sir-correctifs-pulsar]` line goes to the game log, and
the player gets a notification as soon as a game is open.

Two plugins never fight over the same step. Before hooking in, then every ten
seconds, the plugin looks at who is hooked on `MyCubeGrid.UpdatePredictionFlag`
(`Harmony.GetPatchInfo`). If it finds another owner, it steps aside and tells
the player.

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
reading of a compiled method body, session stops and coexistence. They need neither the game nor Pulsar.

This repository evolves through tickets in the sir-studio3 studio: `main`
carries the work accepted in qualification, `qualification` the work still
waiting for it. Every ticket that changes what the plugin does keeps this
README up to date.
