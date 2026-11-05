# MarsForge

A Windows app: draw a box on the NASA Mars Trek map and get a real-size 3D Mars terrain for Blender, with rocks,
soft ground and a ground texture.

## What is what

* **`dist\MarsForge.exe`** is the finished app: one file with everything inside. Copy it anywhere, or give it to
  someone with 64-bit Windows 10/11; nothing needs to be installed. A small loading window shows while it starts.
  The first time, it unpacks itself once into `%LOCALAPPDATA%\MarsForge` (a few seconds); after that it starts
  straight away. A new build replaces the old unpacked copy.
* Everything else is the **source code** used to build it: `src` (the app), `models` (the AI models, packed into
  the `.exe` when building), `training` (Python scripts that made the rock model), `MarsForge.sln` (open in Visual Studio),
  and `build.bat` (makes a fresh `dist`).

## Use it

Run `dist\MarsForge.exe`, then:

1. **Draw box**, then drag on the map (up to 20 km per side). Scroll to zoom, drag to move the map.
2. Choose **Surface detail** and **Rock size**, and tick **Rocks** and/or **Soft ground**.
3. **Generate**. The 3D preview opens: left-drag to turn, scroll to zoom, right-drag to move.
   You can change the **Soft ground** and **Deep soft ground** colours in the preview.
4. **Download .fbx** saves a zip with the `.fbx` (terrain with its texture inside, and the rocks) and the texture
   as a `.png`. In Blender: File › Import › FBX. 1 unit = 1 m, +X = east, +Y = north, +Z = up.

5. The arrow next to **Download** switches the format to **Unity 6+ Terrain**. That zip holds a ready
   **TerrainData `<name>_Terrain.asset`** (16-bit heightmap, one piece, 2^n+1 size up to 1025) and its
   **`<name>_Layer.terrainlayer`** (the same texture as the FBX, soft ground painted in, stored inside it as DXT1
   with mipmaps), plus their `.meta` files. Copy all four into a Unity 6 project's `Assets` folder and drag the
   `.asset` into the scene. No scripts or packages. With **Rocks** ticked the zip also has `<name>_Rocks.fbx`:
   the rocks in place for the terrain at (0, 0, 0), every rock a separate object (pivot at its base).
   +X = east, +Y = up, +Z = north, 1 unit = 1 m, south-west corner at the terrain origin.

An internet connection is needed (heights, Mars Trek images and HiRISE photos are read online). To work offline,
put `Mars_HRSC_MOLA_BlendDEM_Global_200mp_v2.tif` (11 GB, from https://planetarymaps.usgs.gov/mosaic/Mars/HRSC_MOLA_Blend/)
in a `data` folder next to `MarsForge.exe`. The log (`MarsForge.log`) is in `%LOCALAPPDATA%\MarsForge\app-…\lib`.

## What you get

* **Terrain**: real heights from the USGS MOLA–HRSC map (one height every 200 m), smooth curves in between.
  *Surface detail* adds small craters and bumps that look like Mars (generated, not measured).
* **Texture**: plain, seamless Mars ground, with soft ground painted on top in your colours.
* **Rocks**: real rocks from a HiRISE photo where one covers the box (boxes up to 2 km), found with RockBoxNet.
  Everywhere else the rock model places rocks: rockier where the ground stays warm at night. This is a rough
  guide; the exact spot of each predicted rock is random. *Rock size*: All rocks (1 m and up), Medium and big
  rocks (2 m and up), Only big rocks (4 m and up).
* **Soft ground**: places likely to have loose, fine soil (like the soil that trapped the Spirit rover).
  An estimate from orbit pictures, not a measurement.

## Build it

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download). Double-click `build.bat`
It publishes the app (`src\MarsForge.App`), builds the small launcher with the loading window
(`src\MarsForge.Launcher`), and packs the app inside it: the result is the single `dist\MarsForge.exe`.

## Source

| Folder | What |
|---|---|
| `src/MarsForge.Core` | Everything that is not the window: terrain mesh, rock finder (RockBoxNet), rock model, soft ground, textures, FBX writer, Unity 6 terrain writer (TerrainData + TerrainLayer as Unity text assets). Plain .NET, no packages. |
| `src/MarsForge.App` | The Windows app (WPF): map, buttons, 3D preview (Helix Toolkit), GDAL for map data, ONNX Runtime for the AI models. |
| `src/MarsForge.Launcher` | The small `MarsForge.exe` people run: the loading window; unpacks the app packed inside it, then starts it. |
| `models` | `rockboxnet_v1.onnx` (finds rocks in HiRISE photos) and `rock_density_v1.*` (the rock model). |
| `training` | Python scripts that made the rock model (see `training/README.md`). Not needed to run the app. |

Credits: maps NASA Mars Trek; heights USGS Astrogeology; HiRISE photos University of Arizona / NASA.
