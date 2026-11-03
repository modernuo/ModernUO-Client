using System.Runtime.InteropServices;
using Hexa.NET.ImGui;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ModernUO.Studio.Assets;
using ModernUO.Studio.Gui;
using ModernUO.Studio.Panels;

namespace ModernUO.Studio
{
    /// <summary>The Studio window: an FNA game whose whole frame is a docking Dear ImGui UI.</summary>
    internal enum StudioLayout
    {
        Browse,
        Map
    }

    internal sealed class StudioGame : Game
    {
        private readonly GraphicsDeviceManager _graphics;
        private readonly StudioSettings _settings;
        private readonly string _startupPack;
        private ImGuiRenderer _renderer;
        private ImGuiPlatform _platform;
        private Texture2D _logo20, _logo40;
        private StudioContext _context;
        private StartPanel _start;
        private IntPtr _iniPath;
        private bool _buildLayout;
        private bool _showDemo;
        private bool _showMetrics;
        private bool _showAbout;
        private readonly string _screenshot;
        private int _frames;

        /// <summary>-focus: the View menu name (or its start) of a panel to bring to the front at startup.</summary>
        public string Focus { get; init; }

        /// <summary>-layout browse|map: the workspace to build when there is no saved layout (or with -resetlayout).</summary>
        public StudioLayout Layout { get; set; } = StudioLayout.Browse;

        /// <summary>The window brought to the front once a new layout has docked.</summary>
        private string _focusAfterLayout;

        /// <summary>-frames: the frames drawn before -screenshot saves (worker builds such as the radar need longer).</summary>
        public int ScreenshotFrames { get; init; } = 90;

        /// <summary>-goto "x y": the map centres on and picks this tile at startup.</summary>
        public string GoTo { get; init; }

        /// <summary>-select: an id the focused panel selects at startup.</summary>
        public string Select { get; init; }

        /// <param name="screenshot">-screenshot: save the frame drawn after a short warm-up as a PNG and exit (smoke tests)</param>
        public StudioGame(StudioSettings settings, string startupPack, string screenshot = null)
        {
            _settings = settings;
            _startupPack = startupPack;
            _screenshot = screenshot;
            _graphics = new GraphicsDeviceManager(this)
            {
                PreferredBackBufferWidth = Math.Max(800, settings.WindowWidth),
                PreferredBackBufferHeight = Math.Max(600, settings.WindowHeight),
                SynchronizeWithVerticalRetrace = true,
                PreferredDepthStencilFormat = DepthFormat.None
            };

            IsMouseVisible = true;
            IsFixedTimeStep = false;
            Window.AllowUserResizing = true;
            Window.Title = "ModernUO Studio";
            Window.ClientSizeChanged += OnClientSizeChanged;
        }

        private void OnClientSizeChanged(object sender, EventArgs e)
        {
            var bounds = Window.ClientBounds;

            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                return;
            }

            _graphics.PreferredBackBufferWidth = bounds.Width;
            _graphics.PreferredBackBufferHeight = bounds.Height;
            _graphics.ApplyChanges();
        }

        protected override void Initialize()
        {
            ImGui.CreateContext();
            var io = ImGui.GetIO();
            // no keyboard nav: the browsers own the arrow keys (selection), and ImGui's nav would move focus with them too
            io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;
            io.ConfigWindowsMoveFromTitleBarOnly = true;

            string ini = Path.Combine(AppContext.BaseDirectory, "studio_layout.ini");
            _buildLayout = !File.Exists(ini);
            _iniPath = Marshal.StringToCoTaskMemUTF8(ini);

            unsafe
            {
                io.IniFilename = (byte*)_iniPath;
            }

            Theme.Apply(_settings.UiScale);
            Theme.LoadFonts(_settings.UiScale);

            _renderer = new ImGuiRenderer(GraphicsDevice);
            LoadLogo();
            _platform = new ImGuiPlatform(Window.Handle);
            _context = new StudioContext(GraphicsDevice, _renderer, _settings);

            _start = new StartPanel(_context, Window.Handle);
            _context.Add(_start);
            _context.Add(new ArtBrowser(_context));
            _context.Add(new GumpBrowser(_context));
            _context.Add(new HueBrowser(_context));
            _context.Add(new TileDataBrowser(_context));
            _context.Add(new AnimationBrowser(_context));
            _context.Add(new MultiBrowser(_context));
            _context.Add(new TexmapBrowser(_context));
            _context.Add(new SoundBrowser(_context));
            _context.Add(new ClilocBrowser(_context));
            _context.Add(new MapPanel(_context));
            _context.Add(new TileInspectorPanel(_context));
            _context.Add(new RadarPanel(_context));
            _context.Add(new PackInfoPanel(_context));
            _context.Add(new LogPanel(_context));

            string driver = ClassicUO.Renderer.GpuInterop.GetFna3dDriverName(GraphicsDevice);
            StudioLog.Info($"ModernUO Studio, FNA3D driver {driver}");

            base.Initialize();
        }

        protected override void LoadContent()
        {
            base.LoadContent();

            if (!string.IsNullOrEmpty(_startupPack))
            {
                _context.OpenPack(_startupPack);
            }
        }

        protected override void Update(GameTime gameTime)
        {
            _context.Assets?.Update(2.0);
            base.Update(gameTime);
        }

        protected override void Draw(GameTime gameTime)
        {
            int width = GraphicsDevice.PresentationParameters.BackBufferWidth;
            int height = GraphicsDevice.PresentationParameters.BackBufferHeight;

            _platform.NewFrame((float)gameTime.ElapsedGameTime.TotalSeconds, width, height);
            ImGui.NewFrame();

            DrawUi();

            ImGui.Render();

            // the map's world pass submits its own command buffer before FNA's draws that sample its target (as the client)
            _context.RenderWorld();

            GraphicsDevice.SetRenderTarget(null);
            GraphicsDevice.Clear(new Color(27, 28, 31));
            _renderer.Render(ImGui.GetDrawData());

            // the warm-up covers the first-run dock layout and the worker decodes of what the browsers show first
            _frames++;

            if (_screenshot != null && _frames == ScreenshotFrames)
            {
                SaveScreenshot(_screenshot, width, height);
                Exit();
            }

            base.Draw(gameTime);
        }

        private void SaveScreenshot(string path, int width, int height)
        {
            var pixels = new Color[width * height];
            GraphicsDevice.GetBackBufferData(pixels);

            using var texture = new Texture2D(GraphicsDevice, width, height, false, SurfaceFormat.Color);
            texture.SetData(pixels);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

            using var stream = File.Create(path);
            texture.SaveAsPng(stream, width, height);
            StudioLog.Info($"Screenshot saved: {path}");
        }

        private void DrawUi()
        {
            // the bars first: they take their strips of the viewport before the dock space fills the rest
            DrawTopBar();
            DrawStatusBar();
            uint dock = ImGui.DockSpaceOverViewport();

            if (_buildLayout)
            {
                _buildLayout = false;
                BuildLayout(dock, Layout);
            }

            // -focus: keep asking for the panel until the layout has settled (the first frames dock the windows)
            if (Focus != null && _frames < 10)
            {
                var panel = _context.Panels.FirstOrDefault(p => p.MenuName.StartsWith(Focus, StringComparison.OrdinalIgnoreCase));
                panel?.Focus();

                if (panel != null && _frames == 0 && Widgets.TryParseId(Select, out int id))
                {
                    panel.SelectById(id);
                }
            }

            if (GoTo != null && _frames == 1 && _context.Assets != null)
            {
                var parts = GoTo.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                var map = _context.Get<MapPanel>();

                if (map != null && parts.Length >= 2 && Widgets.TryParseId(parts[0], out int gx) && Widgets.TryParseId(parts[1], out int gy))
                {
                    map.CenterOn(map.Facet < 0 ? _context.Assets.Manifest.Facets[0].Index : map.Facet, gx, gy, select: true);
                }
            }

            foreach (var panel in _context.Panels)
            {
                panel.Draw();
            }

            // a new layout docks its windows this frame; the front tab is chosen on the next one
            if (_focusAfterLayout != null && _frames > 0)
            {
                ImGui.SetWindowFocus(_focusAfterLayout);
                _focusAfterLayout = null;
            }

            if (_showDemo)
            {
                ImGui.ShowDemoWindow(ref _showDemo);
            }

            if (_showMetrics)
            {
                ImGui.ShowMetricsWindow(ref _showMetrics);
            }

            if (_showAbout)
            {
                DrawAbout();
            }
        }

        private static readonly string[] _browserWindows =
        {
            "Start###start", "Art###art", "Gumps###gumps", "Hues###hues", "TileData###tiledata", "Animations###anims", "Multis###multis",
            "Texmaps###texmaps", "Sounds###sounds", "Cliloc###cliloc", "Pack Info###packinfo"
        };

        /// <summary>
        /// The workspaces of studio/UX.md. Browse: the browsers as tabs in the middle, the map among them, the log under
        /// them. Map: the map in the middle, the tile inspector and radar on the right, the art, tiledata and hue browsers
        /// as palettes on the left, the log under the map.
        /// </summary>
        private unsafe void BuildLayout(uint dock, StudioLayout layout)
        {
            ImGuiP.DockBuilderRemoveNode(dock);
            ImGuiP.DockBuilderAddNode(dock, ImGuiDockNodeFlags.PassthruCentralNode | (ImGuiDockNodeFlags)ImGuiDockNodeFlagsPrivate.Space);
            ImGuiP.DockBuilderSetNodeSize(dock, ImGui.GetMainViewport().Size);

            uint main = dock, bottom;
            ImGuiP.DockBuilderSplitNode(main, ImGuiDir.Down, 0.2f, &bottom, &main);
            ImGuiP.DockBuilderDockWindow("Log###log", bottom);

            if (layout == StudioLayout.Map)
            {
                uint right, rightBottom, left;
                ImGuiP.DockBuilderSplitNode(main, ImGuiDir.Right, 0.24f, &right, &main);
                ImGuiP.DockBuilderSplitNode(right, ImGuiDir.Down, 0.42f, &rightBottom, &right);
                ImGuiP.DockBuilderSplitNode(main, ImGuiDir.Left, 0.28f, &left, &main);

                foreach (string window in _browserWindows)
                {
                    bool palette = window is "Art###art" or "TileData###tiledata" or "Hues###hues" or "Multis###multis";
                    ImGuiP.DockBuilderDockWindow(window, palette ? left : main);
                }

                ImGuiP.DockBuilderDockWindow("Map###map", main);
                ImGuiP.DockBuilderDockWindow("Tile###tile", right);
                ImGuiP.DockBuilderDockWindow("Radar###radar", rightBottom);
                _focusAfterLayout = "Map###map";
            }
            else
            {
                uint right;
                ImGuiP.DockBuilderSplitNode(main, ImGuiDir.Right, 0.22f, &right, &main);

                foreach (string window in _browserWindows)
                {
                    ImGuiP.DockBuilderDockWindow(window, main);
                }

                ImGuiP.DockBuilderDockWindow("Map###map", main);
                ImGuiP.DockBuilderDockWindow("Tile###tile", right);
                ImGuiP.DockBuilderDockWindow("Radar###radar", right);
                _focusAfterLayout = "Start###start";
            }

            ImGuiP.DockBuilderFinish(dock);
        }

        /// <summary>The top bar: the ModernUO mark, the menus, the workspace switch and the open pack.</summary>
        private void DrawTopBar()
        {
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new System.Numerics.Vector2(10f, 10f));
            ImGui.PushStyleColor(ImGuiCol.MenuBarBg, Theme.Base);
            bool open = ImGui.BeginMainMenuBar();
            ImGui.PopStyleColor();
            ImGui.PopStyleVar();

            if (!open)
            {
                return;
            }

            DrawMark();

            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new System.Numerics.Vector2(10f, 8f));
            DrawMenus();
            ImGui.PopStyleVar();

            // the workspace switch, centred on the bar
            int workspace = (int)Layout;
            string[] spaces = { $"{Icons.Images}  Browse", $"{Icons.Map}  Map" };
            float width = 0f;

            foreach (string s in spaces)
            {
                width = MathF.Max(width, ImGui.CalcTextSize(s).X + 24f);
            }

            // the bar is taller than a control: controls on it get the normal frame padding and are centred vertically
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new System.Numerics.Vector2(10f, 5f));
            float barHeight = ImGui.GetWindowHeight();
            ImGui.SameLine((ImGui.GetWindowWidth() - width * spaces.Length) * 0.5f);
            ImGui.SetCursorPosY((barHeight - Ui.Height) * 0.5f);

            if (Ui.Segmented("workspace", ref workspace, spaces))
            {
                ResetLayout((StudioLayout)workspace);
            }

            if (_context.Assets != null)
            {
                string name = $"{Icons.Package}  {Path.GetFileName(_context.Assets.Root.TrimEnd('\\', '/'))}";
                float right = ImGui.GetWindowWidth() - ImGui.CalcTextSize(name).X - 36f;
                ImGui.SameLine(right);
                ImGui.SetCursorPosY((barHeight - Ui.Height) * 0.5f);
                Ui.Chip(name, Theme.Gold);

                if (ImGui.IsItemHovered())
                {
                    Ui.Tooltip($"{_context.Assets.Root}\nconverted from client {_context.Assets.Manifest.SourceClientVersion}");
                }
            }

            ImGui.PopStyleVar();
            ImGui.EndMainMenuBar();

            if (ImGui.IsKeyChordPressed((int)(ImGuiKey.ModCtrl | ImGuiKey.O)))
            {
                _start.Browse();
            }
        }

        // ModernUO's logo (Branding/, from modernuo.com), then the name in two weights
        private void DrawMark()
        {
            var draw = ImGui.GetWindowDrawList();
            var p = ImGui.GetCursorScreenPos();
            float h = ImGui.GetWindowHeight();
            const float size = 20f;

            // the pre-rendered image for this display density, drawn 1:1 on whole pixels: scaled or between pixels it blurs
            float scale = MathF.Max(1f, _settings.UiScale);
            Texture2D logo = scale >= 1.5f && _logo40 != null ? _logo40 : _logo20;
            float drawn = logo == _logo40 ? 40f / scale : 20f;
            var min = new System.Numerics.Vector2(MathF.Round(p.X + 2f), MathF.Round(ImGui.GetWindowPos().Y + (h - drawn) * 0.5f));

            if (logo != null)
            {
                draw.AddImage(_renderer.Bind(logo, linear: true), min, min + new System.Numerics.Vector2(drawn, drawn));
            }

            ImGui.Dummy(new System.Numerics.Vector2(size + 4f, 0f));
            ImGui.SameLine(0f, 6f);
            ImGui.PushFont(Theme.Strong, 0f);
            ImGui.TextUnformatted("ModernUO");
            ImGui.PopFont();
            ImGui.SameLine(0f, 4f);
            ImGui.TextColored(Theme.TextSecondary, "Studio");
            ImGui.SameLine(0f, 20f);
        }

        private Texture2D LoadPng(string resource)
        {
            using var stream = typeof(StudioGame).Assembly.GetManifestResourceStream(resource);

            return stream == null ? null : Texture2D.FromStream(GraphicsDevice, stream);
        }

        /// <summary>
        /// The logo for the top bar, and the window icon: 32 px with 16 to 64 px alternates, so the system picks the size
        /// it shows instead of shrinking one large image (the exe icon is Branding/muostudio.ico).
        /// </summary>
        private void LoadLogo()
        {
            _logo20 = LoadPng("logo-20.png");
            _logo40 = LoadPng("logo-40.png");

            IntPtr icon = IntPtr.Zero;
            var alternates = new List<IntPtr>();
            // SDL reads the pixels when the icon is set: each array stays pinned until then
            var pinned = new List<System.Runtime.InteropServices.GCHandle>();

            try
            {
                foreach (int size in new[] { 32, 16, 24, 48, 64 })
                {
                    using var texture = LoadPng($"logo-{size}.png");

                    if (texture == null)
                    {
                        continue;
                    }

                    // ABGR8888 is R, G, B, A in memory, the layout of XNA's Color
                    var pixels = new Color[texture.Width * texture.Height];
                    texture.GetData(pixels);
                    var handle = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
                    pinned.Add(handle);
                    IntPtr surface = SDL3.SDL.SDL_CreateSurfaceFrom(texture.Width, texture.Height, SDL3.SDL.SDL_PixelFormat.SDL_PIXELFORMAT_ABGR8888,
                        handle.AddrOfPinnedObject(), texture.Width * 4);

                    if (surface == IntPtr.Zero)
                    {
                        continue;
                    }

                    if (icon == IntPtr.Zero)
                    {
                        icon = surface;
                    }
                    else
                    {
                        alternates.Add(surface);
                        SDL3.SDL.SDL_AddSurfaceAlternateImage(icon, surface);
                    }
                }

                if (icon != IntPtr.Zero)
                {
                    SDL3.SDL.SDL_SetWindowIcon(Window.Handle, icon);
                }
            }
            finally
            {
                foreach (IntPtr alternate in alternates)
                {
                    SDL3.SDL.SDL_DestroySurface(alternate);
                }

                if (icon != IntPtr.Zero)
                {
                    SDL3.SDL.SDL_DestroySurface(icon);
                }

                foreach (var handle in pinned)
                {
                    handle.Free();
                }
            }
        }
        private void DrawMenus()
        {
            if (ImGui.BeginMenu("File"))
            {
                if (ImGui.MenuItem($"{Icons.FolderOpen}  Open Pack...", "Ctrl+O"))
                {
                    _start.Browse();
                }

                if (ImGui.BeginMenu($"{Icons.Refresh}  Recent", _settings.RecentPacks.Count > 0))
                {
                    string open = null;

                    foreach (string recent in _settings.RecentPacks)
                    {
                        if (ImGui.MenuItem(recent))
                        {
                            open = recent;
                        }
                    }

                    ImGui.EndMenu();

                    if (open != null)
                    {
                        _context.OpenPack(open);
                    }
                }

                if (ImGui.MenuItem($"{Icons.X}  Close Pack", (string)null, false, _context.Assets != null))
                {
                    _context.ClosePack();
                }

                ImGui.Separator();

                if (ImGui.MenuItem($"{Icons.ExternalLink}  Open Export Folder"))
                {
                    OpenFolder(_settings.ResolveExportDirectory());
                }

                ImGui.Separator();

                if (ImGui.MenuItem("Exit"))
                {
                    Exit();
                }

                ImGui.EndMenu();
            }

            if (ImGui.BeginMenu("View"))
            {
                foreach (string group in new[] { "Assets", "Map", "Tools" })
                {
                    ImGui.SeparatorText(group);

                    foreach (var panel in _context.Panels)
                    {
                        if (panel.MenuGroup != group)
                        {
                            continue;
                        }

                        bool panelOpen = panel.IsOpen;

                        if (ImGui.MenuItem($"{panel.Icon ?? " "}  {panel.MenuName}", (string)null, ref panelOpen))
                        {
                            panel.IsOpen = panelOpen;
                        }
                    }
                }

                ImGui.Separator();

                if (ImGui.MenuItem($"{Icons.Images}  Browse workspace"))
                {
                    ResetLayout(StudioLayout.Browse);
                }

                if (ImGui.MenuItem($"{Icons.Map}  Map workspace"))
                {
                    ResetLayout(StudioLayout.Map);
                }

                ImGui.EndMenu();
            }

            if (ImGui.BeginMenu("Help"))
            {
                ImGui.MenuItem($"{Icons.Info}  About", (string)null, ref _showAbout);
                ImGui.Separator();
                ImGui.MenuItem("ImGui Demo", (string)null, ref _showDemo);
                ImGui.MenuItem("ImGui Metrics", (string)null, ref _showMetrics);
                ImGui.EndMenu();
            }
        }

        /// <summary>The status bar: what Studio is doing, the selection, and the frame rate.</summary>
        private void DrawStatusBar()
        {
            var flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.MenuBar;
            ImGui.PushStyleColor(ImGuiCol.MenuBarBg, Theme.Base);
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new System.Numerics.Vector2(10f, 4f));
            bool open = ImGuiP.BeginViewportSideBar("##status", ImGui.GetMainViewport(), ImGuiDir.Down, ImGui.GetFrameHeight(), flags);
            ImGui.PopStyleVar();
            ImGui.PopStyleColor();

            if (open && ImGui.BeginMenuBar())
            {
                var assets = _context.Assets;
                var map = _context.Get<MapPanel>();
                int pending = assets == null ? 0 : (assets.Arts?.PendingDecodes ?? 0) + (assets.Gumps?.PendingDecodes ?? 0)
                    + (assets.Texmaps?.PendingDecodes ?? 0) + (assets.Animations?.PendingDirections ?? 0);
                bool streaming = map is { Visible: true } && _context.MapWorld is { VisibleReady: false };

                (System.Numerics.Vector4 color, string text) state = assets == null ? (Theme.TextMuted, "No pack")
                    : streaming ? (Theme.Gold, "Streaming the map")
                    : pending > 0 ? (Theme.Gold, $"Decoding {pending}")
                    : (Theme.Green, "Ready");

                var draw = ImGui.GetWindowDrawList();
                var p = ImGui.GetCursorScreenPos();
                draw.AddCircleFilled(p + new System.Numerics.Vector2(5f, ImGui.GetFrameHeight() * 0.5f), 3.5f, Theme.U32(state.color), 12);
                ImGui.Dummy(new System.Numerics.Vector2(10f, 0f));
                ImGui.TextColored(Theme.TextSecondary, state.text);

                if (assets != null)
                {
                    ImGui.TextColored(Theme.TextMuted, "·");
                    ImGui.TextColored(Theme.TextMuted, $"client {assets.Manifest.SourceClientVersion}");
                }

                var tile = _context.SelectedTile;

                if (tile != null)
                {
                    ImGui.TextColored(Theme.TextMuted, "·");
                    ImGui.TextColored(Theme.TextMuted, $"{Icons.Crosshair} {MapData.FacetName(tile.Facet)} {tile.X}, {tile.Y}, {tile.LandZ}");
                }

                string fps = $"{ImGui.GetIO().Framerate:0} fps";
                ImGui.SameLine(ImGui.GetWindowWidth() - ImGui.CalcTextSize(fps).X - 16f);
                ImGui.PushFont(Theme.Mono, Theme.SmallSize);
                ImGui.TextColored(Theme.TextMuted, fps);
                ImGui.PopFont();
                ImGui.EndMenuBar();
            }

            ImGui.End();
        }
        private void ResetLayout(StudioLayout layout)
        {
            foreach (var panel in _context.Panels)
            {
                panel.IsOpen = true;
            }

            Layout = layout;
            _buildLayout = true;
        }

        private void DrawAbout()
        {
            ImGui.SetNextWindowSize(new System.Numerics.Vector2(460, 0), ImGuiCond.Appearing);

            if (ImGui.Begin("About ModernUO Studio", ref _showAbout, ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.AlwaysAutoResize))
            {
                ImGui.TextUnformatted("ModernUO Studio");
                ImGui.TextDisabled("Asset tooling for ModernUO packs, built on the ModernUO client's loaders and renderer.");
                ImGui.Spacing();
                ImGui.TextUnformatted($"Dear ImGui {ImGui.GetVersionS()}");
                ImGui.TextUnformatted($"FNA3D driver {ClassicUO.Renderer.GpuInterop.GetFna3dDriverName(GraphicsDevice)}");
            }

            ImGui.End();
        }

        private static void OpenFolder(string path)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                StudioLog.Warn($"Could not open {path}: {ex.Message}");
            }
        }

        protected override void UnloadContent()
        {
            _context.Get<MapPanel>()?.SaveOnExit();
            var bounds = Window.ClientBounds;
            _settings.WindowWidth = bounds.Width;
            _settings.WindowHeight = bounds.Height;
            _settings.Save();

            ImGui.SaveIniSettingsToDisk(Marshal.PtrToStringUTF8(_iniPath));
            _context.ClosePack();
            _platform.Dispose();
            _renderer.Dispose();
            ImGui.DestroyContext();
            Marshal.FreeCoTaskMem(_iniPath);

            base.UnloadContent();
        }
    }
}
