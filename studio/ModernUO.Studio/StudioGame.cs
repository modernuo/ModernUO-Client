using System.Runtime.InteropServices;
using Hexa.NET.ImGui;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ModernUO.Studio.Gui;
using ModernUO.Studio.Panels;

namespace ModernUO.Studio
{
    /// <summary>The Studio window: an FNA game whose whole frame is a docking Dear ImGui UI.</summary>
    internal sealed class StudioGame : Game
    {
        private readonly GraphicsDeviceManager _graphics;
        private readonly StudioSettings _settings;
        private readonly string _startupPack;
        private ImGuiRenderer _renderer;
        private ImGuiPlatform _platform;
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

            StudioTheme.Apply(_settings.UiScale);
            StudioTheme.LoadFont(17f * _settings.UiScale);

            _renderer = new ImGuiRenderer(GraphicsDevice);
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
            GraphicsDevice.SetRenderTarget(null);
            GraphicsDevice.Clear(new Color(27, 28, 31));
            _renderer.Render(ImGui.GetDrawData());

            // the warm-up covers the first-run dock layout and the worker decodes of what the browsers show first
            _frames++;

            if (_screenshot != null && _frames == 90)
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
            uint dock = ImGui.DockSpaceOverViewport();

            if (_buildLayout)
            {
                _buildLayout = false;
                BuildDefaultLayout(dock);
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

            DrawMainMenu();

            foreach (var panel in _context.Panels)
            {
                panel.Draw();
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

        // first run (no studio_layout.ini): browsers as tabs in the middle, the log under them
        private static unsafe void BuildDefaultLayout(uint dock)
        {
            ImGuiP.DockBuilderRemoveNode(dock);
            ImGuiP.DockBuilderAddNode(dock, ImGuiDockNodeFlags.PassthruCentralNode | (ImGuiDockNodeFlags)ImGuiDockNodeFlagsPrivate.Space);
            ImGuiP.DockBuilderSetNodeSize(dock, ImGui.GetMainViewport().Size);

            uint main = dock, bottom;
            ImGuiP.DockBuilderSplitNode(main, ImGuiDir.Down, 0.2f, &bottom, &main);

            foreach (string window in new[] { "Start###start", "Art###art", "Gumps###gumps", "Hues###hues", "TileData###tiledata",
                         "Animations###anims", "Multis###multis", "Texmaps###texmaps", "Sounds###sounds", "Cliloc###cliloc", "Pack Info###packinfo" })
            {
                ImGuiP.DockBuilderDockWindow(window, main);
            }

            ImGuiP.DockBuilderDockWindow("Log###log", bottom);
            ImGuiP.DockBuilderFinish(dock);
        }

        private void DrawMainMenu()
        {
            if (!ImGui.BeginMainMenuBar())
            {
                return;
            }

            if (ImGui.BeginMenu("File"))
            {
                if (ImGui.MenuItem("Open Pack...", "Ctrl+O"))
                {
                    _start.Browse();
                }

                if (ImGui.BeginMenu("Recent", _settings.RecentPacks.Count > 0))
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

                if (ImGui.MenuItem("Close Pack", (string)null, false, _context.Assets != null))
                {
                    _context.ClosePack();
                }

                ImGui.Separator();

                if (ImGui.MenuItem("Open Export Folder"))
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
                foreach (string group in new[] { "Assets", "Tools" })
                {
                    ImGui.SeparatorText(group);

                    foreach (var panel in _context.Panels)
                    {
                        if (panel.MenuGroup != group)
                        {
                            continue;
                        }

                        bool open = panel.IsOpen;

                        if (ImGui.MenuItem(panel.MenuName, (string)null, ref open))
                        {
                            panel.IsOpen = open;
                        }
                    }
                }

                ImGui.Separator();

                if (ImGui.MenuItem("Reset Layout"))
                {
                    foreach (var panel in _context.Panels)
                    {
                        panel.IsOpen = true;
                    }

                    _buildLayout = true;
                }

                ImGui.EndMenu();
            }

            if (ImGui.BeginMenu("Help"))
            {
                ImGui.MenuItem("About", (string)null, ref _showAbout);
                ImGui.MenuItem("ImGui Demo", (string)null, ref _showDemo);
                ImGui.MenuItem("ImGui Metrics", (string)null, ref _showMetrics);
                ImGui.EndMenu();
            }

            string status = _context.Assets == null ? "no pack" : _context.Assets.Root;
            string fps = $"{ImGui.GetIO().Framerate:0} fps";
            float right = ImGui.CalcTextSize(status).X + ImGui.CalcTextSize(fps).X + 40f;
            ImGui.SameLine(ImGui.GetWindowWidth() - right);
            ImGui.TextDisabled(status);
            ImGui.SameLine();
            ImGui.TextDisabled(fps);

            ImGui.EndMainMenuBar();

            if (ImGui.IsKeyChordPressed((int)(ImGuiKey.ModCtrl | ImGuiKey.O)))
            {
                _start.Browse();
            }
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
