bl_info = {
    "name": "Pako Material Applier",
    "author": "Pako",
    "version": (0, 1, 0),
    "blender": (3, 6, 0),
    "location": "View3D > Sidebar > Pako",
    "description": "Build exported Pako OPP materials on selected meshes.",
    "category": "Import-Export",
}

import json
from pathlib import Path

import bpy
from bpy.props import PointerProperty, StringProperty
from bpy.types import Operator, Panel, PropertyGroup

IMAGE_EXTS = [".png", ".tga", ".jpg", ".jpeg", ".dds"]
_image_cache = {}
_file_cache = {}

TEXTURE_KEYS = {
    "Albedo": [
        "PM_Diffuse", "Albedo", "Albedo_Map", "AlbedoMap_1", "Diffuse",
        "BaseColor", "BaseColorTexture", "Base Color", "Base_Color", "BC",
    ],
    "Metallic": ["Metallic"],
    "Rough": ["AO_(R)Base(G)Detail", "Rough", "Roughness"],
    "Normal": ["PM_Normals", "Tangent", "Normal", "BaseNormal", "NormalMap", "NormalTexture"],
    "Emissive": ["PM_Emissive", "EmissiveMap", "Emissive"],
    "Alpha": ["Alpha", "Opacity", "OpacityMask"],
    "Packed": ["PM_SpecularMasks", "ORM", "MRA", "MRO", "SpecularMasks"],
}


class PakoImporterSettings(PropertyGroup):
    export_dir: StringProperty(
        name="Export Folder",
        subtype="DIR_PATH",
        description="Folder with Pako exported JSON materials and textures",
    )


def indexed_files(folder):
    key = str(folder)
    if key not in _file_cache:
        _file_cache[key] = [p for p in folder.iterdir() if p.is_file()] if folder.exists() else []
    return _file_cache[key]


def find_json_for_material(export_root, mat_name):
    direct = list(export_root.glob(f"**/{mat_name}.json"))
    if direct:
        return direct[0]
    mat_lower = mat_name.lower()
    for path in export_root.glob("**/*.json"):
        if path.stem.lower() == mat_lower:
            return path
    return None


def find_texture_path(export_root, raw_path):
    rel = raw_path.split(".")[0].lstrip("/\\")
    rel_path = Path(rel)
    folder = export_root / rel_path.parent
    stem = rel_path.stem.lower()

    for ext in IMAGE_EXTS:
        candidate = folder / f"{rel_path.stem}{ext}"
        if candidate.exists():
            return candidate

    for file in indexed_files(folder):
        if file.stem.lower() == stem and file.suffix.lower() in IMAGE_EXTS:
            return file

    for file in export_root.glob(f"**/{rel_path.stem}.*"):
        if file.suffix.lower() in IMAGE_EXTS:
            return file
    return None


def load_image(path, non_color=False):
    if not path:
        return None
    key = str(path.resolve()).lower()
    if key in _image_cache:
        image = _image_cache[key]
    else:
        image = bpy.data.images.load(str(path), check_existing=True)
        _image_cache[key] = image
    if non_color:
        image.colorspace_settings.name = "Non-Color"
    return image


def texture_rank(texture_type, json_key, path):
    stem = path.stem.lower()
    key = json_key.lower()
    if texture_type == "Albedo":
        score = 100
        for index, token in enumerate(["_a", "_alb", "_albedo", "_basecolor", "_bc", "_d", "_diff"]):
            if stem.endswith(token) or token in stem:
                score = min(score, index)
        for token in ["_c", "_color"]:
            if stem.endswith(token):
                score += 30
        if key == "pm_diffuse":
            score += 8
        return score
    if texture_type == "Normal":
        return 0 if any(x in stem for x in ["_n", "_nm", "normal", "tangent"]) else 20
    if texture_type == "Packed":
        return 0 if any(x in stem for x in ["orm", "mra", "mro", "pack", "mask"]) else 20
    return 0


def texture_map_from_json(export_root, json_path):
    with open(json_path, "r", encoding="utf-8") as f:
        data = json.load(f)
    candidates = {}
    for json_key, unreal_path in data.get("Textures", {}).items():
        for texture_type, keys in TEXTURE_KEYS.items():
            if json_key in keys:
                tex_path = find_texture_path(export_root, unreal_path)
                if tex_path:
                    score = texture_rank(texture_type, json_key, tex_path)
                    candidates.setdefault(texture_type, []).append((score, tex_path))
                break
    return {texture_type: sorted(items, key=lambda item: item[0])[0][1] for texture_type, items in candidates.items()}


def clear_material_nodes(mat):
    mat.use_nodes = True
    nodes = mat.node_tree.nodes
    for node in list(nodes):
        nodes.remove(node)
    output = nodes.new("ShaderNodeOutputMaterial")
    output.location = (450, 0)
    principled = nodes.new("ShaderNodeBsdfPrincipled")
    principled.location = (0, 0)
    if "IOR" in principled.inputs:
        principled.inputs["IOR"].default_value = 1.15
    mat.node_tree.links.new(principled.outputs["BSDF"], output.inputs["Surface"])
    return principled


def add_image_node(mat, image, name, x, y):
    node = mat.node_tree.nodes.new("ShaderNodeTexImage")
    node.name = name
    node.label = name
    node.location = (x, y)
    node.image = image
    return node


def setup_material(mat, textures):
    principled = clear_material_nodes(mat)
    links = mat.node_tree.links

    if "Albedo" in textures:
        img = load_image(textures["Albedo"])
        node = add_image_node(mat, img, "Base Color texture", -650, 240)
        img.alpha_mode = "NONE"
        links.new(node.outputs["Color"], principled.inputs["Base Color"])

    if "Normal" in textures:
        tex = add_image_node(mat, load_image(textures["Normal"], True), "Normal texture", -850, -220)
        normal = mat.node_tree.nodes.new("ShaderNodeNormalMap")
        normal.location = (-420, -220)
        links.new(tex.outputs["Color"], normal.inputs["Color"])
        links.new(normal.outputs["Normal"], principled.inputs["Normal"])

    if "Metallic" in textures:
        tex = add_image_node(mat, load_image(textures["Metallic"], True), "Metallic texture", -650, 20)
        ramp = mat.node_tree.nodes.new("ShaderNodeValToRGB")
        ramp.location = (-320, 20)
        links.new(tex.outputs["Color"], ramp.inputs["Fac"])
        links.new(ramp.outputs["Color"], principled.inputs["Metallic"])

    if "Rough" in textures:
        tex = add_image_node(mat, load_image(textures["Rough"], True), "Roughness texture", -650, -90)
        ramp = mat.node_tree.nodes.new("ShaderNodeValToRGB")
        ramp.location = (-320, -90)
        links.new(tex.outputs["Color"], ramp.inputs["Fac"])
        links.new(ramp.outputs["Color"], principled.inputs["Roughness"])

    if "Packed" in textures:
        tex = add_image_node(mat, load_image(textures["Packed"], True), "Packed mask texture", -650, -90)
        sep = mat.node_tree.nodes.new("ShaderNodeSeparateColor")
        sep.location = (-380, -90)
        links.new(tex.outputs["Color"], sep.inputs["Color"])
        if "Rough" not in textures:
            links.new(sep.outputs["Green"], principled.inputs["Roughness"])
        if "Metallic" not in textures:
            links.new(sep.outputs["Blue"], principled.inputs["Metallic"])

    if "Emissive" in textures and "Emission Color" in principled.inputs:
        tex = add_image_node(mat, load_image(textures["Emissive"]), "Emission texture", -650, -360)
        principled.inputs["Emission Strength"].default_value = 1.0
        links.new(tex.outputs["Color"], principled.inputs["Emission Color"])

    if "Alpha" in textures:
        img = load_image(textures["Alpha"], True)
        img.alpha_mode = "CHANNEL_PACKED"
        tex = add_image_node(mat, img, "Alpha texture", -650, -500)
        mat.blend_method = "CLIP"
        mat.show_transparent_back = True


def selected_mesh_materials():
    materials = set()
    for ob in bpy.context.selected_objects:
        if ob.type != "MESH":
            continue
        for slot in ob.material_slots:
            if slot.material:
                materials.add(slot.material)
    return materials


def apply_opp_materials(export_root):
    export_root = Path(bpy.path.abspath(str(export_root)))
    setup_count = 0
    missing = []
    for mat in selected_mesh_materials():
        json_path = find_json_for_material(export_root, mat.name)
        if not json_path:
            missing.append(mat.name)
            continue
        textures = texture_map_from_json(export_root, json_path)
        if not textures:
            missing.append(mat.name)
            continue
        setup_material(mat, textures)
        setup_count += 1
    return setup_count, missing


class PAKO_OT_apply_materials(Operator):
    bl_idname = "pako.apply_materials"
    bl_label = "Apply Pako Materials"
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        settings = context.scene.pako_importer
        try:
            count, missing = apply_opp_materials(settings.export_dir)
            suffix = f"; missing: {', '.join(sorted(missing)[:8])}" if missing else ""
            self.report({"INFO"}, f"Pako materials applied: {count}{suffix}")
            return {"FINISHED"}
        except Exception as exc:
            self.report({"ERROR"}, str(exc))
            return {"CANCELLED"}


class PAKO_PT_importer(Panel):
    bl_label = "Pako Materials"
    bl_idname = "PAKO_PT_importer"
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"
    bl_category = "Pako"

    def draw(self, context):
        layout = self.layout
        settings = context.scene.pako_importer
        layout.prop(settings, "export_dir")
        layout.operator("pako.apply_materials", icon="MATERIAL")


classes = (
    PakoImporterSettings,
    PAKO_OT_apply_materials,
    PAKO_PT_importer,
)


def register():
    for cls in classes:
        bpy.utils.register_class(cls)
    bpy.types.Scene.pako_importer = PointerProperty(type=PakoImporterSettings)


def unregister():
    if hasattr(bpy.types.Scene, "pako_importer"):
        del bpy.types.Scene.pako_importer
    for cls in reversed(classes):
        bpy.utils.unregister_class(cls)
