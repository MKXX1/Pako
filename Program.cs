using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ImGuiNET;
using Veldrid;
using Veldrid.Sdl2;
using Veldrid.StartupUtilities;

namespace Pako;

internal static class Program
{
    private enum AppScreen
    {
        Setup,
        Loading,
        Browser
    }

    private static Sdl2Window _window = null!;
    private static GraphicsDevice _graphicsDevice = null!;
    private static CommandList _commandList = null!;
    private static ImGuiController _controller = null!;
    private static readonly ConfigStore ConfigStore = new();
    private static readonly OutlastAssetService Assets = new();
    private static readonly ThumbnailCache Thumbnails = new(Assets, 160);
    private static readonly List<string> Logs = new();
    private static AppScreen _screen = AppScreen.Setup;
    private static CancellationTokenSource? _scanCts;
    private static Task? _scanTask;
    private static Task? _loadTask;
    private static string _search = "";
    private static ModelItem? _selectedModel;
    private static string _setupPath = "";
    private static string _loadingPath = "";
    private static string _loadError = "";
    private static bool _loadSucceeded;
    private static string _pendingFolder = "";
    private static string _pendingExportFolder = "";
    private static string _pendingBlenderFile = "";
    private static string _blenderPath = "";
    private static string _lastExport = "";
    private static PakoMeshExportFormat _exportFormat = PakoMeshExportFormat.Glb;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0)
            return RunCli(args);

        Application.EnableVisualStyles();
        ImGui.CreateContext();
        ImGui.GetIO().Fonts.AddFontDefault();
        _setupPath = ConfigStore.Config.GameDirectory;
        _blenderPath = ConfigStore.Config.BlenderPath;
        BlenderFbxConverter.BlenderPath = _blenderPath;

        VeldridStartup.CreateWindowAndGraphicsDevice(
            new WindowCreateInfo(80, 80, 1500, 900, WindowState.Normal, "Pako - Outlast Trials Model Browser"),
            new GraphicsDeviceOptions(false, null, true, ResourceBindingModel.Improved, true, true),
            out _window,
            out _graphicsDevice);

        _window.Resized += () =>
        {
            _graphicsDevice.MainSwapchain.Resize((uint)_window.Width, (uint)_window.Height);
            _controller.WindowResized(_window.Width, _window.Height);
        };

        _commandList = _graphicsDevice.ResourceFactory.CreateCommandList();
        _controller = new ImGuiController(_graphicsDevice, _graphicsDevice.MainSwapchain.Framebuffer.OutputDescription, _window.Width, _window.Height);

        var previousTicks = DateTime.UtcNow.Ticks;
        while (_window.Exists)
        {
            var nowTicks = DateTime.UtcNow.Ticks;
            var delta = (nowTicks - previousTicks) / (float)TimeSpan.TicksPerSecond;
            previousTicks = nowTicks;

            var snapshot = _window.PumpEvents();
            if (!_window.Exists) break;

            _controller.Update(delta, snapshot);
            Thumbnails.PumpCompleted(_graphicsDevice, _controller);
            DrawUi();

            _commandList.Begin();
            _commandList.SetFramebuffer(_graphicsDevice.MainSwapchain.Framebuffer);
            _commandList.ClearColorTarget(0, new RgbaFloat(0.055f, 0.058f, 0.064f, 1f));
            _controller.Render(_graphicsDevice, _commandList);
            _commandList.End();
            _graphicsDevice.SubmitCommands(_commandList);
            _graphicsDevice.SwapBuffers(_graphicsDevice.MainSwapchain);
        }

        _scanCts?.Cancel();
        _graphicsDevice.WaitForIdle();
        _controller.Dispose();
        Thumbnails.Dispose();
        _commandList.Dispose();
        _graphicsDevice.Dispose();
        return 0;
    }

    private static int RunCli(string[] args)
    {
        var resultPath = GetArg(args, "--result");
        try
        {
            if (HasArg(args, "--help") || HasArg(args, "-h"))
            {
                PrintCliHelp();
                return 0;
            }

            if (!HasArg(args, "--export-model"))
            {
                Console.Error.WriteLine("Unknown command. Use --help for usage.");
                WriteCliResult(resultPath, new { ok = false, error = "Unknown command. Use --help for usage." });
                return 2;
            }

            var gamePath = GetArg(args, "--game");
            var objectPath = GetArg(args, "--object");
            var outputPath = GetArg(args, "--output");
            var formatText = GetArg(args, "--format") ?? "glb";
            BlenderFbxConverter.BlenderPath = GetArg(args, "--blender") ?? ConfigStore.Config.BlenderPath;

            if (string.IsNullOrWhiteSpace(gamePath) ||
                string.IsNullOrWhiteSpace(objectPath) ||
                string.IsNullOrWhiteSpace(outputPath))
            {
                Console.Error.WriteLine("--export-model requires --game, --object and --output.");
                WriteCliResult(resultPath, new { ok = false, error = "--export-model requires --game, --object and --output." });
                return 2;
            }

            var format = formatText.ToLowerInvariant() switch
            {
                "actorx" or "psk" or "pskx" => PakoMeshExportFormat.ActorX,
                "fbx" => PakoMeshExportFormat.Fbx,
                _ => PakoMeshExportFormat.Glb
            };

            var service = new OutlastAssetService();
            if (!service.Mount(gamePath, out var mountError))
            {
                Console.Error.WriteLine($"Mount failed: {mountError}");
                WriteCliResult(resultPath, new { ok = false, error = $"Mount failed: {mountError}" });
                return 1;
            }

            Directory.CreateDirectory(outputPath);
            var model = CreateCliModelItem(objectPath);
            if (!service.ExportModel(model, outputPath, format, out var savedFilePath, out var exportError))
            {
                Console.Error.WriteLine($"Export failed: {exportError}");
                WriteCliResult(resultPath, new { ok = false, error = $"Export failed: {exportError}" });
                return 1;
            }

            var result = JsonSerializer.Serialize(new
            {
                ok = true,
                file = savedFilePath,
                export_root = outputPath
            });
            WriteCliResult(resultPath, new
            {
                ok = true,
                file = savedFilePath,
                export_root = outputPath
            });
            Console.WriteLine(result);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            WriteCliResult(resultPath, new { ok = false, error = ex.Message });
            return 1;
        }
    }

    private static ModelItem CreateCliModelItem(string objectPath)
    {
        var displayName = objectPath;
        var separator = objectPath.LastIndexOf('.');
        if (separator >= 0 && separator < objectPath.Length - 1)
            displayName = objectPath[(separator + 1)..];

        var packagePath = separator > 0 ? objectPath[..separator] + ".uasset" : objectPath + ".uasset";
        return new ModelItem
        {
            DisplayName = displayName,
            PackagePath = packagePath,
            ObjectPath = objectPath,
            Category = ModelCategory.Other,
            Folder = "",
            Kind = ModelKind.StaticMesh
        };
    }

    private static bool HasArg(string[] args, string name)
    {
        return args.Any(arg => arg.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private static string? GetArg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }

        return null;
    }

    private static void PrintCliHelp()
    {
        Console.WriteLine("Pako command line");
        Console.WriteLine("  Pako.exe --export-model --game <game-or-paks-folder> --object <ue-object-path> --output <folder> [--format glb|actorx|fbx] [--blender <blender.exe>] [--result result.json]");
    }

    private static void WriteCliResult(string? resultPath, object result)
    {
        if (string.IsNullOrWhiteSpace(resultPath)) return;

        var directory = Path.GetDirectoryName(resultPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(resultPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void DrawUi()
    {
        ApplyStyle(ConfigStore.Config.DarkTheme);

        ImGui.SetNextWindowPos(Vector2.Zero);
        ImGui.SetNextWindowSize(new Vector2(_window.Width, _window.Height));
        ImGui.Begin("PakoRoot", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse);

        HandlePendingFolders();

        switch (_screen)
        {
            case AppScreen.Setup:
                DrawSetupScreen();
                break;
            case AppScreen.Loading:
                DrawLoadingScreen();
                break;
            case AppScreen.Browser:
                DrawBrowserUi();
                break;
        }

        ImGui.End();
    }

    private static void DrawBrowserUi()
    {
        DrawToolbar();
        ImGui.Separator();

        var contentSize = ImGui.GetContentRegionAvail();
        var leftWidth = Math.Clamp(contentSize.X * 0.54f, 560f, 900f);

        ImGui.BeginChild("models", new Vector2(leftWidth, contentSize.Y));
        DrawModelList();
        ImGui.EndChild();

        ImGui.SameLine();

        ImGui.BeginChild("details", new Vector2(0, contentSize.Y));
        DrawDetails();
        ImGui.EndChild();
    }

    private static void DrawSetupScreen()
    {
        var drawList = ImGui.GetWindowDrawList();
        var size = new Vector2(_window.Width, _window.Height);
        var panelSize = new Vector2(Math.Min(680f, size.X - 48f), 330f);
        var panelPos = (size - panelSize) * 0.5f;

        drawList.AddRectFilled(Vector2.Zero, size, Color(new Vector4(0.055f, 0.058f, 0.064f, 1f)));
        ImGui.SetCursorScreenPos(panelPos);
        ImGui.BeginChild("setup-panel", panelSize, ImGuiChildFlags.Borders, ImGuiWindowFlags.NoScrollbar);

        ImGui.TextUnformatted("Pako");
        ImGui.TextColored(new Vector4(0.58f, 0.80f, 0.78f, 1f), "Outlast Trials Model Browser");
        ImGui.Spacing();
        ImGui.TextWrapped("Select The Outlast Trials game folder or OPP/Content/Paks. Models will be scanned before the browser opens.");
        ImGui.Spacing();

        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##game-path", ref _setupPath, 1024);

        if (ImGui.Button("Browse Game", new Vector2(150f, 0f)))
            BrowseFolder(path => _pendingFolder = path);

        ImGui.SameLine();
        var canLoad = !string.IsNullOrWhiteSpace(_setupPath);
        if (!canLoad) ImGui.BeginDisabled();
        if (ImGui.Button("Load", new Vector2(110f, 0f)))
            StartLoadGame(_setupPath);
        if (!canLoad) ImGui.EndDisabled();

        ImGui.Spacing();
        ImGui.TextUnformatted("Blender:");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##setup-blender-path", ref _blenderPath, 1024);

        if (ImGui.Button("Browse Blender", new Vector2(150f, 0f)))
            BrowseBlender(path => _pendingBlenderFile = path);

        ImGui.SameLine();
        if (ImGui.Button("Save Blender", new Vector2(130f, 0f)))
            SaveBlenderPath(_blenderPath);

        ImGui.SameLine();
        if (ImGui.Button("Auto", new Vector2(70f, 0f)))
            SaveBlenderPath("");

        if (!string.IsNullOrWhiteSpace(_loadError))
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.95f, 0.42f, 0.36f, 1f), _loadError);
        }

        ImGui.EndChild();
    }

    private static void DrawLoadingScreen()
    {
        if (_loadTask?.IsCompleted == true)
        {
            if (_loadSucceeded)
            {
                _screen = AppScreen.Browser;
                return;
            }

            _screen = AppScreen.Setup;
            return;
        }

        var size = new Vector2(_window.Width, _window.Height);
        var center = size * 0.5f;
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(Vector2.Zero, size, Color(new Vector4(0.055f, 0.058f, 0.064f, 1f)));
        DrawSpinner(drawList, center + new Vector2(0f, -48f), 28f, Color(new Vector4(0.72f, 0.92f, 0.90f, 1f)));

        var total = Math.Max(Assets.ScanTotal, 1);
        var fraction = Math.Clamp(Assets.ScanCompleted / (float)total, 0f, 1f);
        ImGui.SetCursorScreenPos(center + new Vector2(-240f, 4f));
        ImGui.BeginChild("loading-panel", new Vector2(480f, 140f), ImGuiChildFlags.None, ImGuiWindowFlags.NoScrollbar);
        ImGui.TextUnformatted("Loading game assets");
        ImGui.TextColored(new Vector4(0.57f, 0.60f, 0.64f, 1f), _loadingPath);
        ImGui.Spacing();
        ImGui.ProgressBar(fraction, new Vector2(-1f, 20f), Assets.ScanTotal > 0 ? $"{Assets.ScanCompleted}/{Assets.ScanTotal}" : "Mounting");
        ImGui.TextWrapped(Assets.Status);
        ImGui.Spacing();
        if (ImGui.Button("Cancel"))
        {
            _scanCts?.Cancel();
            _loadError = "Loading canceled.";
        }
        ImGui.EndChild();
    }

    private static void DrawToolbar()
    {
        ImGui.Text("Game:");
        ImGui.SameLine();
        ImGui.TextUnformatted(string.IsNullOrEmpty(Assets.GameDirectory) ? "not mounted" : Assets.GameDirectory);

        ImGui.SameLine();
        if (ImGui.Button("Browse Game"))
            BrowseFolder(path => _pendingFolder = path);

        ImGui.SameLine();
        if (ImGui.Button("Rescan Models") && Assets.IsMounted && !Assets.IsBusy)
            StartScan();

        ImGui.SameLine();
        if (Assets.IsBusy && ImGui.Button("Cancel"))
            _scanCts?.Cancel();

        ImGui.SameLine();
        var dark = ConfigStore.Config.DarkTheme;
        if (ImGui.Checkbox("Dark", ref dark))
        {
            ConfigStore.Config.DarkTheme = dark;
            ConfigStore.Save();
        }

        if (!string.IsNullOrEmpty(_pendingExportFolder))
        {
            ConfigStore.Config.ExportDirectory = _pendingExportFolder;
            ConfigStore.Save();
            _pendingExportFolder = "";
        }

        if (!string.IsNullOrEmpty(_pendingBlenderFile))
        {
            SaveBlenderPath(_pendingBlenderFile);
            _pendingBlenderFile = "";
        }

        ImGui.TextColored(new Vector4(0.44f, 0.82f, 0.58f, 1f), Assets.Status);
        if (Assets.IsBusy)
            DrawScanLoading();
    }

    private static void DrawModelList()
    {
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##search", ref _search, 256);

        var filtered = FilteredModels().ToArray();
        ImGui.TextUnformatted($"Models: {filtered.Length} / {Assets.Models.Count}");

        ImGui.BeginChild("model-card-list", new Vector2(0, 0));
        DrawCategoryGroup("Characters", ModelCategory.Characters, filtered);
        DrawCategoryGroup("Props", ModelCategory.Props, filtered);
        DrawCategoryGroup("Other", ModelCategory.Other, filtered);
        ImGui.EndChild();
    }

    private static void DrawCategoryGroup(string label, ModelCategory category, IReadOnlyList<ModelItem> models)
    {
        var items = models
            .Where(m => m.Category == category)
            .OrderBy(m => m.Folder, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (items.Length == 0) return;

        var flags = ImGuiTreeNodeFlags.SpanAvailWidth;
        if (!string.IsNullOrWhiteSpace(_search))
            flags |= ImGuiTreeNodeFlags.DefaultOpen;

        var open = ImGui.TreeNodeEx($"{label} ({items.Length})", flags);
        if (!open) return;

        foreach (var group in items.GroupBy(m => m.Folder).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            DrawFolderGroup(group.Key, group.ToArray());

        ImGui.TreePop();
    }

    private static void DrawFolderGroup(string folder, IReadOnlyList<ModelItem> models)
    {
        var label = string.IsNullOrWhiteSpace(folder) ? "Root" : folder;
        var flags = ImGuiTreeNodeFlags.SpanAvailWidth;
        if (!string.IsNullOrWhiteSpace(_search))
            flags |= ImGuiTreeNodeFlags.DefaultOpen;

        var open = ImGui.TreeNodeEx($"{label} ({models.Count})", flags);
        if (!open) return;

        foreach (var model in models)
            DrawModelCard(model);

        ImGui.TreePop();
    }

    private static void DrawModelCard(ModelItem model)
    {
        const float rowHeight = 118f;
        const float iconSize = 96f;
        var drawList = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        var width = Math.Max(320f, ImGui.GetContentRegionAvail().X);
        var selected = ReferenceEquals(model, _selectedModel);

        ImGui.PushID(model.ObjectPath);
        ImGui.InvisibleButton("model-card", new Vector2(width, rowHeight));
        var hovered = ImGui.IsItemHovered();
        if (ImGui.IsItemClicked())
            _selectedModel = model;

        var bg = selected
            ? new Vector4(0.18f, 0.32f, 0.40f, 1f)
            : hovered
                ? new Vector4(0.13f, 0.15f, 0.17f, 1f)
                : new Vector4(0.085f, 0.09f, 0.10f, 1f);
        var border = selected
            ? new Vector4(0.36f, 0.72f, 0.78f, 1f)
            : new Vector4(0.18f, 0.20f, 0.22f, 1f);

        drawList.AddRectFilled(start, start + new Vector2(width, rowHeight - 4f), Color(bg), 5f);
        drawList.AddRect(start, start + new Vector2(width, rowHeight - 4f), Color(border), 5f);

        var iconPos = start + new Vector2(9f, 7f);
        DrawModelPreview(drawList, model, iconPos, iconSize);

        var textX = iconPos.X + iconSize + 12f;
        var maxTextWidth = width - iconSize - 36f;
        var name = Ellipsize(model.DisplayName, maxTextWidth, true);
        var type = model.Kind == ModelKind.SkeletalMesh ? "Skeletal Mesh" : "Static Mesh";
        var folder = Ellipsize(model.Folder, maxTextWidth, false);

        drawList.AddText(new Vector2(textX, start.Y + 10f), Color(new Vector4(0.93f, 0.95f, 0.96f, 1f)), name);
        drawList.AddText(new Vector2(textX, start.Y + 36f), Color(new Vector4(0.58f, 0.80f, 0.78f, 1f)), type);
        drawList.AddText(new Vector2(textX, start.Y + 62f), Color(new Vector4(0.57f, 0.60f, 0.64f, 1f)), folder);
        drawList.AddText(new Vector2(textX, start.Y + 88f), Color(new Vector4(0.47f, 0.50f, 0.55f, 1f)), CategoryLabel(model.Category));

        if (ImGui.BeginPopupContextItem("model-card-menu"))
        {
            if (ImGui.MenuItem("Copy Object Path"))
                ImGui.SetClipboardText(model.ObjectPath);
            if (ImGui.MenuItem($"Export {GetExportFormatLabel()}"))
                Export(model);
            ImGui.EndPopup();
        }

        ImGui.PopID();
    }

    private static void DrawModelIcon(ImDrawListPtr drawList, ModelItem model, Vector2 pos, float size)
    {
        var hue = NameHue(model.DisplayName);
        var accent = HsvToRgb(hue, 0.42f, model.Kind == ModelKind.SkeletalMesh ? 0.78f : 0.68f);
        var dark = new Vector4(accent.X * 0.30f, accent.Y * 0.30f, accent.Z * 0.30f, 1f);
        var light = new Vector4(Math.Min(accent.X + 0.22f, 1f), Math.Min(accent.Y + 0.22f, 1f), Math.Min(accent.Z + 0.22f, 1f), 1f);

        drawList.AddRectFilled(pos, pos + new Vector2(size, size), Color(dark), 6f);
        drawList.AddRect(pos, pos + new Vector2(size, size), Color(new Vector4(light.X, light.Y, light.Z, 0.85f)), 6f);

        if (model.Kind == ModelKind.SkeletalMesh)
        {
            var head = pos + new Vector2(size * 0.50f, size * 0.25f);
            var spine = pos + new Vector2(size * 0.50f, size * 0.50f);
            var hip = pos + new Vector2(size * 0.50f, size * 0.68f);
            var leftArm = pos + new Vector2(size * 0.28f, size * 0.48f);
            var rightArm = pos + new Vector2(size * 0.72f, size * 0.48f);
            var leftLeg = pos + new Vector2(size * 0.34f, size * 0.86f);
            var rightLeg = pos + new Vector2(size * 0.66f, size * 0.86f);
            var col = Color(new Vector4(0.93f, 0.96f, 0.92f, 1f));

            drawList.AddCircleFilled(head, size * 0.10f, col);
            drawList.AddLine(head + new Vector2(0, size * 0.10f), spine, col, 2.4f);
            drawList.AddLine(spine, hip, col, 2.4f);
            drawList.AddLine(spine, leftArm, col, 2.2f);
            drawList.AddLine(spine, rightArm, col, 2.2f);
            drawList.AddLine(hip, leftLeg, col, 2.2f);
            drawList.AddLine(hip, rightLeg, col, 2.2f);
            drawList.AddText(pos + new Vector2(6f, size - 20f), Color(new Vector4(0.93f, 0.96f, 0.92f, 0.85f)), "SK");
        }
        else
        {
            var col = Color(new Vector4(0.94f, 0.96f, 0.92f, 1f));
            var p1 = pos + new Vector2(size * 0.28f, size * 0.32f);
            var p2 = pos + new Vector2(size * 0.55f, size * 0.18f);
            var p3 = pos + new Vector2(size * 0.78f, size * 0.34f);
            var p4 = pos + new Vector2(size * 0.50f, size * 0.50f);
            var p5 = pos + new Vector2(size * 0.28f, size * 0.66f);
            var p6 = pos + new Vector2(size * 0.76f, size * 0.68f);

            drawList.AddLine(p1, p2, col, 2.2f);
            drawList.AddLine(p2, p3, col, 2.2f);
            drawList.AddLine(p3, p4, col, 2.2f);
            drawList.AddLine(p4, p1, col, 2.2f);
            drawList.AddLine(p1, p5, col, 2.2f);
            drawList.AddLine(p4, p6, col, 2.2f);
            drawList.AddLine(p5, p6, col, 2.2f);
            drawList.AddLine(p3, p6, col, 2.2f);
            drawList.AddText(pos + new Vector2(6f, size - 20f), Color(new Vector4(0.93f, 0.96f, 0.92f, 0.85f)), "ST");
        }
    }

    private static void DrawModelPreview(ImDrawListPtr drawList, ModelItem model, Vector2 pos, float size)
    {
        var textureId = Thumbnails.GetOrRequest(model);
        if (textureId == null)
        {
            DrawModelIcon(drawList, model, pos, size);
            if (Thumbnails.IsPending(model))
                DrawSpinner(drawList, pos + new Vector2(size - 16f, 16f), 7f, Color(new Vector4(0.72f, 0.92f, 0.90f, 1f)));
            else if (!string.IsNullOrWhiteSpace(Thumbnails.GetError(model)))
                drawList.AddText(pos + new Vector2(7f, 7f), Color(new Vector4(0.95f, 0.42f, 0.36f, 0.95f)), "no preview");
            return;
        }

        drawList.AddRectFilled(pos, pos + new Vector2(size, size), Color(new Vector4(0.055f, 0.060f, 0.066f, 1f)), 6f);
        drawList.AddImage(textureId.Value, pos + new Vector2(3f, 3f), pos + new Vector2(size - 3f, size - 3f));
        drawList.AddRect(pos, pos + new Vector2(size, size), Color(new Vector4(0.30f, 0.36f, 0.38f, 0.95f)), 6f);
    }

    private static void DrawDetails()
    {
        ImGui.Text("Selected");
        ImGui.Separator();

        if (_selectedModel == null)
        {
            ImGui.TextWrapped("Select a model from the list.");
        }
        else
        {
            var drawList = ImGui.GetWindowDrawList();
            var previewPos = ImGui.GetCursorScreenPos();
            DrawModelPreview(drawList, _selectedModel, previewPos, 168f);
            ImGui.Dummy(new Vector2(168f, 178f));

            ImGui.TextUnformatted(_selectedModel.DisplayName);
            ImGui.TextUnformatted(_selectedModel.Kind == ModelKind.StaticMesh ? "StaticMesh" : "SkeletalMesh");
            ImGui.TextUnformatted(CategoryLabel(_selectedModel.Category));
            ImGui.TextWrapped(_selectedModel.ObjectPath);

            ImGui.Spacing();
            if (ImGui.Button($"Export {GetExportFormatLabel()}"))
                Export(_selectedModel);

            ImGui.SameLine();
            if (ImGui.Button("Copy Path"))
                ImGui.SetClipboardText(_selectedModel.ObjectPath);
        }

        ImGui.Spacing();
        ImGui.Separator();
        DrawExportFormatSelector();
        ImGui.Spacing();
        ImGui.Text("Export Folder:");
        ImGui.TextWrapped(ConfigStore.Config.ExportDirectory);
        if (ImGui.Button("Change Export Folder"))
            BrowseFolder(path => _pendingExportFolder = path);

        ImGui.Spacing();
        ImGui.Text("Blender:");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##details-blender-path", ref _blenderPath, 1024);
        if (ImGui.Button("Browse Blender"))
            BrowseBlender(path => _pendingBlenderFile = path);

        ImGui.SameLine();
        if (ImGui.Button("Save Blender"))
            SaveBlenderPath(_blenderPath);

        ImGui.SameLine();
        if (ImGui.Button("Auto Detect"))
            SaveBlenderPath("");

        if (!string.IsNullOrEmpty(_lastExport))
        {
            ImGui.Spacing();
            ImGui.Text("Last export:");
            ImGui.TextWrapped(_lastExport);
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Text("Log");
        ImGui.BeginChild("log", new Vector2(0, 0));
        foreach (var line in Logs.TakeLast(200))
            ImGui.TextWrapped(line);
        ImGui.EndChild();
    }

    private static IEnumerable<ModelItem> FilteredModels()
    {
        if (string.IsNullOrWhiteSpace(_search)) return Assets.Models;
        return Assets.Models.Where(m =>
            m.DisplayName.Contains(_search, StringComparison.OrdinalIgnoreCase) ||
            m.ObjectPath.Contains(_search, StringComparison.OrdinalIgnoreCase) ||
            m.Folder.Contains(_search, StringComparison.OrdinalIgnoreCase));
    }

    private static void HandlePendingFolders()
    {
        if (!string.IsNullOrEmpty(_pendingBlenderFile))
        {
            SaveBlenderPath(_pendingBlenderFile);
            _pendingBlenderFile = "";
        }

        if (string.IsNullOrEmpty(_pendingFolder)) return;

        if (_screen == AppScreen.Browser)
            StartLoadGame(_pendingFolder);
        else
            _setupPath = _pendingFolder;

        _pendingFolder = "";
    }

    private static void DrawScanLoading()
    {
        var total = Math.Max(Assets.ScanTotal, 1);
        var fraction = Math.Clamp(Assets.ScanCompleted / (float)total, 0f, 1f);
        var drawList = ImGui.GetWindowDrawList();
        var cursor = ImGui.GetCursorScreenPos();
        DrawSpinner(drawList, cursor + new Vector2(9f, 9f), 8f, Color(new Vector4(0.72f, 0.92f, 0.90f, 1f)));
        ImGui.Dummy(new Vector2(22f, 20f));
        ImGui.SameLine();
        ImGui.SetNextItemWidth(260f);
        ImGui.ProgressBar(fraction, new Vector2(260f, 18f), $"{Assets.ScanCompleted}/{Assets.ScanTotal}");
    }

    private static void DrawSpinner(ImDrawListPtr drawList, Vector2 center, float radius, uint color)
    {
        var time = (float)ImGui.GetTime();
        const int segments = 10;
        for (var i = 0; i < segments; i++)
        {
            var t = i / (float)segments;
            var angle = time * 5.5f + t * MathF.Tau;
            var alpha = 0.18f + t * 0.82f;
            var col = FadeColor(color, alpha);
            var p1 = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * (radius * 0.55f);
            var p2 = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
            drawList.AddLine(p1, p2, col, 2.0f);
        }
    }

    private static uint FadeColor(uint color, float alpha)
    {
        var a = (uint)Math.Clamp((int)(alpha * 255f), 0, 255);
        return (color & 0x00FFFFFFu) | (a << 24);
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

    private static void StartLoadGame(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        var token = _scanCts.Token;
        _screen = AppScreen.Loading;
        _loadingPath = path;
        _loadError = "";
        _loadSucceeded = false;
        _selectedModel = null;
        _search = "";
        Thumbnails.Clear();

        _loadTask = Task.Run(() =>
        {
            try
            {
                Log($"Loading: {path}");
                if (!Assets.Mount(path, out var error))
                {
                    _loadError = $"Mount failed: {error}";
                    Log(_loadError);
                    return;
                }

                ConfigStore.Config.GameDirectory = Assets.GameDirectory;
                ConfigStore.Save();
                Log($"Mounted: {Assets.GameDirectory}");

                Assets.BuildModelIndex(token);
                if (token.IsCancellationRequested)
                {
                    _loadError = "Loading canceled.";
                    Log(_loadError);
                    return;
                }

                Log(Assets.Status);
                _loadSucceeded = true;
            }
            catch (Exception ex)
            {
                _loadError = $"Load failed: {ex.Message}";
                Log(_loadError);
            }
        }, token);
    }

    private static void StartScan()
    {
        if (!Assets.IsMounted || Assets.IsBusy) return;

        _scanCts?.Cancel();
        Thumbnails.Clear();
        _scanCts = new CancellationTokenSource();
        var token = _scanCts.Token;
        _scanTask = Task.Run(() =>
        {
            Log("Scanning models...");
            Assets.BuildModelIndex(token);
            Log(Assets.Status);
        }, token);
    }

    private static void Export(ModelItem model)
    {
        Directory.CreateDirectory(ConfigStore.Config.ExportDirectory);
        if (!string.Equals(_blenderPath.Trim(), ConfigStore.Config.BlenderPath, StringComparison.Ordinal))
            SaveBlenderPath(_blenderPath);
        else
            BlenderFbxConverter.BlenderPath = ConfigStore.Config.BlenderPath;

        var ok = Assets.ExportModel(model, ConfigStore.Config.ExportDirectory, _exportFormat, out _lastExport, out var error);

        if (ok)
        {
            Log($"Exported: {_lastExport}");
        }
        else
        {
            Log($"Export failed: {error}");
        }
    }

    private static void DrawExportFormatSelector()
    {
        var labels = new[] { "GLB (quick preview)", "ActorX PSK/PSKX (better skeleton)", "FBX via Blender (PSK -> FBX)" };
        var current = (int)_exportFormat;
        ImGui.SetNextItemWidth(260);
        if (ImGui.Combo("Export Format", ref current, labels, labels.Length))
            _exportFormat = (PakoMeshExportFormat)current;
    }

    private static string GetExportFormatLabel()
    {
        return _exportFormat switch
        {
            PakoMeshExportFormat.ActorX => "ActorX",
            PakoMeshExportFormat.Fbx => "FBX",
            _ => "GLB"
        };
    }

    private static uint Color(Vector4 color)
    {
        return ImGui.ColorConvertFloat4ToU32(color);
    }

    private static string Ellipsize(string text, float maxWidth, bool keepStart)
    {
        if (string.IsNullOrEmpty(text) || ImGui.CalcTextSize(text).X <= maxWidth)
            return text;

        const string ellipsis = "...";
        var lo = 0;
        var hi = text.Length;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            var candidate = keepStart
                ? text[..mid] + ellipsis
                : ellipsis + text[^mid..];
            if (ImGui.CalcTextSize(candidate).X <= maxWidth)
                lo = mid;
            else
                hi = mid - 1;
        }

        return keepStart
            ? text[..Math.Max(0, lo)] + ellipsis
            : ellipsis + text[^Math.Max(0, lo)..];
    }

    private static float NameHue(string name)
    {
        unchecked
        {
            var hash = 23;
            foreach (var c in name)
                hash = hash * 31 + c;
            return (Math.Abs(hash) % 360) / 360f;
        }
    }

    private static Vector4 HsvToRgb(float h, float s, float v)
    {
        var i = (int)MathF.Floor(h * 6f);
        var f = h * 6f - i;
        var p = v * (1f - s);
        var q = v * (1f - f * s);
        var t = v * (1f - (1f - f) * s);

        return (i % 6) switch
        {
            0 => new Vector4(v, t, p, 1f),
            1 => new Vector4(q, v, p, 1f),
            2 => new Vector4(p, v, t, 1f),
            3 => new Vector4(p, q, v, 1f),
            4 => new Vector4(t, p, v, 1f),
            _ => new Vector4(v, p, q, 1f)
        };
    }

    private static void BrowseFolder(Action<string> onSelected)
    {
        var thread = new Thread(() =>
        {
            using var dialog = new FolderBrowserDialog();
            if (dialog.ShowDialog() == DialogResult.OK)
                onSelected(dialog.SelectedPath);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private static void BrowseBlender(Action<string> onSelected)
    {
        var thread = new Thread(() =>
        {
            using var dialog = new OpenFileDialog
            {
                Filter = "Blender executable|blender.exe|Executable files (*.exe)|*.exe|All files (*.*)|*.*",
                Title = "Select blender.exe",
                CheckFileExists = true
            };

            if (dialog.ShowDialog() == DialogResult.OK)
                onSelected(dialog.FileName);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private static void SaveBlenderPath(string path)
    {
        _blenderPath = path.Trim();
        ConfigStore.Config.BlenderPath = _blenderPath;
        BlenderFbxConverter.BlenderPath = _blenderPath;
        ConfigStore.Save();
        Log(string.IsNullOrWhiteSpace(_blenderPath)
            ? "Blender path cleared; using auto-detect."
            : $"Blender path: {_blenderPath}");
    }

    private static void Log(string message)
    {
        lock (Logs)
        {
            Logs.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
        }
    }

    private static void ApplyStyle(bool dark)
    {
        if (dark)
        {
            ImGui.StyleColorsDark();
            var style = ImGui.GetStyle();
            style.WindowRounding = 0;
            style.ChildRounding = 4;
            style.FrameRounding = 3;
            style.Colors[(int)ImGuiCol.WindowBg] = new Vector4(0.055f, 0.058f, 0.064f, 1f);
            style.Colors[(int)ImGuiCol.ChildBg] = new Vector4(0.078f, 0.082f, 0.092f, 1f);
            style.Colors[(int)ImGuiCol.Header] = new Vector4(0.20f, 0.34f, 0.46f, 1f);
            style.Colors[(int)ImGuiCol.Button] = new Vector4(0.20f, 0.38f, 0.34f, 1f);
            style.Colors[(int)ImGuiCol.ButtonHovered] = new Vector4(0.26f, 0.48f, 0.43f, 1f);
        }
        else
        {
            ImGui.StyleColorsLight();
        }
    }
}
