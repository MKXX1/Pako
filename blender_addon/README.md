# Pako Material Applier

Blender addon that applies exported Pako OPP materials to selected Blender meshes.

## Install

1. Build Pako so `bin/Debug/net8.0-windows/Pako.exe` exists.
2. In Blender, open `Edit > Preferences > Add-ons > Install...`.
3. Select the `blender_addon/pako_blender_importer` folder or zip that folder.
4. Enable `Pako Material Applier`.

## Use

Open `View3D > Sidebar > Pako`.

- `Export Folder`: folder where Pako wrote JSON materials and textures.

After exporting a model from Pako and importing it into Blender, select the imported mesh objects and click `Apply Pako Materials`.
