using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Hexa.NET.ImGui;
using static SDL3.SDL;

namespace ModernUO.Studio.Gui
{
    /// <summary>
    /// The SDL3 side of Dear ImGui: input from an SDL event filter (FNA pumps the events, the filter sees each one as it is
    /// queued), the clipboard and the mouse cursor shapes.
    /// </summary>
    internal sealed unsafe class ImGuiPlatform : IDisposable
    {
        private readonly IntPtr _window;
        private readonly SDL_EventFilter _filter;
        private readonly IntPtr[] _cursors = new IntPtr[(int)ImGuiMouseCursor.Count];
        private ImGuiMouseCursor _lastCursor = ImGuiMouseCursor.Count;
        private static IntPtr _clipboard;

        public ImGuiPlatform(IntPtr window)
        {
            _window = window;

            var io = ImGui.GetIO();
            io.BackendFlags |= ImGuiBackendFlags.HasMouseCursors;

            var platform = ImGui.GetPlatformIO();
            platform.PlatformGetClipboardTextFn = (void*)(delegate* unmanaged[Cdecl]<ImGuiContext*, byte*>)&GetClipboardText;
            platform.PlatformSetClipboardTextFn = (void*)(delegate* unmanaged[Cdecl]<ImGuiContext*, byte*, void>)&SetClipboardText;

            _cursors[(int)ImGuiMouseCursor.Arrow] = SDL_CreateSystemCursor(SDL_SystemCursor.SDL_SYSTEM_CURSOR_DEFAULT);
            _cursors[(int)ImGuiMouseCursor.TextInput] = SDL_CreateSystemCursor(SDL_SystemCursor.SDL_SYSTEM_CURSOR_TEXT);
            _cursors[(int)ImGuiMouseCursor.ResizeAll] = SDL_CreateSystemCursor(SDL_SystemCursor.SDL_SYSTEM_CURSOR_MOVE);
            _cursors[(int)ImGuiMouseCursor.ResizeNs] = SDL_CreateSystemCursor(SDL_SystemCursor.SDL_SYSTEM_CURSOR_NS_RESIZE);
            _cursors[(int)ImGuiMouseCursor.ResizeEw] = SDL_CreateSystemCursor(SDL_SystemCursor.SDL_SYSTEM_CURSOR_EW_RESIZE);
            _cursors[(int)ImGuiMouseCursor.ResizeNesw] = SDL_CreateSystemCursor(SDL_SystemCursor.SDL_SYSTEM_CURSOR_NESW_RESIZE);
            _cursors[(int)ImGuiMouseCursor.ResizeNwse] = SDL_CreateSystemCursor(SDL_SystemCursor.SDL_SYSTEM_CURSOR_NWSE_RESIZE);
            _cursors[(int)ImGuiMouseCursor.Hand] = SDL_CreateSystemCursor(SDL_SystemCursor.SDL_SYSTEM_CURSOR_POINTER);
            _cursors[(int)ImGuiMouseCursor.Wait] = SDL_CreateSystemCursor(SDL_SystemCursor.SDL_SYSTEM_CURSOR_WAIT);
            _cursors[(int)ImGuiMouseCursor.Progress] = SDL_CreateSystemCursor(SDL_SystemCursor.SDL_SYSTEM_CURSOR_PROGRESS);
            _cursors[(int)ImGuiMouseCursor.NotAllowed] = SDL_CreateSystemCursor(SDL_SystemCursor.SDL_SYSTEM_CURSOR_NOT_ALLOWED);

            _filter = OnEvent;
            SDL_SetEventFilter(_filter, IntPtr.Zero);
            SDL_StartTextInput(window);
        }

        public void NewFrame(float deltaSeconds, int width, int height)
        {
            var io = ImGui.GetIO();
            io.DisplaySize = new System.Numerics.Vector2(width, height);
            io.DisplayFramebufferScale = System.Numerics.Vector2.One;
            io.DeltaTime = deltaSeconds > 0 ? deltaSeconds : 1f / 60f;

            UpdateCursor(io);
        }

        private void UpdateCursor(ImGuiIOPtr io)
        {
            if ((io.ConfigFlags & ImGuiConfigFlags.NoMouseCursorChange) != 0)
            {
                return;
            }

            ImGuiMouseCursor cursor = ImGui.GetMouseCursor();

            if (cursor == _lastCursor)
            {
                return;
            }

            _lastCursor = cursor;

            if (io.MouseDrawCursor || cursor == ImGuiMouseCursor.None)
            {
                SDL_HideCursor();
            }
            else
            {
                IntPtr sdlCursor = _cursors[(int)cursor] != IntPtr.Zero ? _cursors[(int)cursor] : _cursors[(int)ImGuiMouseCursor.Arrow];
                SDL_SetCursor(sdlCursor);
                SDL_ShowCursor();
            }
        }

        private bool OnEvent(IntPtr userData, SDL_Event* evt)
        {
            var io = ImGui.GetIO();

            switch ((SDL_EventType)evt->type)
            {
                case SDL_EventType.SDL_EVENT_MOUSE_MOTION:
                    io.AddMousePosEvent(evt->motion.x, evt->motion.y);

                    break;

                case SDL_EventType.SDL_EVENT_MOUSE_WHEEL:
                    io.AddMouseWheelEvent(-evt->wheel.x, evt->wheel.y);

                    break;

                case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN:
                case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP:
                {
                    int button = evt->button.button switch
                    {
                        1 => 0, // left
                        3 => 1, // right
                        2 => 2, // middle
                        4 => 3,
                        5 => 4,
                        _ => -1
                    };

                    if (button >= 0)
                    {
                        io.AddMouseButtonEvent(button, evt->button.down);
                    }

                    break;
                }

                case SDL_EventType.SDL_EVENT_TEXT_INPUT:
                    io.AddInputCharactersUTF8(evt->text.text);

                    break;

                case SDL_EventType.SDL_EVENT_KEY_DOWN:
                case SDL_EventType.SDL_EVENT_KEY_UP:
                {
                    bool down = evt->key.down;
                    SDL_Keymod mod = evt->key.mod;
                    io.AddKeyEvent(ImGuiKey.ModCtrl, (mod & SDL_Keymod.SDL_KMOD_CTRL) != 0);
                    io.AddKeyEvent(ImGuiKey.ModShift, (mod & SDL_Keymod.SDL_KMOD_SHIFT) != 0);
                    io.AddKeyEvent(ImGuiKey.ModAlt, (mod & SDL_Keymod.SDL_KMOD_ALT) != 0);
                    io.AddKeyEvent(ImGuiKey.ModSuper, (mod & SDL_Keymod.SDL_KMOD_GUI) != 0);

                    ImGuiKey key = MapKey(evt->key.scancode);

                    if (key != ImGuiKey.None)
                    {
                        io.AddKeyEvent(key, down);
                    }

                    break;
                }

                case SDL_EventType.SDL_EVENT_WINDOW_FOCUS_GAINED:
                    io.AddFocusEvent(true);

                    break;

                case SDL_EventType.SDL_EVENT_WINDOW_FOCUS_LOST:
                    io.AddFocusEvent(false);

                    break;

                case SDL_EventType.SDL_EVENT_WINDOW_MOUSE_LEAVE:
                    io.AddMousePosEvent(-float.MaxValue, -float.MaxValue);

                    break;
            }

            return true;
        }

        // Scancodes, not keycodes: shortcuts stay on the same physical keys on every layout (text goes through TEXT_INPUT)
        private static ImGuiKey MapKey(SDL_Scancode code)
        {
            if (code >= SDL_Scancode.SDL_SCANCODE_A && code <= SDL_Scancode.SDL_SCANCODE_Z)
            {
                return ImGuiKey.A + (code - SDL_Scancode.SDL_SCANCODE_A);
            }

            if (code >= SDL_Scancode.SDL_SCANCODE_1 && code <= SDL_Scancode.SDL_SCANCODE_9)
            {
                return ImGuiKey.Key1 + (code - SDL_Scancode.SDL_SCANCODE_1);
            }

            if (code >= SDL_Scancode.SDL_SCANCODE_F1 && code <= SDL_Scancode.SDL_SCANCODE_F12)
            {
                return ImGuiKey.F1 + (code - SDL_Scancode.SDL_SCANCODE_F1);
            }

            if (code >= SDL_Scancode.SDL_SCANCODE_KP_1 && code <= SDL_Scancode.SDL_SCANCODE_KP_9)
            {
                return ImGuiKey.Keypad1 + (code - SDL_Scancode.SDL_SCANCODE_KP_1);
            }

            return code switch
            {
                SDL_Scancode.SDL_SCANCODE_0 => ImGuiKey.Key0,
                SDL_Scancode.SDL_SCANCODE_KP_0 => ImGuiKey.Keypad0,
                SDL_Scancode.SDL_SCANCODE_RETURN => ImGuiKey.Enter,
                SDL_Scancode.SDL_SCANCODE_ESCAPE => ImGuiKey.Escape,
                SDL_Scancode.SDL_SCANCODE_BACKSPACE => ImGuiKey.Backspace,
                SDL_Scancode.SDL_SCANCODE_TAB => ImGuiKey.Tab,
                SDL_Scancode.SDL_SCANCODE_SPACE => ImGuiKey.Space,
                SDL_Scancode.SDL_SCANCODE_MINUS => ImGuiKey.Minus,
                SDL_Scancode.SDL_SCANCODE_EQUALS => ImGuiKey.Equal,
                SDL_Scancode.SDL_SCANCODE_LEFTBRACKET => ImGuiKey.LeftBracket,
                SDL_Scancode.SDL_SCANCODE_RIGHTBRACKET => ImGuiKey.RightBracket,
                SDL_Scancode.SDL_SCANCODE_BACKSLASH => ImGuiKey.Backslash,
                SDL_Scancode.SDL_SCANCODE_SEMICOLON => ImGuiKey.Semicolon,
                SDL_Scancode.SDL_SCANCODE_APOSTROPHE => ImGuiKey.Apostrophe,
                SDL_Scancode.SDL_SCANCODE_GRAVE => ImGuiKey.GraveAccent,
                SDL_Scancode.SDL_SCANCODE_COMMA => ImGuiKey.Comma,
                SDL_Scancode.SDL_SCANCODE_PERIOD => ImGuiKey.Period,
                SDL_Scancode.SDL_SCANCODE_SLASH => ImGuiKey.Slash,
                SDL_Scancode.SDL_SCANCODE_CAPSLOCK => ImGuiKey.CapsLock,
                SDL_Scancode.SDL_SCANCODE_PRINTSCREEN => ImGuiKey.PrintScreen,
                SDL_Scancode.SDL_SCANCODE_SCROLLLOCK => ImGuiKey.ScrollLock,
                SDL_Scancode.SDL_SCANCODE_PAUSE => ImGuiKey.Pause,
                SDL_Scancode.SDL_SCANCODE_INSERT => ImGuiKey.Insert,
                SDL_Scancode.SDL_SCANCODE_HOME => ImGuiKey.Home,
                SDL_Scancode.SDL_SCANCODE_PAGEUP => ImGuiKey.PageUp,
                SDL_Scancode.SDL_SCANCODE_DELETE => ImGuiKey.Delete,
                SDL_Scancode.SDL_SCANCODE_END => ImGuiKey.End,
                SDL_Scancode.SDL_SCANCODE_PAGEDOWN => ImGuiKey.PageDown,
                SDL_Scancode.SDL_SCANCODE_RIGHT => ImGuiKey.RightArrow,
                SDL_Scancode.SDL_SCANCODE_LEFT => ImGuiKey.LeftArrow,
                SDL_Scancode.SDL_SCANCODE_DOWN => ImGuiKey.DownArrow,
                SDL_Scancode.SDL_SCANCODE_UP => ImGuiKey.UpArrow,
                SDL_Scancode.SDL_SCANCODE_NUMLOCKCLEAR => ImGuiKey.NumLock,
                SDL_Scancode.SDL_SCANCODE_KP_DIVIDE => ImGuiKey.KeypadDivide,
                SDL_Scancode.SDL_SCANCODE_KP_MULTIPLY => ImGuiKey.KeypadMultiply,
                SDL_Scancode.SDL_SCANCODE_KP_MINUS => ImGuiKey.KeypadSubtract,
                SDL_Scancode.SDL_SCANCODE_KP_PLUS => ImGuiKey.KeypadAdd,
                SDL_Scancode.SDL_SCANCODE_KP_ENTER => ImGuiKey.KeypadEnter,
                SDL_Scancode.SDL_SCANCODE_KP_PERIOD => ImGuiKey.KeypadDecimal,
                SDL_Scancode.SDL_SCANCODE_LCTRL => ImGuiKey.LeftCtrl,
                SDL_Scancode.SDL_SCANCODE_LSHIFT => ImGuiKey.LeftShift,
                SDL_Scancode.SDL_SCANCODE_LALT => ImGuiKey.LeftAlt,
                SDL_Scancode.SDL_SCANCODE_LGUI => ImGuiKey.LeftSuper,
                SDL_Scancode.SDL_SCANCODE_RCTRL => ImGuiKey.RightCtrl,
                SDL_Scancode.SDL_SCANCODE_RSHIFT => ImGuiKey.RightShift,
                SDL_Scancode.SDL_SCANCODE_RALT => ImGuiKey.RightAlt,
                SDL_Scancode.SDL_SCANCODE_RGUI => ImGuiKey.RightSuper,
                SDL_Scancode.SDL_SCANCODE_APPLICATION => ImGuiKey.Menu,
                _ => ImGuiKey.None
            };
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static byte* GetClipboardText(ImGuiContext* ctx)
        {
            string text = SDL_GetClipboardText() ?? string.Empty;

            if (_clipboard != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(_clipboard);
            }

            // ImGui reads the returned string until the next call
            _clipboard = Marshal.StringToCoTaskMemUTF8(text);

            return (byte*)_clipboard;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void SetClipboardText(ImGuiContext* ctx, byte* text)
        {
            SDL_SetClipboardText(Marshal.PtrToStringUTF8((IntPtr)text) ?? string.Empty);
        }

        public void Dispose()
        {
            SDL_SetEventFilter(null, IntPtr.Zero);
            SDL_StopTextInput(_window);

            foreach (IntPtr cursor in _cursors)
            {
                if (cursor != IntPtr.Zero)
                {
                    SDL_DestroyCursor(cursor);
                }
            }

            if (_clipboard != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(_clipboard);
                _clipboard = IntPtr.Zero;
            }
        }
    }
}
