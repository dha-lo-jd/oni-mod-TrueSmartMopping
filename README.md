# "TRUE" Smart Mopping

An Oxygen Not Included mod that adds two ways to deal with spilled liquid.

- **Smart Mopping**: a tool that replaces the manual area selection. Click anywhere on a puddle and the mop task is placed in the most efficient spot, mopping up the liquid from its edges. Each duplicant mops at the base-game speed (no boost); the order mops thick parts first, then dries the rest from the edge so no film is left, and up to two more duplicants can join and take turns from one end, the fastest pattern of the base-game command.
- **Auto-Mopping**: when a duplicant comes into contact with a liquid, a Smart Mopping task is created automatically.

The Workshop description (English and Japanese) is in `Workshop/`.

Version 1.1.0 works with both the game update U60 and U59 (game build 744825 or later).

## Build

Copy `Local.Build.props.example` to `Local.Build.props` and set `GameManagedPath` and `ModInstallPath`, then `dotnet build` the `.sln`. A Release build packages the Workshop folder under `release/TrueSmartMopping`.

## License

MIT (see `LICENSE`).
