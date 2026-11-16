# Supine Position (SPT 4.1.x client plugin)

Hold backward (S) while prone for ~0.35s -> you roll onto your back: head stays at the camera,
body and feet appear out in front of you, camera lifted slightly. Press forward (W) to return to prone.
Y = manual toggle. All keys/offsets are in BepInEx/config/com.supine.position.cfg.

## Build
1. Install .NET SDK (any 6+; targets net472).
2. Edit `<SPT>` in SupinePosition.csproj to your SPT folder.
3. `dotnet build -c Release` -> DLL is auto-copied to BepInEx/plugins/SupinePosition/.

## Tuning (in game, edit cfg, relaunch raid)
- Body floating/clipping floor: PivotHeight
- Weapon/hands too far back or forward: ArmForwardShift
- Camera height: CameraRaise
