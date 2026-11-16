# "TRUE" Smart Mopping

An Oxygen Not Included mod that adds two ways to deal with spilled liquid.

- **Smart Mopping**: a tool that replaces the manual area selection. Click anywhere on a puddle and the mop task is placed in the most efficient spot, mopping up the liquid from its edges. It is exactly as fast as the base-game mop command; it only saves the micromanagement.
- **Auto-Mopping**: when a duplicant comes into contact with a liquid, a Smart Mopping task is created automatically.

The Workshop description (English and Japanese) is in `Workshop/`.

## Build

Copy `Local.Build.props.example` to `Local.Build.props` and set `GameManagedPath` and `ModInstallPath`, then `dotnet build` the `.sln`. A Release build packages the Workshop folder under `release/TrueSmartMopping`.

## License

MIT (see `LICENSE`).
