using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using ImGuiNET;
using Veldrid;
using Vortice.D3DCompiler;
using Vortice.Direct3D;

namespace Pako;

public sealed class GPU_ModelRender : IDisposable
{
    private const uint TextureSize = 900;

    private readonly GraphicsDevice _graphicsDevice;
    private readonly ResourceFactory _factory;
    private readonly ImGuiController _imgui;

    private Texture _colorTexture = null!;
    private TextureView _colorView = null!;
    private Texture _depthTexture = null!;
    private Framebuffer _framebuffer = null!;
    private IntPtr _imguiBinding;

    private ResourceLayout _constantsLayout = null!;
    private ResourceLayout _textureLayout = null!;
    private DeviceBuffer _constantsBuffer = null!;
    private ResourceSet _constantsSet = null!;
    private Pipeline _pipeline = null!;
    private Pipeline _gridPipeline = null!;
    private Shader[] _shaders = Array.Empty<Shader>();
    private Texture _whiteTexture = null!;
    private TextureView _whiteTextureView = null!;
    private ResourceSet _whiteTextureSet = null!;

    private DeviceBuffer? _vertexBuffer;
    private DeviceBuffer? _indexBuffer;
    private DeviceBuffer? _gridBuffer;
    private uint[] _indices = Array.Empty<uint>();
    private uint _gridVertexCount;
    private readonly List<DrawBatch> _drawRanges = new();
    private readonly List<IDisposable> _sceneResources = new();
    private ModelClass? _scene;

    public GPU_ModelRender(GraphicsDevice graphicsDevice, ImGuiController imgui)
    {
        _graphicsDevice = graphicsDevice;
        _factory = graphicsDevice.ResourceFactory;
        _imgui = imgui;
        CreateResources();
    }

    public IntPtr ImGuiBinding => _imguiBinding;

    public void LoadScene(ModelClass scene)
    {
        ClearScene();
        _scene = scene;

        var vertices = new GpuVertex[scene.Positions.Length];
        for (var i = 0; i < vertices.Length; i++)
        {
            vertices[i] = new GpuVertex(
                scene.Positions[i],
                scene.Normals.Length > i ? scene.Normals[i] : Vector3.UnitY,
                scene.UVs.Length > i ? scene.UVs[i] : Vector2.Zero);
        }

        _indices = scene.Indices;
        _vertexBuffer = _factory.CreateBuffer(new BufferDescription((uint)(vertices.Length * Marshal.SizeOf<GpuVertex>()), BufferUsage.VertexBuffer));
        _indexBuffer = _factory.CreateBuffer(new BufferDescription((uint)(_indices.Length * sizeof(uint)), BufferUsage.IndexBuffer));
        _graphicsDevice.UpdateBuffer(_vertexBuffer, 0, vertices);
        _graphicsDevice.UpdateBuffer(_indexBuffer, 0, _indices);

        BuildDrawRanges(scene);
        BuildGrid(scene);
    }

    public void Render(
        CommandList commandList,
        float yaw,
        float pitch,
        float zoom,
        bool showGrid,
        bool useTextures,
        bool singleMaterial,
        float lightYaw,
        float lightPitch,
        float lightIntensity,
        float ambientLight)
    {
        if (_scene == null || _vertexBuffer == null || _indexBuffer == null || _indices.Length == 0)
            return;

        commandList.SetFramebuffer(_framebuffer);
        commandList.ClearColorTarget(0, new RgbaFloat(0.050f, 0.057f, 0.064f, 1f));
        commandList.ClearDepthStencil(1f);

        var aspect = 1f;
        var radius = MathF.Max(_scene.Radius, 1f);
        var distance = radius * Math.Clamp(2.85f / MathF.Max(zoom, 0.1f), 0.42f, 8f);
        var rotation = Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateRotationX(pitch);
        var target = _scene.Center;
        var cameraOffset = Vector3.Transform(new Vector3(0f, 0f, distance), rotation);
        var eye = target + cameraOffset;
        var up = Vector3.Transform(Vector3.UnitY, rotation);
        var view = Matrix4x4.CreateLookAt(eye, target, up);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(0.74f, aspect, MathF.Max(0.1f, radius * 0.001f), radius * 20f + distance);
        var world = Matrix4x4.Identity;
        var wvp = world * view * projection;
        var light = GetLightDirection(lightYaw, lightPitch);
        var lighting = new Vector2(Math.Clamp(ambientLight, 0f, 1f), Math.Clamp(lightIntensity, 0f, 3f));

        if (showGrid)
            DrawGrid(commandList, wvp, world, light, lighting);

        commandList.SetPipeline(_pipeline);
        commandList.SetVertexBuffer(0, _vertexBuffer);
        commandList.SetIndexBuffer(_indexBuffer, IndexFormat.UInt32);
        commandList.SetGraphicsResourceSet(0, _constantsSet);

        if (singleMaterial)
        {
            DrawRange(commandList, 0, (uint)_indices.Length, new Vector3(0.60f, 0.70f, 0.62f), _whiteTextureSet, false, wvp, world, light, lighting);
            return;
        }

        foreach (var range in _drawRanges)
        {
            var material = _scene.Materials[Math.Clamp(range.MaterialIndex, 0, _scene.Materials.Length - 1)];
            var textureSet = range.TextureSet ?? _whiteTextureSet;
            DrawRange(commandList, range.StartIndex, range.IndexCount, material.Color, textureSet, useTextures && range.HasTexture, wvp, world, light, lighting);
        }
    }

    private void DrawRange(
        CommandList commandList,
        uint startIndex,
        uint indexCount,
        Vector3 color,
        ResourceSet textureSet,
        bool useTexture,
        Matrix4x4 wvp,
        Matrix4x4 world,
        Vector3 light,
        Vector2 lighting)
    {
        var constants = new PreviewConstants
        {
            WorldViewProjection = Matrix4x4.Transpose(wvp),
            World = Matrix4x4.Transpose(world),
            MaterialColor = new Vector4(color, 1f),
            LightDirectionUseTexture = new Vector4(light, useTexture ? 1f : 0f),
            LightParams = new Vector4(lighting, 0f, 0f)
        };
        commandList.UpdateBuffer(_constantsBuffer, 0, ref constants);
        commandList.SetGraphicsResourceSet(1, textureSet);
        commandList.DrawIndexed(indexCount, 1, startIndex, 0, 0);
    }

    private void DrawGrid(CommandList commandList, Matrix4x4 wvp, Matrix4x4 world, Vector3 light, Vector2 lighting)
    {
        if (_gridBuffer == null || _gridVertexCount == 0)
            return;

        var constants = new PreviewConstants
        {
            WorldViewProjection = Matrix4x4.Transpose(wvp),
            World = Matrix4x4.Transpose(world),
            MaterialColor = new Vector4(0.20f, 0.28f, 0.32f, 1f),
            LightDirectionUseTexture = new Vector4(light, 0f),
            LightParams = new Vector4(lighting, 0f, 0f)
        };

        commandList.SetPipeline(_gridPipeline);
        commandList.SetVertexBuffer(0, _gridBuffer);
        commandList.SetGraphicsResourceSet(0, _constantsSet);
        commandList.SetGraphicsResourceSet(1, _whiteTextureSet);
        commandList.UpdateBuffer(_constantsBuffer, 0, ref constants);
        commandList.Draw(_gridVertexCount, 1, 0, 0);
    }

    private void BuildDrawRanges(ModelClass scene)
    {
        _drawRanges.Clear();
        var triCount = scene.Indices.Length / 3;
        if (triCount == 0)
            return;

        var startTri = 0;
        var currentMaterial = scene.TriangleMaterials.Length > 0 ? scene.TriangleMaterials[0] : 0;
        for (var tri = 1; tri <= triCount; tri++)
        {
            var material = tri < scene.TriangleMaterials.Length ? scene.TriangleMaterials[tri] : currentMaterial;
            if (tri < triCount && material == currentMaterial)
                continue;

            AddDrawRange(scene, currentMaterial, startTri, tri - startTri);
            startTri = tri;
            currentMaterial = material;
        }
    }

    private void AddDrawRange(ModelClass scene, int materialIndex, int startTriangle, int triangleCount)
    {
        if (triangleCount <= 0)
            return;

        ResourceSet? textureSet = null;
        var hasTexture = false;
        if ((uint)materialIndex < scene.Materials.Length && scene.Materials[materialIndex].Diffuse is { } diffuse)
        {
            textureSet = CreateTextureSet(diffuse);
            hasTexture = textureSet != null;
        }

        _drawRanges.Add(new DrawBatch(
            materialIndex,
            (uint)(startTriangle * 3),
            (uint)(triangleCount * 3),
            textureSet,
            hasTexture));
    }

    private void BuildGrid(ModelClass scene)
    {
        _gridBuffer?.Dispose();
        _gridBuffer = null;
        _gridVertexCount = 0;

        var radius = MathF.Max(scene.Radius, 1f);
        var halfSize = radius * 1.45f;
        var step = MathF.Max(radius / 8f, 1f);
        var lineCount = 16;
        var floorY = scene.Center.Y - radius * 0.92f;
        var vertices = new List<GpuVertex>((lineCount * 2 + 1) * 4);

        for (var i = -lineCount; i <= lineCount; i++)
        {
            var offset = i * step;
            vertices.Add(new GpuVertex(new Vector3(scene.Center.X - halfSize, floorY, scene.Center.Z + offset), Vector3.UnitY, Vector2.Zero));
            vertices.Add(new GpuVertex(new Vector3(scene.Center.X + halfSize, floorY, scene.Center.Z + offset), Vector3.UnitY, Vector2.Zero));
            vertices.Add(new GpuVertex(new Vector3(scene.Center.X + offset, floorY, scene.Center.Z - halfSize), Vector3.UnitY, Vector2.Zero));
            vertices.Add(new GpuVertex(new Vector3(scene.Center.X + offset, floorY, scene.Center.Z + halfSize), Vector3.UnitY, Vector2.Zero));
        }

        _gridVertexCount = (uint)vertices.Count;
        _gridBuffer = _factory.CreateBuffer(new BufferDescription((uint)(vertices.Count * Marshal.SizeOf<GpuVertex>()), BufferUsage.VertexBuffer));
        _graphicsDevice.UpdateBuffer(_gridBuffer, 0, vertices.ToArray());
    }

    private static Vector3 GetLightDirection(float yaw, float pitch)
    {
        var horizontal = MathF.Cos(pitch);
        return Vector3.Normalize(new Vector3(
            MathF.Sin(yaw) * horizontal,
            MathF.Sin(pitch),
            MathF.Cos(yaw) * horizontal));
    }

    private ResourceSet? CreateTextureSet(PreviewTexture texture)
    {
        try
        {
            var gpuTexture = _factory.CreateTexture(TextureDescription.Texture2D(
                (uint)texture.Width,
                (uint)texture.Height,
                1,
                1,
                PixelFormat.R8_G8_B8_A8_UNorm,
                TextureUsage.Sampled));
            _graphicsDevice.UpdateTexture(gpuTexture, texture.Rgba, 0, 0, 0, (uint)texture.Width, (uint)texture.Height, 1, 0, 0);
            var view = _factory.CreateTextureView(gpuTexture);
            var set = _factory.CreateResourceSet(new ResourceSetDescription(_textureLayout, view, _graphicsDevice.LinearSampler));
            _sceneResources.Add(set);
            _sceneResources.Add(view);
            _sceneResources.Add(gpuTexture);
            return set;
        }
        catch
        {
            return null;
        }
    }

    private void CreateResources()
    {
        _colorTexture = _factory.CreateTexture(TextureDescription.Texture2D(
            TextureSize,
            TextureSize,
            1,
            1,
            PixelFormat.R8_G8_B8_A8_UNorm,
            TextureUsage.RenderTarget | TextureUsage.Sampled));
        _colorView = _factory.CreateTextureView(_colorTexture);
        _depthTexture = _factory.CreateTexture(TextureDescription.Texture2D(
            TextureSize,
            TextureSize,
            1,
            1,
            PixelFormat.D32_Float_S8_UInt,
            TextureUsage.DepthStencil));
        _framebuffer = _factory.CreateFramebuffer(new FramebufferDescription(_depthTexture, _colorTexture));
        _imguiBinding = _imgui.GetOrCreateImGuiBinding(_factory, _colorView);

        _constantsBuffer = _factory.CreateBuffer(new BufferDescription((uint)Marshal.SizeOf<PreviewConstants>(), BufferUsage.UniformBuffer | BufferUsage.Dynamic));
        _constantsLayout = _factory.CreateResourceLayout(new ResourceLayoutDescription(
            new ResourceLayoutElementDescription("PreviewConstants", ResourceKind.UniformBuffer, ShaderStages.Vertex | ShaderStages.Fragment)));
        _textureLayout = _factory.CreateResourceLayout(new ResourceLayoutDescription(
            new ResourceLayoutElementDescription("DiffuseTexture", ResourceKind.TextureReadOnly, ShaderStages.Fragment),
            new ResourceLayoutElementDescription("DiffuseSampler", ResourceKind.Sampler, ShaderStages.Fragment)));
        _constantsSet = _factory.CreateResourceSet(new ResourceSetDescription(_constantsLayout, _constantsBuffer));

        _shaders = CreateShaders();
        var vertexLayout = new VertexLayoutDescription(
            new VertexElementDescription("Position", VertexElementSemantic.Position, VertexElementFormat.Float3),
            new VertexElementDescription("Normal", VertexElementSemantic.Normal, VertexElementFormat.Float3),
            new VertexElementDescription("TexCoord", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float2));

        var pipelineDescription = new GraphicsPipelineDescription(
            BlendStateDescription.SingleOverrideBlend,
            new DepthStencilStateDescription(true, true, ComparisonKind.LessEqual),
            new RasterizerStateDescription(FaceCullMode.None, PolygonFillMode.Solid, FrontFace.Clockwise, true, false),
            Veldrid.PrimitiveTopology.TriangleList,
            new ShaderSetDescription(new[] { vertexLayout }, _shaders),
            new[] { _constantsLayout, _textureLayout },
            _framebuffer.OutputDescription,
            ResourceBindingModel.Default);
        _pipeline = _factory.CreateGraphicsPipeline(ref pipelineDescription);

        var gridPipelineDescription = new GraphicsPipelineDescription(
            BlendStateDescription.SingleAlphaBlend,
            new DepthStencilStateDescription(true, false, ComparisonKind.LessEqual),
            new RasterizerStateDescription(FaceCullMode.None, PolygonFillMode.Solid, FrontFace.Clockwise, true, false),
            Veldrid.PrimitiveTopology.LineList,
            new ShaderSetDescription(new[] { vertexLayout }, _shaders),
            new[] { _constantsLayout, _textureLayout },
            _framebuffer.OutputDescription,
            ResourceBindingModel.Default);
        _gridPipeline = _factory.CreateGraphicsPipeline(ref gridPipelineDescription);

        _whiteTexture = _factory.CreateTexture(TextureDescription.Texture2D(1, 1, 1, 1, PixelFormat.R8_G8_B8_A8_UNorm, TextureUsage.Sampled));
        _graphicsDevice.UpdateTexture(_whiteTexture, new byte[] { 255, 255, 255, 255 }, 0, 0, 0, 1, 1, 1, 0, 0);
        _whiteTextureView = _factory.CreateTextureView(_whiteTexture);
        _whiteTextureSet = _factory.CreateResourceSet(new ResourceSetDescription(_textureLayout, _whiteTextureView, _graphicsDevice.LinearSampler));
    }

    private Shader[] CreateShaders()
    {
        if (_graphicsDevice.BackendType != GraphicsBackend.Direct3D11)
            throw new NotSupportedException("GPU model preview currently supports Direct3D11 only.");

        var vertexBytes = CompileHlsl(ShaderSource, "VS", "vs_5_0");
        var fragmentBytes = CompileHlsl(ShaderSource, "FS", "ps_5_0");
        return
        [
            _factory.CreateShader(new ShaderDescription(ShaderStages.Vertex, vertexBytes, "VS")),
            _factory.CreateShader(new ShaderDescription(ShaderStages.Fragment, fragmentBytes, "FS"))
        ];
    }

    private static unsafe byte[] CompileHlsl(string source, string entryPoint, string profile)
    {
        var bytes = Encoding.UTF8.GetBytes(source);
        fixed (byte* sourcePtr = bytes)
        {
            var result = Compiler.Compile(
                sourcePtr,
                bytes.Length,
                "GpuModelPreview.hlsl",
                null,
                null,
                entryPoint,
                profile,
                ShaderFlags.OptimizationLevel3,
                EffectFlags.None,
                out Blob code,
                out Blob errors);

            using (code)
            using (errors)
            {
                if (result.Failure)
                {
                    var message = errors == null ? "Unknown shader compile error." : Marshal.PtrToStringAnsi(errors.BufferPointer) ?? "Unknown shader compile error.";
                    throw new InvalidOperationException(message);
                }

                var compiled = new byte[(int)code.BufferSize];
                Marshal.Copy(code.BufferPointer, compiled, 0, compiled.Length);
                return compiled;
            }
        }
    }

    private void ClearScene()
    {
        _vertexBuffer?.Dispose();
        _indexBuffer?.Dispose();
        _gridBuffer?.Dispose();
        _vertexBuffer = null;
        _indexBuffer = null;
        _gridBuffer = null;
        _gridVertexCount = 0;
        foreach (var resource in _sceneResources)
            resource.Dispose();
        _sceneResources.Clear();
    }

    public void Dispose()
    {
        ClearScene();
        _whiteTextureSet.Dispose();
        _whiteTextureView.Dispose();
        _whiteTexture.Dispose();
        _pipeline.Dispose();
        _gridPipeline.Dispose();
        foreach (var shader in _shaders)
            shader.Dispose();
        _constantsSet.Dispose();
        _textureLayout.Dispose();
        _constantsLayout.Dispose();
        _constantsBuffer.Dispose();
        _framebuffer.Dispose();
        _depthTexture.Dispose();
        _colorView.Dispose();
        _colorTexture.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct GpuVertex(Vector3 Position, Vector3 Normal, Vector2 TexCoord);

    [StructLayout(LayoutKind.Sequential)]
    private struct PreviewConstants
    {
        public Matrix4x4 WorldViewProjection;
        public Matrix4x4 World;
        public Vector4 MaterialColor;
        public Vector4 LightDirectionUseTexture;
        public Vector4 LightParams;
    }

    private sealed record DrawBatch(int MaterialIndex, uint StartIndex, uint IndexCount, ResourceSet? TextureSet, bool HasTexture);

    private const string ShaderSource = """
cbuffer PreviewConstants : register(b0)
{
    float4x4 WorldViewProjection;
    float4x4 World;
    float4 MaterialColor;
    float4 LightDirectionUseTexture;
    float4 LightParams;
};

Texture2D DiffuseTexture : register(t0);
SamplerState DiffuseSampler : register(s0);

struct VSInput
{
    float3 Position : POSITION;
    float3 Normal : NORMAL;
    float2 TexCoord : TEXCOORD0;
};

struct VSOutput
{
    float4 Position : SV_Position;
    float3 Normal : NORMAL;
    float2 TexCoord : TEXCOORD0;
};

VSOutput VS(VSInput input)
{
    VSOutput output;
    output.Position = mul(float4(input.Position, 1.0), WorldViewProjection);
    output.Normal = normalize(mul(float4(input.Normal, 0.0), World).xyz);
    output.TexCoord = input.TexCoord;
    return output;
}

float4 FS(VSOutput input) : SV_Target
{
    float3 normal = normalize(input.Normal);
    float3 light = normalize(LightDirectionUseTexture.xyz);
    float ndotl = saturate(dot(normal, light));
    float shade = saturate(LightParams.x + ndotl * LightParams.y);
    float3 color = MaterialColor.rgb;
    if (LightDirectionUseTexture.w > 0.5)
    {
        color *= DiffuseTexture.Sample(DiffuseSampler, input.TexCoord).rgb;
    }
    return float4(color * shade, 1.0);
}
""";
}
