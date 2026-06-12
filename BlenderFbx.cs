using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace Pako;

public static class BlenderFbx
{
    public static string BlenderPath { get; set; } = "";

    public static bool TryConvertPskToFbx(string pskPath, string outputDirectory, out string fbxPath, out string error)
    {
        fbxPath = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(pskPath) + ".fbx");
        error = "";

        if (!File.Exists(pskPath))
        {
            error = $"PSK file was not found: {pskPath}";
            return false;
        }

        var blenderPath = FindBlenderExecutable();
        if (blenderPath == null)
        {
            error = "Blender was not found. Set Blender path in Pako, install Blender to the default folder, or add blender.exe to PATH.";
            return false;
        }

        var addonPath = Path.Combine(AppContext.BaseDirectory, "BlenderAddons", "io_import_scene_unreal_psa_psk_280");
        if (!Directory.Exists(addonPath))
        {
            error = $"Bundled PSK importer addon was not found: {addonPath}";
            return false;
        }

        Directory.CreateDirectory(outputDirectory);
        var scriptPath = Path.Combine(Path.GetTempPath(), $"pako_psk_to_fbx_{Guid.NewGuid():N}.py");
        File.WriteAllText(scriptPath, BuildScript(addonPath, pskPath, fbxPath), Encoding.UTF8);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = blenderPath,
                Arguments = $"-b --factory-startup --python \"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                error = "Could not start Blender.";
                return false;
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(120_000))
            {
                try { process.Kill(true); } catch { }
                try { process.WaitForExit(5_000); } catch { }
                error = "Blender conversion timed out.";
                return false;
            }

            var output = outputTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();
            if (process.ExitCode != 0 || !File.Exists(fbxPath))
            {
                error = CollapseBlenderOutput(output, stderr);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { }
        }
    }

    private static string? FindBlenderExecutable()
    {
        var candidates = new[]
        {
            BlenderPath,
            Environment.GetEnvironmentVariable("PAKO_BLENDER"),
            "blender.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Blender Foundation", "Blender 4.4", "blender.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Blender Foundation", "Blender 4.3", "blender.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Blender Foundation", "Blender 4.2", "blender.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Blender Foundation", "Blender 4.1", "blender.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Blender Foundation", "Blender 4.0", "blender.exe")
        };

        foreach (var candidate in candidates.Where(c => !string.IsNullOrWhiteSpace(c)))
        {
            if (candidate!.Equals("blender.exe", StringComparison.OrdinalIgnoreCase))
            {
                if (IsExecutableOnPath(candidate)) return candidate;
                continue;
            }

            if (File.Exists(candidate)) return candidate;
        }

        var foundation = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Blender Foundation");
        if (!Directory.Exists(foundation)) return null;

        return Directory.EnumerateFiles(foundation, "blender.exe", SearchOption.AllDirectories)
            .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static bool IsExecutableOnPath(string executable)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        return path.Split(Path.PathSeparator)
            .Where(Directory.Exists)
            .Select(directory => Path.Combine(directory, executable))
            .Any(File.Exists);
    }

    private static string BuildScript(string addonPath, string pskPath, string fbxPath)
    {
        return $$"""
import bpy
import importlib.util
import os
import sys
import traceback

addon_path = r'{{EscapePythonString(addonPath)}}'
psk_path = r'{{EscapePythonString(pskPath)}}'
fbx_path = r'{{EscapePythonString(fbxPath)}}'

try:
    bpy.ops.object.select_all(action='SELECT')
    bpy.ops.object.delete()

    package_name = "io_import_scene_unreal_psa_psk_280"
    parent = os.path.dirname(addon_path)
    if parent not in sys.path:
        sys.path.insert(0, parent)

    spec = importlib.util.spec_from_file_location(
        package_name,
        os.path.join(addon_path, "__init__.py"),
        submodule_search_locations=[addon_path])
    module = importlib.util.module_from_spec(spec)
    sys.modules[package_name] = module
    spec.loader.exec_module(module)
    module.register()

    bpy.ops.import_scene.psk(
        filepath=psk_path,
        import_mode='All',
        bSpltiUVdata=False,
        bReorientBones=False,
        bDontInvertRoot=True,
        fBonesize=5.0,
        fBonesizeRatio=0.6,
        bScaleDown=True,
        bFilenameAsPrefix=False)

    for obj in bpy.context.scene.objects:
        obj.select_set(obj.type in {'MESH', 'ARMATURE'})
    bpy.ops.export_scene.fbx(
        filepath=fbx_path,
        use_selection=True,
        add_leaf_bones=False,
        bake_anim=False,
        object_types={'ARMATURE', 'MESH'},
        path_mode='AUTO')
except Exception:
    traceback.print_exc()
    raise
""";
    }

    private static string EscapePythonString(string value)
    {
        return value.Replace("\\", "\\\\").Replace("'", "\\'");
    }

    private static string CollapseBlenderOutput(string output, string stderr)
    {
        var combined = (output + Environment.NewLine + stderr).Trim();
        if (string.IsNullOrWhiteSpace(combined))
            return "Blender failed to convert PSK to FBX.";

        var lines = combined.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .TakeLast(18);
        return string.Join(Environment.NewLine, lines);
    }
}
