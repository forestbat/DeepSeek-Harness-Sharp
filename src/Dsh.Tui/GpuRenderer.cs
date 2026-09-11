using OpenTK.Graphics.OpenGL;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Dsh.Tui;

public sealed class GpuRenderer : IDisposable
{
    private const int CellPixelWidth = 16;
    private const int CellPixelHeight = 20;
    private const int FloatsPerVertex = 9;
    private const int VerticesPerQuad = 6;

    private readonly GlyphAtlas _atlas = new();
    private readonly ChatWindow _chat;
    private readonly GameWindow _window;
    private CellGrid _grid;
    private UiLayout _layout;
    private CellGrid? _lastGrid;
    private int _vao;
    private int _vbo;
    private int _shader;
    private int _texture;
    private int _vertexCount;
    private float _mouseX;
    private float _mouseY;
    private bool _disposed;
    private readonly string? _screenshotPath = Environment.GetEnvironmentVariable("DSH_GPU_SCREENSHOT");
    private bool _screenshotTaken;

    public GpuRenderer(ChatWindow chat)
    {
        ArgumentNullException.ThrowIfNull(chat);
        _chat = chat;
        var settings = new NativeWindowSettings
        {
            ClientSize = new Vector2i(80 * CellPixelWidth, 25 * CellPixelHeight),
            Title = "dsh --gpu",
            API = ContextAPI.OpenGL,
            Profile = ContextProfile.Core,
            APIVersion = new Version(3, 3),
        };
        _window = new GameWindow(GameWindowSettings.Default, settings);
        _grid = new CellGrid(80, 25);
        _layout = LayoutEngine.Calculate(_grid.Width, _grid.Height);
        _window.Load += OnLoad;
        _window.Resize += OnResize;
        _window.RenderFrame += OnRenderFrame;
        _window.KeyDown += OnKeyDown;
        _window.TextInput += OnTextInput;
        _window.MouseMove += OnMouseMove;
        _window.MouseDown += OnMouseDown;
        _window.MouseWheel += OnMouseWheel;
    }

    public void Run()
        => _window.Run();

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_shader != 0)
            GL.DeleteProgram(_shader);
        if (_texture != 0)
            GL.DeleteTexture(_texture);
        if (_vao != 0)
            GL.DeleteVertexArray(_vao);
        if (_vbo != 0)
            GL.DeleteBuffer(_vbo);
        _window.Dispose();
    }

    private void OnLoad()
    {
        GL.ClearColor(0f, 0f, 0f, 1f);
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        _shader = CreateShader();
        _texture = CreateTexture();

        _vao = GL.GenVertexArray();
        _vbo = GL.GenBuffer();
        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);

        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, FloatsPerVertex * sizeof(float), 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, FloatsPerVertex * sizeof(float), 2 * sizeof(float));
        GL.EnableVertexAttribArray(2);
        GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, FloatsPerVertex * sizeof(float), 6 * sizeof(float));
        GL.EnableVertexAttribArray(3);
        GL.VertexAttribPointer(3, 1, VertexAttribPointerType.Float, false, FloatsPerVertex * sizeof(float), 8 * sizeof(float));

        GL.BindVertexArray(0);
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
    }

    private void OnResize(ResizeEventArgs e)
    {
        var framebufferSize = _window.FramebufferSize;
        var width = Math.Max(1, framebufferSize.X);
        var height = Math.Max(1, framebufferSize.Y);
        GL.Viewport(0, 0, width, height);
        var gridWidth = Math.Max(1, width / CellPixelWidth);
        var gridHeight = Math.Max(1, height / CellPixelHeight);
        if (_grid.Width != gridWidth || _grid.Height != gridHeight)
        {
            _grid = new CellGrid(gridWidth, gridHeight);
            _layout = LayoutEngine.Calculate(gridWidth, gridHeight);
        }
    }

    private void OnRenderFrame(FrameEventArgs e)
    {
        _chat.DrainUi();
        if (_chat.ExitRequested)
        {
            _window.Close();
            return;
        }

        _chat.Draw(_grid, _layout);
        var changed = _lastGrid is null
            || _lastGrid.Width != _grid.Width
            || _lastGrid.Height != _grid.Height
            || _grid.Diff(_lastGrid).Any();
        if (changed)
        {
            var quads = CellQuadBuilder.Build(_grid);
            var vertices = BuildVertices(quads);
            _vertexCount = vertices.Length / FloatsPerVertex;
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * sizeof(float), vertices, BufferUsage.DynamicDraw);
        }

        _lastGrid = _grid.Clone();

        GL.Clear(ClearBufferMask.ColorBufferBit);
        GL.UseProgram(_shader);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, _texture);
        GL.Uniform1i(GL.GetUniformLocation(_shader, "uTexture"), 0);
        GL.Uniform2f(GL.GetUniformLocation(_shader, "uGridSize"), _grid.Width, _grid.Height);

        GL.BindVertexArray(_vao);
        GL.DrawArrays(PrimitiveType.Triangles, 0, _vertexCount);
        GL.BindVertexArray(0);

        if (!_screenshotTaken && _screenshotPath is not null)
        {
            SaveScreenshot(_screenshotPath);
            _screenshotTaken = true;
            _chat.RequestExit();
        }

        _window.SwapBuffers();
    }

    private void OnKeyDown(KeyboardKeyEventArgs e)
    {
        if (e.Control && e.Key == Keys.Q)
        {
            _chat.RequestExit();
            _window.Close();
            return;
        }

        if (e.Control && e.Key == Keys.V)
        {
            var clipboard = _window.ClipboardString;
            if (clipboard.Length > 0)
                _chat.InsertText(clipboard);
            return;
        }

        if (!TryMapKey(e.Key, out var consoleKey))
            return;

        _chat.HandleKey(new ConsoleKeyInfo('\0', consoleKey, e.Shift, e.Alt, e.Control));
    }

    private void OnTextInput(TextInputEventArgs e)
    {
        var text = e.AsString;
        if (text.Length == 0)
            return;
        _chat.HandleKey(new ConsoleKeyInfo(text[0], ConsoleKey.NoName, false, false, false));
    }

    private void OnMouseMove(MouseMoveEventArgs e)
    {
        _mouseX = e.X;
        _mouseY = e.Y;
        var cellX = (int)(_mouseX / CellPixelWidth);
        var cellY = (int)(_mouseY / CellPixelHeight);
        _chat.HandleMouseMove(cellX, cellY, _layout);
    }

    private void OnMouseDown(MouseButtonEventArgs e)
    {
        if (e.Button != MouseButton.Left || !e.IsPressed)
            return;
        var cellX = (int)(_mouseX / CellPixelWidth);
        var cellY = (int)(_mouseY / CellPixelHeight);
        _chat.HandleMouseClick(cellX, cellY, _layout);
    }

    private void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (e.OffsetY != 0)
            _chat.HandleMouseWheel((int)e.OffsetY);
    }

    private void SaveScreenshot(string path)
    {
        var size = _window.FramebufferSize;
        if (size.X <= 0 || size.Y <= 0)
            return;
        var pixels = new byte[size.X * size.Y * 4];
        GL.ReadPixels(0, 0, size.X, size.Y, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
        using var image = new Image<Rgba32>(size.X, size.Y);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                var sourceY = accessor.Height - 1 - y;
                for (var x = 0; x < accessor.Width; x++)
                {
                    var source = ((sourceY * accessor.Width) + x) * 4;
                    row[x] = new Rgba32(pixels[source], pixels[source + 1], pixels[source + 2], pixels[source + 3]);
                }
            }
        });
        if (path.EndsWith(".tif", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".tiff", StringComparison.OrdinalIgnoreCase))
            image.SaveAsTiff(path);
        else
            image.SaveAsPng(path);
    }
    

    private int CreateTexture()
    {
        var texture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, texture);
        var data = _atlas.CreateTextureData();
        GL.TexImage2D(
            TextureTarget.Texture2D,
            0,
            InternalFormat.R8,
            GlyphAtlas.Columns * GlyphAtlas.GlyphWidth,
            GlyphAtlas.Rows * GlyphAtlas.GlyphHeight,
            0,
            PixelFormat.Red,
            PixelType.UnsignedByte,
            data);
        GL.TexParameteri(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameteri(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.TexParameteri(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameteri(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.BindTexture(TextureTarget.Texture2D, 0);
        return texture;
    }

    private static int CreateShader()
    {
        const string vertexSource = """
            #version 330 core
            layout(location = 0) in vec2 aPosition;
            layout(location = 1) in vec4 aColor;
            layout(location = 2) in vec2 aUv;
            layout(location = 3) in float aTexEnabled;
            uniform vec2 uGridSize;
            out vec4 vColor;
            out vec2 vUv;
            out float vTexEnabled;
            void main()
            {
                vec2 ndc = vec2(
                    aPosition.x / uGridSize.x * 2.0 - 1.0,
                    1.0 - aPosition.y / uGridSize.y * 2.0);
                gl_Position = vec4(ndc, 0.0, 1.0);
                vColor = aColor;
                vUv = aUv;
                vTexEnabled = aTexEnabled;
            }
            """;

        const string fragmentSource = """
            #version 330 core
            in vec4 vColor;
            in vec2 vUv;
            in float vTexEnabled;
            uniform sampler2D uTexture;
            out vec4 FragColor;
            void main()
            {
                if (vTexEnabled > 0.5)
                {
                    float alpha = texture(uTexture, vUv).r;
                    FragColor = vec4(vColor.rgb, alpha);
                }
                else
                {
                    FragColor = vColor;
                }
            }
            """;

        var vertexShader = GL.CreateShader(ShaderType.VertexShader);
        GL.ShaderSource(vertexShader, vertexSource);
        GL.CompileShader(vertexShader);
        GL.GetShaderi(vertexShader, ShaderParameterName.CompileStatus, out var vertexStatus);
        if (vertexStatus == 0)
            throw new InvalidOperationException($"Vertex shader compile error: {GL.GetShaderInfoLog(vertexShader)}");

        var fragmentShader = GL.CreateShader(ShaderType.FragmentShader);
        GL.ShaderSource(fragmentShader, fragmentSource);
        GL.CompileShader(fragmentShader);
        GL.GetShaderi(fragmentShader, ShaderParameterName.CompileStatus, out var fragmentStatus);
        if (fragmentStatus == 0)
            throw new InvalidOperationException($"Fragment shader compile error: {GL.GetShaderInfoLog(fragmentShader)}");

        var program = GL.CreateProgram();
        GL.AttachShader(program, vertexShader);
        GL.AttachShader(program, fragmentShader);
        GL.LinkProgram(program);
        GL.GetProgrami(program, ProgramProperty.LinkStatus, out var linkStatus);
        if (linkStatus == 0)
            throw new InvalidOperationException($"Shader link error: {GL.GetProgramInfoLog(program)}");

        GL.DetachShader(program, vertexShader);
        GL.DetachShader(program, fragmentShader);
        GL.DeleteShader(vertexShader);
        GL.DeleteShader(fragmentShader);
        return program;
    }

    private static float[] BuildVertices(IReadOnlyList<CellQuad> quads)
    {
        var vertices = new float[quads.Count * VerticesPerQuad * FloatsPerVertex];
        var offset = 0;

        foreach (var quad in quads)
        {
            var x0 = quad.X;
            var y0 = quad.Y;
            var x1 = quad.X + quad.Width;
            var y1 = quad.Y + quad.Height;
            var u0 = quad.U0;
            var v0 = 1f - quad.V1;
            var u1 = quad.U1;
            var v1 = 1f - quad.V0;

            AddVertex(vertices, ref offset, x0, y0, u0, v0, quad);
            AddVertex(vertices, ref offset, x1, y0, u1, v0, quad);
            AddVertex(vertices, ref offset, x0, y1, u0, v1, quad);
            AddVertex(vertices, ref offset, x0, y1, u0, v1, quad);
            AddVertex(vertices, ref offset, x1, y0, u1, v0, quad);
            AddVertex(vertices, ref offset, x1, y1, u1, v1, quad);
        }

        return vertices;
    }

    private static void AddVertex(float[] vertices, ref int offset, float x, float y, float u, float v, CellQuad quad)
    {
        vertices[offset++] = x;
        vertices[offset++] = y;
        vertices[offset++] = quad.Color.R;
        vertices[offset++] = quad.Color.G;
        vertices[offset++] = quad.Color.B;
        vertices[offset++] = quad.Color.A;
        vertices[offset++] = u;
        vertices[offset++] = v;
        vertices[offset++] = quad.IsGlyph ? 1f : 0f;
    }

    private static bool TryMapKey(Keys key, out ConsoleKey consoleKey)
    {
        if (key is >= Keys.A and <= Keys.Z)
        {
            consoleKey = ConsoleKey.A + (key - Keys.A);
            return true;
        }

        if (key is >= Keys.D0 and <= Keys.D9)
        {
            consoleKey = ConsoleKey.D0 + (key - Keys.D0);
            return true;
        }

        switch (key)
        {
            case Keys.Enter:
                consoleKey = ConsoleKey.Enter;
                return true;
            case Keys.Escape:
                consoleKey = ConsoleKey.Escape;
                return true;
            case Keys.Tab:
                consoleKey = ConsoleKey.Tab;
                return true;
            case Keys.Backspace:
                consoleKey = ConsoleKey.Backspace;
                return true;
            case Keys.Delete:
                consoleKey = ConsoleKey.Delete;
                return true;
            case Keys.Up:
                consoleKey = ConsoleKey.UpArrow;
                return true;
            case Keys.Down:
                consoleKey = ConsoleKey.DownArrow;
                return true;
            case Keys.Left:
                consoleKey = ConsoleKey.LeftArrow;
                return true;
            case Keys.Right:
                consoleKey = ConsoleKey.RightArrow;
                return true;
            case Keys.Home:
                consoleKey = ConsoleKey.Home;
                return true;
            case Keys.End:
                consoleKey = ConsoleKey.End;
                return true;
            case Keys.PageUp:
                consoleKey = ConsoleKey.PageUp;
                return true;
            case Keys.PageDown:
                consoleKey = ConsoleKey.PageDown;
                return true;
            case Keys.Space:
                consoleKey = ConsoleKey.Spacebar;
                return true;
            default:
                consoleKey = default;
                return false;
        }
    }
}
