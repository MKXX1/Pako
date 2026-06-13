using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using CUE4Parse.Compression;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Nanite;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
using CUE4Parse.Utils;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Animations;
using CUE4Parse_Conversion.Meshes;
using CUE4Parse_Conversion.Meshes.PSK;
using CUE4Parse_Conversion.Textures;
using CUE4Parse_Conversion.Textures.BC;
using CUE4Parse_Conversion.UEFormat.Enums;

namespace Pako;

public sealed class OPP_PakMount
{
    private const string AesKey = "0x613E92E0F3CE880FC652EC86254E2581126AE86D63BA46550FB2CE0EC2EDA439";
    private static readonly ModelScanRoot[] ModelScanRoots =
    [
        new("OPP/Content/Characters/", ModelCategory.Characters),
        new("OPP/Content/Props/", ModelCategory.Props)
    ];

    private readonly VersionContainer _version = new(EGame.GAME_OutlastTrials, ETexturePlatform.DesktopMobile);
    private DefaultFileProvider? _provider;
    private bool _detexInitialized;
    private string _modelIndexCacheKey = "";

    public IReadOnlyList<ModelItem> Models => _models;
    public string Status { get; private set; } = "Game is not mounted";
    public bool IsMounted => _provider != null;
    public bool IsBusy { get; private set; }
    public string GameDirectory { get; private set; } = "";
    public int ScanCompleted { get; private set; }
    public int ScanTotal { get; private set; }

    private readonly List<ModelItem> _models = new();

    public string? GetPaksPath(string selectedPath)
    {
        if (string.IsNullOrWhiteSpace(selectedPath)) return null;

        var fullPath = Path.GetFullPath(selectedPath);
        if (File.Exists(fullPath)) fullPath = Path.GetDirectoryName(fullPath) ?? fullPath;

        var current = fullPath;
        while (!string.IsNullOrEmpty(current))
        {
            var direct = Path.Combine(current, "OPP", "Content", "Paks");
            if (IsValidPaksPath(direct)) return direct;

            if (Path.GetFileName(current).Equals("Paks", StringComparison.OrdinalIgnoreCase) && IsValidPaksPath(current))
                return current;

            var parent = Path.GetDirectoryName(current);
            if (parent == current) break;
            current = parent ?? "";
        }

        return null;
    }

    public bool Mount(string selectedPath, out string error)
    {
        error = "";
        var paksPath = GetPaksPath(selectedPath);
        if (paksPath == null)
        {
            error = "Select the game folder or OPP/Content/Paks folder.";
            return false;
        }

        try
        {
            var oodlePath = Path.Combine(AppContext.BaseDirectory, "utils", "oo2core_9_win64.dll");
            if (!File.Exists(oodlePath))
                throw new FileNotFoundException("oo2core_9_win64.dll was not found.", oodlePath);
            OodleHelper.Initialize(oodlePath);
            EnsureDetexInitialized();

            var provider = new DefaultFileProvider(paksPath, SearchOption.TopDirectoryOnly, true, _version);
            provider.Initialize();
            provider.SubmitKey(new FGuid(), new FAesKey(AesKey));
            provider.PostMount();

            _provider = provider;
            GameDirectory = paksPath;
            _modelIndexCacheKey = BuildModelIndexCacheKey(paksPath);
            _models.Clear();
            Status = $"Mounted: {paksPath}";
            return true;
        }
        catch (Exception ex)
        {
            _provider = null;
            error = ex.Message;
            Status = "Mount failed";
            return false;
        }
    }

    public void BuildModelIndex(CancellationToken token = default)
    {
        if (_provider == null) return;

        IsBusy = true;
        _models.Clear();
        ScanCompleted = 0;
        ScanTotal = 0;
        Status = "Scanning packages for meshes...";

        try
        {
            if (TryLoadModelIndexCache(out var cachedModels))
            {
                _models.AddRange(cachedModels);
                ScanCompleted = 1;
                ScanTotal = 1;
                Status = $"Models loaded from cache: {_models.Count}";
                return;
            }

            var files = _provider.Files.Values
                .Select(f => new ScannableFile(f, GetModelCategory(f.Path)))
                .Where(f => f.Category != ModelCategory.Other && f.File.Path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var found = new List<ModelItem>();
            var scanned = 0;
            ScanTotal = files.Length;
            Status = $"Scanning model packages... 0/{files.Length}";

            foreach (var scanFile in files)
            {
                token.ThrowIfCancellationRequested();
                scanned++;
                ScanCompleted = scanned;
                if (scanned % 250 == 0)
                    Status = $"Scanning packages... {scanned}/{files.Length}, models: {found.Count}";

                var file = scanFile.File;
                if (!_provider.TryLoadPackage(file, out var package)) continue;

                foreach (var export in package.GetExports())
                {
                    var kind = export switch
                    {
                        UStaticMesh => ModelKind.StaticMesh,
                        USkeletalMesh => ModelKind.SkeletalMesh,
                        _ => (ModelKind?)null
                    };

                    if (kind == null) continue;

                    var objectName = export.Name;
                    if (string.IsNullOrWhiteSpace(objectName))
                        objectName = Path.GetFileNameWithoutExtension(file.Name);

                    var packageNoExt = file.Path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)
                        ? file.Path[..^7]
                        : file.Path;

                    found.Add(new ModelItem
                    {
                        DisplayName = objectName,
                        PackagePath = file.Path,
                        ObjectPath = $"{packageNoExt}.{objectName}",
                        Category = scanFile.Category,
                        Folder = GetDisplayFolder(packageNoExt, scanFile.Category),
                        Kind = kind.Value
                    });
                }
            }

            _models.AddRange(found
                .DistinctBy(m => m.ObjectPath)
                .OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase));

            SaveModelIndexCache(_models);
            Status = $"Models loaded: {_models.Count}";
        }
        catch (OperationCanceledException)
        {
            Status = $"Scan canceled. Models loaded: {_models.Count}";
        }
        catch (Exception ex)
        {
            Status = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            ScanCompleted = ScanTotal;
        }
    }

    public bool ExportModel(ModelItem model, string exportDirectory, PakoMeshExportFormat format, out string savedFilePath, out string error)
    {
        savedFilePath = "";
        error = "";

        if (_provider == null)
        {
            error = "Game is not mounted.";
            return false;
        }

        try
        {
            EnsureDetexInitialized();

            if (format == PakoMeshExportFormat.Fbx)
                return ExportModelAsFbx(model, exportDirectory, out savedFilePath, out error);

            if (!_provider.TryLoadPackageObject(model.ObjectPath, out UObject export))
            {
                error = $"Could not load {model.ObjectPath}";
                return false;
            }

            var options = new ExporterOptions
            {
                LodFormat = ELodFormat.FirstLod,
                MeshFormat = format == PakoMeshExportFormat.ActorX ? EMeshFormat.ActorX : EMeshFormat.Gltf2,
                NaniteMeshFormat = ENaniteMeshFormat.OnlyNaniteLOD,
                AnimFormat = EAnimFormat.ActorX,
                MaterialFormat = EMaterialFormat.AllLayers,
                TextureFormat = ETextureFormat.Png,
                CompressionFormat = EFileCompressionFormat.None,
                Platform = _version.Platform,
                SocketFormat = ESocketFormat.None,
                ExportMorphTargets = true,
                ExportMaterials = true,
                ExportHdrTexturesAsHdr = true
            };

            var exporter = new Exporter(export, options);
            if (!exporter.TryWriteToDir(new DirectoryInfo(exportDirectory), out _, out savedFilePath))
            {
                error = $"Exporter returned no file for {model.DisplayName}.";
                return false;
            }

            return File.Exists(savedFilePath);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private bool ExportModelAsFbx(ModelItem model, string exportDirectory, out string savedFilePath, out string error)
    {
        savedFilePath = "";
        error = "";

        var intermediateDirectory = Path.Combine(exportDirectory, "_psk_intermediate");
        Directory.CreateDirectory(intermediateDirectory);

        if (!ExportModel(model, intermediateDirectory, PakoMeshExportFormat.ActorX, out var pskPath, out error))
            return false;

        var pskFile = ResolveActorXMeshFile(pskPath);
        if (pskFile == null)
        {
            error = $"ActorX export succeeded, but no .psk/.pskx file was found near {pskPath}";
            return false;
        }

        if (!BlenderFbx.TryConvertPskToFbx(pskFile, exportDirectory, out savedFilePath, out error))
            return false;

        return File.Exists(savedFilePath);
    }

    private static string? ResolveActorXMeshFile(string savedFilePath)
    {
        if (File.Exists(savedFilePath) &&
            (savedFilePath.EndsWith(".psk", StringComparison.OrdinalIgnoreCase) ||
             savedFilePath.EndsWith(".pskx", StringComparison.OrdinalIgnoreCase)))
            return savedFilePath;

        var directory = File.Exists(savedFilePath)
            ? Path.GetDirectoryName(savedFilePath)
            : Directory.Exists(savedFilePath)
                ? savedFilePath
                : Path.GetDirectoryName(savedFilePath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return null;

        return Directory.EnumerateFiles(directory, "*.psk*", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault(path =>
                path.EndsWith(".psk", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".pskx", StringComparison.OrdinalIgnoreCase));
    }

    public byte[]? CreateThumbnailRgba(ModelItem model, int size, out string error)
    {
        error = "";

        if (_provider == null)
        {
            error = "Game is not mounted.";
            return null;
        }

        try
        {
            EnsureDetexInitialized();

            if (!_provider.TryLoadPackageObject(model.ObjectPath, out UObject export))
            {
                error = $"Could not load {model.ObjectPath}";
                return null;
            }

            switch (export)
            {
                case UStaticMesh staticMesh when staticMesh.TryConvert(out CStaticMesh converted, ENaniteMeshFormat.OnlyNormalLODs):
                    return RenderAndDispose(converted, size, model);
                case USkeletalMesh skeletalMesh when skeletalMesh.TryConvert(out CSkeletalMesh converted):
                    return RenderAndDispose(converted, size, model);
                case UStaticMesh:
                    error = "Static mesh conversion returned no renderable LOD.";
                    return null;
                case USkeletalMesh:
                    error = "Skeletal mesh conversion returned no renderable LOD.";
                    return null;
                default:
                    error = $"Unsupported export type: {export.GetType().Name}";
                    return null;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    public ModelClass? CreatePreviewScene(ModelItem model, out string error)
    {
        error = "";

        if (_provider == null)
        {
            error = "Game is not mounted.";
            return null;
        }

        try
        {
            if (!_provider.TryLoadPackageObject(model.ObjectPath, out UObject export))
            {
                error = $"Could not load {model.ObjectPath}";
                return null;
            }

            switch (export)
            {
                case UStaticMesh staticMesh when staticMesh.TryConvert(out CStaticMesh converted, ENaniteMeshFormat.OnlyNormalLODs):
                    return CreatePreviewSceneAndDispose(converted, model, out error);
                case USkeletalMesh skeletalMesh when skeletalMesh.TryConvert(out CSkeletalMesh converted):
                    return CreatePreviewSceneAndDispose(converted, model, out error);
                case UStaticMesh:
                    error = "Static mesh conversion returned no renderable LOD.";
                    return null;
                case USkeletalMesh:
                    error = "Skeletal mesh conversion returned no renderable LOD.";
                    return null;
                default:
                    error = $"Unsupported export type: {export.GetType().Name}";
                    return null;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private static bool IsValidPaksPath(string path)
    {
        return Directory.Exists(path) &&
               Path.GetFileName(path).Equals("Paks", StringComparison.OrdinalIgnoreCase) &&
               Directory.EnumerateFiles(path, "*.pak", SearchOption.TopDirectoryOnly).Any();
    }

    private static ModelCategory GetModelCategory(string path)
    {
        foreach (var root in ModelScanRoots)
        {
            if (path.StartsWith(root.Path, StringComparison.OrdinalIgnoreCase))
                return root.Category;
        }

        return ModelCategory.Other;
    }

    private static string GetDisplayFolder(string packageNoExt, ModelCategory category)
    {
        var root = ModelScanRoots.FirstOrDefault(r => r.Category == category).Path;
        var folder = packageNoExt.Contains('/') ? packageNoExt[..packageNoExt.LastIndexOf('/')] : packageNoExt;
        if (!string.IsNullOrEmpty(root) && folder.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            folder = folder[root.Length..];

        return string.IsNullOrWhiteSpace(folder) ? CategoryLabel(category) : folder;
    }

    private static string CategoryLabel(ModelCategory category)
    {
        return category switch
        {
            ModelCategory.Characters => "Characters",
            ModelCategory.Props => "Props",
            _ => "Other"
        };
    }

    private bool TryLoadModelIndexCache(out List<ModelItem> models)
    {
        models = new List<ModelItem>();
        if (string.IsNullOrWhiteSpace(_modelIndexCacheKey)) return false;

        try
        {
            var path = GetModelIndexCachePath();
            if (!File.Exists(path)) return false;

            var cache = JsonSerializer.Deserialize<ModelIndexCache>(File.ReadAllText(path));
            if (cache == null || cache.Version != 1 || !string.Equals(cache.Key, _modelIndexCacheKey, StringComparison.Ordinal))
                return false;

            models = cache.Models
                .DistinctBy(m => m.ObjectPath)
                .OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return models.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private void SaveModelIndexCache(IReadOnlyList<ModelItem> models)
    {
        if (string.IsNullOrWhiteSpace(_modelIndexCacheKey) || models.Count == 0) return;

        try
        {
            var cache = new ModelIndexCache(1, _modelIndexCacheKey, models.ToArray());
            File.WriteAllText(GetModelIndexCachePath(), JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = false }));
        }
        catch
        {
            // A cache failure should not prevent the browser from working.
        }
    }

    private static string GetModelIndexCachePath()
    {
        return Path.Combine(AppContext.BaseDirectory, "model-index-cache.json");
    }

    private static string BuildModelIndexCacheKey(string paksPath)
    {
        var containers = Directory.EnumerateFiles(paksPath, "*.*", SearchOption.TopDirectoryOnly)
            .Where(path =>
                path.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".ucas", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".sig", StringComparison.OrdinalIgnoreCase))
            .Select(path =>
            {
                var info = new FileInfo(path);
                return $"{info.Name}:{info.Length}:{info.LastWriteTimeUtc.Ticks}";
            })
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase);

        return Path.GetFullPath(paksPath).ToUpperInvariant() + "|" + string.Join("|", containers);
    }

    private void EnsureDetexInitialized()
    {
        if (_detexInitialized) return;

        var dataDirectory = Path.Combine(AppContext.BaseDirectory, ".data");
        Directory.CreateDirectory(dataDirectory);

        var detexPath = Path.Combine(dataDirectory, DetexHelper.DLL_NAME);
        if (!DetexHelper.LoadDll(detexPath))
            throw new InvalidOperationException("Detex.dll could not be loaded from CUE4Parse resources.");

        DetexHelper.Initialize(detexPath);
        _detexInitialized = true;
    }

    private static byte[]? RenderAndDispose(CStaticMesh mesh, int size, ModelItem model)
    {
        try
        {
            foreach (var lod in mesh.LODs)
            {
                if (lod.Verts == null || lod.Verts.Length == 0) continue;
                var indices = lod.Indices == null ? Array.Empty<uint>() : ReadIndices(lod.Indices.Value);
                return RenderMeshThumbnail(lod.Verts, indices, size, model);
            }
        }
        finally
        {
            (mesh as IDisposable)?.Dispose();
        }

        return null;
    }

    private static ModelClass? CreatePreviewSceneAndDispose(CStaticMesh mesh, ModelItem model, out string error)
    {
        try
        {
            foreach (var lod in mesh.LODs)
            {
                if (lod.Verts == null || lod.Verts.Length == 0) continue;
                var indices = lod.Indices == null ? Array.Empty<uint>() : ReadIndices(lod.Indices.Value).ToArray();
                var sections = lod.Sections?.Value ?? Array.Empty<CMeshSection>();
                return BuildPreviewScene(model, lod.Verts, indices, sections, out error);
            }
        }
        finally
        {
            (mesh as IDisposable)?.Dispose();
        }

        error = "No renderable static mesh LOD was found.";
        return null;
    }

    private static ModelClass? CreatePreviewSceneAndDispose(CSkeletalMesh mesh, ModelItem model, out string error)
    {
        try
        {
            foreach (var lod in mesh.LODs)
            {
                if (lod.Verts == null || lod.Verts.Length == 0) continue;
                var indices = lod.Indices == null ? Array.Empty<uint>() : ReadIndices(lod.Indices.Value).ToArray();
                var sections = lod.Sections?.Value ?? Array.Empty<CMeshSection>();
                return BuildPreviewScene(model, lod.Verts, indices, sections, out error);
            }
        }
        finally
        {
            (mesh as IDisposable)?.Dispose();
        }

        error = "No renderable skeletal mesh LOD was found.";
        return null;
    }

    private static ModelClass? BuildPreviewScene(ModelItem model, IReadOnlyList<CMeshVertex> vertices, uint[] indices, IReadOnlyList<CMeshSection> sections, out string error)
    {
        error = "";
        if (vertices.Count == 0)
        {
            error = "Mesh has no vertices.";
            return null;
        }

        if (indices.Length < 3)
            indices = Enumerable.Range(0, vertices.Count).Select(i => (uint)i).ToArray();

        var positions = new Vector3[vertices.Count];
        var normals = new Vector3[vertices.Count];
        var uvs = new Vector2[vertices.Count];
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        for (var i = 0; i < vertices.Count; i++)
        {
            var p = vertices[i].Position;
            var position = new Vector3(p.X, p.Z, p.Y);
            positions[i] = position;
            min = Vector3.Min(min, position);
            max = Vector3.Max(max, position);

            var n = vertices[i].Normal;
            var normal = new Vector3(n.X, n.Z, n.Y);
            normals[i] = normal.LengthSquared() > 0.000001f ? Vector3.Normalize(normal) : Vector3.UnitY;
            uvs[i] = new Vector2(vertices[i].UV.U, vertices[i].UV.V);
        }

        var center = (min + max) * 0.5f;
        var radius = 0f;
        for (var i = 0; i < positions.Length; i++)
            radius = MathF.Max(radius, Vector3.Distance(positions[i], center));

        var materials = BuildPreviewMaterials(model, sections);
        var triangleMaterials = new int[Math.Max(1, indices.Length / 3)];
        if (sections.Count == 0)
        {
            Array.Fill(triangleMaterials, 0);
        }
        else
        {
            for (var sectionIndex = 0; sectionIndex < sections.Count; sectionIndex++)
            {
                var section = sections[sectionIndex];
                var firstTriangle = Math.Max(0, section.FirstIndex / 3);
                var lastTriangle = Math.Min(triangleMaterials.Length, firstTriangle + Math.Max(0, section.NumFaces));
                for (var triangle = firstTriangle; triangle < lastTriangle; triangle++)
                    triangleMaterials[triangle] = Math.Min(sectionIndex, materials.Length - 1);
            }
        }

        return new ModelClass
        {
            DisplayName = model.DisplayName,
            Kind = model.Kind,
            Positions = positions,
            Normals = normals,
            UVs = uvs,
            Indices = indices,
            TriangleMaterials = triangleMaterials,
            Materials = materials,
            Center = center,
            Radius = MathF.Max(radius, 1f)
        };
    }

    private static PreviewMaterial[] BuildPreviewMaterials(ModelItem model, IReadOnlyList<CMeshSection> sections)
    {
        if (sections.Count == 0)
            return
            [
                new PreviewMaterial
                {
                    Name = "Default",
                    Color = ThumbnailColor(model.DisplayName, model.Kind)
                }
            ];

        var materials = new PreviewMaterial[sections.Count];
        for (var i = 0; i < sections.Count; i++)
        {
            var section = sections[i];
            var name = section.MaterialName;
            if (string.IsNullOrWhiteSpace(name))
                name = section.Material?.Name.Text;
            if (string.IsNullOrWhiteSpace(name))
                name = $"Material {i + 1}";

            var color = MaterialColor(name, i, model.Kind);
            PreviewTexture? diffuse = null;
            if (section.Material?.Load<UMaterialInterface>() is { } material)
            {
                TryReadMaterialPreview(material, ref color, out diffuse);
            }

            materials[i] = new PreviewMaterial
            {
                Name = name,
                Color = color,
                Diffuse = diffuse
            };
        }

        return materials;
    }

    private static void TryReadMaterialPreview(UMaterialInterface material, ref Vector3 color, out PreviewTexture? diffuse)
    {
        diffuse = null;

        try
        {
            var parameters = new CMaterialParams2();
            material.GetParams(parameters, EMaterialFormat.AllLayers);

            var candidate = FindBestDiffuseTexture(parameters);
            if (candidate != null)
            {
                diffuse = DecodePreviewTexture(candidate);
                if (diffuse != null)
                    return;
            }

            if (parameters.TryGetLinearColor(out var linearColor, CMaterialParams2.Diffuse[0]))
                color = new Vector3(linearColor.R, linearColor.G, linearColor.B);
        }
        catch
        {
            diffuse = null;
        }
    }

    private static UTexture? FindBestDiffuseTexture(CMaterialParams2 parameters)
    {
        UTexture? best = null;
        var bestScore = int.MinValue;

        foreach (var nameGroup in CMaterialParams2.Diffuse)
        {
            foreach (var key in nameGroup)
            {
                if (!parameters.Textures.TryGetValue(key, out var unrealMaterial) || unrealMaterial is not UTexture texture)
                    continue;

                var score = ScoreDiffuseTexture(key, texture);
                if (score <= bestScore)
                    continue;

                best = texture;
                bestScore = score;
            }
        }

        if (best != null && bestScore > -10)
            return best;

        return parameters.TryGetFirstTexture2d(out var first) ? first : null;
    }

    private static int ScoreDiffuseTexture(string parameterName, UTexture texture)
    {
        var haystack = (parameterName + " " + texture.Name + " " + texture.GetPathName()).ToLowerInvariant();
        var score = 0;

        if (haystack.Contains("basecolor") || haystack.Contains("base_color")) score += 80;
        if (haystack.Contains("albedo")) score += 76;
        if (haystack.Contains("diffuse")) score += 70;
        if (haystack.Contains("_bc") || haystack.Contains("bc_") || haystack.Contains("bco")) score += 54;
        if (haystack.Contains("color") || haystack.Contains("colour")) score += 36;
        if (haystack.Contains("_d") || haystack.Contains(" d_")) score += 18;

        if (haystack.Contains("normal") || haystack.Contains("_n") || haystack.Contains("nrm") || haystack.Contains("nor")) score -= 90;
        if (haystack.Contains("mask") || haystack.Contains("packed") || haystack.Contains("orm") || haystack.Contains("mro") || haystack.Contains("rough") || haystack.Contains("metal") || haystack.Contains("ao")) score -= 80;
        if (haystack.Contains("emissive") || haystack.Contains("_e")) score -= 40;
        if (parameterName.Equals("Texture", StringComparison.OrdinalIgnoreCase) || parameterName.Equals("Color", StringComparison.OrdinalIgnoreCase)) score -= 18;

        return score;
    }

    private static PreviewTexture? DecodePreviewTexture(UTexture texture)
    {
        var decoded = texture.Decode(512, ETexturePlatform.DesktopMobile);
        if (decoded == null || decoded.Width <= 0 || decoded.Height <= 0)
            return null;

        var rgba = ToRgba(decoded);
        if (rgba == null)
            return null;

        return new PreviewTexture
        {
            Width = decoded.Width,
            Height = decoded.Height,
            Rgba = rgba,
            Name = texture.Name
        };
    }

    private static byte[]? ToRgba(CTexture texture)
    {
        var pixelCount = texture.Width * texture.Height;
        if (pixelCount <= 0)
            return null;

        var data = texture.Data;
        var rgba = new byte[pixelCount * 4];

        switch (texture.PixelFormat)
        {
            case EPixelFormat.PF_R8G8B8A8:
                if (data.Length < rgba.Length) return null;
                Buffer.BlockCopy(data, 0, rgba, 0, rgba.Length);
                return rgba;
            case EPixelFormat.PF_B8G8R8A8:
            case EPixelFormat.PF_A8R8G8B8:
                if (data.Length < rgba.Length) return null;
                for (var i = 0; i < pixelCount; i++)
                {
                    var src = i * 4;
                    rgba[src] = data[src + 2];
                    rgba[src + 1] = data[src + 1];
                    rgba[src + 2] = data[src];
                    rgba[src + 3] = data[src + 3];
                }
                return rgba;
            case EPixelFormat.PF_G8:
                if (data.Length < pixelCount) return null;
                for (var i = 0; i < pixelCount; i++)
                {
                    var dst = i * 4;
                    rgba[dst] = data[i];
                    rgba[dst + 1] = data[i];
                    rgba[dst + 2] = data[i];
                    rgba[dst + 3] = 255;
                }
                return rgba;
            default:
                return null;
        }
    }

    private static Vector3 MaterialColor(string name, int index, ModelKind kind)
    {
        unchecked
        {
            var hash = 29 + index * 97;
            foreach (var c in name)
                hash = hash * 31 + c;

            var hue = (Math.Abs(hash) % 360) / 360f;
            var sat = kind == ModelKind.SkeletalMesh ? 0.12f : 0.16f;
            var value = 0.58f + (Math.Abs(hash / 360) % 12) / 100f;
            var tint = HsvToRgb(hue, sat, Math.Clamp(value, 0.56f, 0.70f));
            var baseSkinLike = kind == ModelKind.SkeletalMesh
                ? new Vector3(0.58f, 0.68f, 0.60f)
                : new Vector3(0.62f, 0.66f, 0.68f);
            return Vector3.Lerp(baseSkinLike, tint, 0.38f);
        }
    }

    private static byte[]? RenderAndDispose(CSkeletalMesh mesh, int size, ModelItem model)
    {
        try
        {
            foreach (var lod in mesh.LODs)
            {
                if (lod.Verts == null || lod.Verts.Length == 0) continue;
                var indices = lod.Indices == null ? Array.Empty<uint>() : ReadIndices(lod.Indices.Value);
                return RenderMeshThumbnail(lod.Verts, indices, size, model);
            }
        }
        finally
        {
            (mesh as IDisposable)?.Dispose();
        }

        return null;
    }

    private static IReadOnlyList<uint> ReadIndices(object indexBuffer)
    {
        if (indexBuffer is IReadOnlyList<uint> uintList) return uintList;
        if (indexBuffer is uint[] uintArray) return uintArray;
        if (indexBuffer is IReadOnlyList<ushort> ushortList) return ushortList.Select(i => (uint)i).ToArray();
        if (indexBuffer is ushort[] ushortArray) return ushortArray.Select(i => (uint)i).ToArray();

        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var type = indexBuffer.GetType();
        var dataProperty = type.GetProperty("Indices", flags) ??
                           type.GetProperty("Indices32", flags) ??
                           type.GetProperty("Indices16", flags) ??
                           type.GetProperty("IndexData", flags) ??
                           type.GetProperty("Data", flags);
        if (dataProperty?.GetValue(indexBuffer) is object data && !ReferenceEquals(data, indexBuffer))
            return ReadIndices(data);

        var dataField = type.GetField("Indices", flags) ??
                        type.GetField("Indices32", flags) ??
                        type.GetField("Indices16", flags) ??
                        type.GetField("IndexData", flags) ??
                        type.GetField("Data", flags);
        if (dataField?.GetValue(indexBuffer) is object fieldData && !ReferenceEquals(fieldData, indexBuffer))
            return ReadIndices(fieldData);

        if (indexBuffer is System.Collections.IEnumerable enumerable)
        {
            var indices = new List<uint>();
            foreach (var value in enumerable)
                indices.Add(Convert.ToUInt32(value));
            return indices;
        }

        return Array.Empty<uint>();
    }

    private static byte[]? RenderMeshThumbnail(IReadOnlyList<CMeshVertex> vertices, IReadOnlyList<uint> indices, int size, ModelItem model)
    {
        if (vertices.Count == 0 || size < 16) return null;

        var transformed = new ThumbVertex[vertices.Count];
        var min = new Vector2(float.MaxValue, float.MaxValue);
        var max = new Vector2(float.MinValue, float.MinValue);

        for (var i = 0; i < vertices.Count; i++)
        {
            var p = vertices[i].Position;
            var v = new Vector3(p.X, p.Y, p.Z);
            var r = RotateForThumbnail(v);
            var projected = new Vector2(r.X, -r.Z + r.Y * 0.16f);

            transformed[i] = new ThumbVertex(r, projected);
            min = Vector2.Min(min, projected);
            max = Vector2.Max(max, projected);
        }

        var span = max - min;
        if (span.X <= 0.0001f || span.Y <= 0.0001f) return null;

        var scale = (size - 14f) / MathF.Max(span.X, span.Y);
        var center = (min + max) * 0.5f;
        var imageCenter = new Vector2(size * 0.5f, size * 0.52f);

        for (var i = 0; i < transformed.Length; i++)
        {
            var screen = (transformed[i].Projected - center) * scale + imageCenter;
            transformed[i] = transformed[i] with { Projected = screen };
        }

        var pixels = new byte[size * size * 4];
        var depth = new float[size * size];
        Array.Fill(depth, float.NegativeInfinity);

        var baseColor = ThumbnailColor(model.DisplayName, model.Kind);
        var painted = 0;

        if (indices.Count < 3)
        {
            DrawVertexCloud(pixels, depth, transformed, baseColor, size);
            AddOutline(pixels, size);
            return pixels;
        }

        var lightDir = Vector3.Normalize(new Vector3(-0.45f, -0.70f, 0.55f));
        var totalTriangles = indices.Count / 3;
        var triangleBudget = size >= 160 ? 70000 : 45000;
        var triangleStep = Math.Max(1, (int)MathF.Ceiling(totalTriangles / (float)triangleBudget));

        if (triangleStep > 1)
            DrawVertexCloud(pixels, depth, transformed, baseColor, size);

        for (var triangle = 0; triangle < totalTriangles; triangle += triangleStep)
        {
            var i0 = (int)indices[triangle * 3];
            var i1 = (int)indices[triangle * 3 + 1];
            var i2 = (int)indices[triangle * 3 + 2];
            if ((uint)i0 >= vertices.Count || (uint)i1 >= vertices.Count || (uint)i2 >= vertices.Count) continue;

            var a = transformed[i0];
            var b = transformed[i1];
            var c = transformed[i2];
            var area = Edge(a.Projected, b.Projected, c.Projected);
            if (MathF.Abs(area) < 0.05f) continue;

            var normal = Vector3.Cross(b.World - a.World, c.World - a.World);
            if (normal.LengthSquared() < 0.000001f) continue;
            normal = Vector3.Normalize(normal);
            var shade = 0.35f + MathF.Max(0f, Vector3.Dot(normal, lightDir)) * 0.55f;
            if (area < 0f) shade *= 0.76f;

            var minX = Math.Clamp((int)MathF.Floor(MathF.Min(a.Projected.X, MathF.Min(b.Projected.X, c.Projected.X))), 0, size - 1);
            var maxX = Math.Clamp((int)MathF.Ceiling(MathF.Max(a.Projected.X, MathF.Max(b.Projected.X, c.Projected.X))), 0, size - 1);
            var minY = Math.Clamp((int)MathF.Floor(MathF.Min(a.Projected.Y, MathF.Min(b.Projected.Y, c.Projected.Y))), 0, size - 1);
            var maxY = Math.Clamp((int)MathF.Ceiling(MathF.Max(a.Projected.Y, MathF.Max(b.Projected.Y, c.Projected.Y))), 0, size - 1);

            for (var y = minY; y <= maxY; y++)
            {
                for (var x = minX; x <= maxX; x++)
                {
                    var p = new Vector2(x + 0.5f, y + 0.5f);
                    var w0 = Edge(b.Projected, c.Projected, p);
                    var w1 = Edge(c.Projected, a.Projected, p);
                    var w2 = Edge(a.Projected, b.Projected, p);
                    if (!SameSign(area, w0, w1, w2)) continue;

                    w0 /= area;
                    w1 /= area;
                    w2 /= area;
                    var z = a.World.Y * w0 + b.World.Y * w1 + c.World.Y * w2;
                    var offset = y * size + x;
                    if (z <= depth[offset]) continue;

                    depth[offset] = z;
                    var pixel = offset * 4;
                    pixels[pixel] = (byte)Math.Clamp(baseColor.X * shade * 255f, 0f, 255f);
                    pixels[pixel + 1] = (byte)Math.Clamp(baseColor.Y * shade * 255f, 0f, 255f);
                    pixels[pixel + 2] = (byte)Math.Clamp(baseColor.Z * shade * 255f, 0f, 255f);
                    pixels[pixel + 3] = 255;
                    painted++;
                }
            }
        }

        if (painted == 0)
            DrawVertexCloud(pixels, depth, transformed, baseColor, size);

        AddOutline(pixels, size);
        return pixels;
    }

    private static void DrawVertexCloud(byte[] pixels, float[] depth, IReadOnlyList<ThumbVertex> vertices, Vector3 baseColor, int size)
    {
        var stride = Math.Max(1, vertices.Count / 9000);
        for (var i = 0; i < vertices.Count; i += stride)
        {
            var v = vertices[i];
            var x = (int)MathF.Round(v.Projected.X);
            var y = (int)MathF.Round(v.Projected.Y);
            var radius = size >= 120 ? 2 : 1;

            for (var py = y - radius; py <= y + radius; py++)
            {
                if ((uint)py >= size) continue;
                for (var px = x - radius; px <= x + radius; px++)
                {
                    if ((uint)px >= size) continue;
                    var offset = py * size + px;
                    if (v.World.Y <= depth[offset]) continue;

                    depth[offset] = v.World.Y;
                    var shade = 0.55f + Math.Clamp((v.World.Z + 0.5f) * 0.18f, 0f, 0.35f);
                    var pixel = offset * 4;
                    pixels[pixel] = (byte)Math.Clamp(baseColor.X * shade * 255f, 0f, 255f);
                    pixels[pixel + 1] = (byte)Math.Clamp(baseColor.Y * shade * 255f, 0f, 255f);
                    pixels[pixel + 2] = (byte)Math.Clamp(baseColor.Z * shade * 255f, 0f, 255f);
                    pixels[pixel + 3] = 255;
                }
            }
        }
    }

    private static Vector3 RotateForThumbnail(Vector3 p)
    {
        const float yaw = -0.68f;
        const float pitch = 0.18f;
        var cy = MathF.Cos(yaw);
        var sy = MathF.Sin(yaw);
        var cp = MathF.Cos(pitch);
        var sp = MathF.Sin(pitch);

        var x = p.X * cy - p.Y * sy;
        var y = p.X * sy + p.Y * cy;
        var z = p.Z * cp - y * sp;
        y = p.Z * sp + y * cp;
        return new Vector3(x, y, z);
    }

    private static float Edge(Vector2 a, Vector2 b, Vector2 p)
    {
        return (p.X - a.X) * (b.Y - a.Y) - (p.Y - a.Y) * (b.X - a.X);
    }

    private static bool SameSign(float area, float w0, float w1, float w2)
    {
        return area > 0f ? w0 >= 0f && w1 >= 0f && w2 >= 0f : w0 <= 0f && w1 <= 0f && w2 <= 0f;
    }

    private static Vector3 ThumbnailColor(string name, ModelKind kind)
    {
        unchecked
        {
            var hash = 17;
            foreach (var c in name)
                hash = hash * 31 + c;

            var hue = (Math.Abs(hash) % 360) / 360f;
            return HsvToRgb(hue, 0.30f, kind == ModelKind.SkeletalMesh ? 0.92f : 0.82f);
        }
    }

    private static Vector3 HsvToRgb(float h, float s, float v)
    {
        var i = (int)MathF.Floor(h * 6f);
        var f = h * 6f - i;
        var p = v * (1f - s);
        var q = v * (1f - f * s);
        var t = v * (1f - (1f - f) * s);

        return (i % 6) switch
        {
            0 => new Vector3(v, t, p),
            1 => new Vector3(q, v, p),
            2 => new Vector3(p, v, t),
            3 => new Vector3(p, q, v),
            4 => new Vector3(t, p, v),
            _ => new Vector3(v, p, q)
        };
    }

    private static void AddOutline(byte[] pixels, int size)
    {
        var copy = (byte[])pixels.Clone();
        for (var y = 1; y < size - 1; y++)
        {
            for (var x = 1; x < size - 1; x++)
            {
                var offset = (y * size + x) * 4;
                if (copy[offset + 3] != 0) continue;

                var touches =
                    copy[((y - 1) * size + x) * 4 + 3] != 0 ||
                    copy[((y + 1) * size + x) * 4 + 3] != 0 ||
                    copy[(y * size + x - 1) * 4 + 3] != 0 ||
                    copy[(y * size + x + 1) * 4 + 3] != 0;
                if (!touches) continue;

                pixels[offset] = 18;
                pixels[offset + 1] = 24;
                pixels[offset + 2] = 28;
                pixels[offset + 3] = 190;
            }
        }
    }

    private readonly record struct ThumbVertex(Vector3 World, Vector2 Projected);
    private readonly record struct ModelScanRoot(string Path, ModelCategory Category);
    private readonly record struct ScannableFile(GameFile File, ModelCategory Category)
    {
        public string Path => File.Path;
    }
    private sealed record ModelIndexCache(int Version, string Key, ModelItem[] Models);
}
