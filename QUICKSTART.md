# Quickstart

## Run

```bash
dotnet run --project src/Rawr.App -c Release
```

## First use

1. `Ctrl+O` → pick a folder of RAW photos
2. `←` / `→` to scroll, `1`–`5` to rate, `P` pick / `X` reject
3. `Ctrl+C` to copy picked files to a destination folder

## CR3 decoding

Out of the box RAWR uses Windows WIC, which needs the free **Microsoft Raw
Image Extension** (Microsoft Store) to decode CR3.

For best speed, drop a `libraw.dll` (LibRaw 0.21+, x64) next to `RAWR.exe` in
`src/Rawr.App/bin/Release/net9.0-windows/`. RAWR auto-detects it.

## Standalone build

```bash
dotnet publish src/Rawr.App -c Release -r win-x64 --self-contained
```

Full key map and architecture details: `README.md`, `docs/architecture.md`.
