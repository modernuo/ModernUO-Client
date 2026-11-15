// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Game.Managers;
using ClassicUO.Game.Scenes;
using ClassicUO.Input;
using ClassicUO.Renderer;
using ClassicUO.Utility;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using SDL3;
using System;
using System.Collections.Generic;
using System.Linq;
using Keyboard = ClassicUO.Input.Keyboard;

namespace ClassicUO.Game.UI.Controls
{
    internal abstract class Control
    {
        internal long DrawVersion { get; private set; }

        internal void InvalidateDraw()
        {
            unchecked { DrawVersion++; }
            _parent?.InvalidateDraw();
        }

        protected const float CHILD_LAYER_INCREMENT = 0.01f;
        internal static int _StepsDone = 1;
        internal static int _StepChanger = 1;

        private bool _acceptKeyboardInput, _acceptMouseInput;
        private int _activePage;
        private Rectangle _bounds;
        private bool _handlesKeyboardFocus;
        private Point _offset;
        private Control _parent;
        private bool _outOfParentList;

        protected Control(Control parent = null)
        {
            Parent = parent;
            Children = new ControlChildren(this);
            AllowedToDraw = true;
            AcceptMouseInput = true;
            Page = 0;

            IsDisposed = false;
            IsEnabled = true;
        }

        public virtual ClickPriority Priority { get; set; } = ClickPriority.Default;

        public uint ServerSerial { get; set; }

        public uint LocalSerial { get; set; }

        public bool IsFromServer { get; set; }

        private int _page = 0;
        public int Page
        {
            get => _page;
            set
            {
                if (_page != value)
                {
                    _page = value;
                    InvalidateDraw();
                }
            }
        }

        public Point Location
        {
            get => _bounds.Location;
            set
            {
                X = value.X;
                Y = value.Y;
                _bounds.Location = value;
            }
        }

        public Rectangle Bounds
        {
            get => _bounds;
            set
            {
                if (_bounds != value)
                {
                    _bounds = value;
                    InvalidateDraw();
                }
            }
        }

        public Point Offset => _offset;

        public bool IsDisposed { get; private set; }

        private bool _isVisible = true;
        public bool IsVisible
        {
            get => _isVisible;
            set
            {
                if (_isVisible != value)
                {
                    _isVisible = value;
                    InvalidateDraw();
                }
            }
        }

        private bool _isEnabled = false;
        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (_isEnabled != value)
                {
                    _isEnabled = value;
                    InvalidateDraw();
                }
            }
        }

        public bool HasKeyboardFocus => UIManager.KeyboardFocusControl == this;

        public bool MouseIsOver => UIManager.MouseOverControl == this;

        public bool CanMove { get; set; }

        public bool CanCloseWithRightClick { get; set; } = true;

        public bool CanCloseWithEsc { get; set; }

        public bool IsEditable { get; set; }

        public bool IsFocused { get; set; }

        private float _alpha = 1.0f;
        public float Alpha
        {
            get => _alpha;
            set
            {
                if (_alpha != value)
                {
                    _alpha = value;
                    InvalidateDraw();
                }
            }
        }

        public ControlChildren Children { get; }

        public object Tag { get; set; }

        public object Tooltip { get; private set; }

        public bool HasTooltip => /*World.ClientFlags.TooltipsEnabled &&*/ Tooltip != null;

        public virtual bool AcceptKeyboardInput
        {
            get => IsEnabled && !IsDisposed && IsVisible && _acceptKeyboardInput;
            set => _acceptKeyboardInput = value;
        }

        public virtual bool AcceptMouseInput
        {
            get => IsEnabled && !IsDisposed && _acceptMouseInput && IsVisible;
            set => _acceptMouseInput = value;
        }

        public int X
        {
            get => _bounds.X;
            set
            {
                if (_bounds.X != value)
                {
                    _bounds.X = value;
                    InvalidateDraw();
                }
            }
        }

        public int Y
        {
            get => _bounds.Y;
            set
            {
                if (_bounds.Y != value)
                {
                    _bounds.Y = value;
                    InvalidateDraw();
                }
            }
        }

        public int Width
        {
            get => _bounds.Width;
            set
            {
                if (_bounds.Width != value)
                {
                    _bounds.Width = value;
                    InvalidateDraw();
                }
            }
        }

        public int Height
        {
            get => _bounds.Height;
            set
            {
                if (_bounds.Height != value)
                {
                    _bounds.Height = value;
                    InvalidateDraw();
                }
            }
        }

        public int ParentX => Parent != null ? Parent.X + Parent.ParentX : 0;

        public int ParentY => Parent != null ? Parent.Y + Parent.ParentY : 0;

        public int ScreenCoordinateX => ParentX + X;

        public int ScreenCoordinateY => ParentY + Y;

        public ContextMenuControl ContextMenu { get; set; }

        public Control Parent
        {
            get => _parent;
            internal set
            {
                LeaveParentList();

                value?.Children.Add(this);

                _parent = value;
                InvalidateDraw();
            }
        }

        /// <summary>
        /// perf-9: the caller took this control out of its parent's child list itself and keeps <see cref="Parent"/> as it
        /// is (ContainerItemControls.Begin keeps a container window's item controls for reuse). The next re-parent skips the
        /// list scan, which would not find it: re-adding n controls this way scanned the list n times. A caller that puts
        /// the control back into the list directly calls <see cref="InParentList"/>.
        /// </summary>
        internal void OutOfParentList() => _outOfParentList = true;

        /// <summary>Undoes <see cref="OutOfParentList"/>: the caller put the control back into its parent's child list.</summary>
        internal void InParentList() => _outOfParentList = false;

        private void LeaveParentList()
        {
            if (_outOfParentList)
            {
                _outOfParentList = false;

                return;
            }

            _parent?.Children.Remove(this);
        }

        public Control RootParent
        {
            get
            {
                if (Parent == null)
                {
                    return null;
                }

                Control p = Parent;

                while (p.Parent != null)
                {
                    p = p.Parent;
                }

                return p;
            }
        }

        public UILayer LayerOrder { get; set; } = UILayer.Default;
        public bool IsModal { get; set; }
        public bool ModalClickOutsideAreaClosesThisControl { get; set; }


        public virtual bool HandlesKeyboardFocus
        {
            get
            {
                if (!IsEnabled || IsDisposed || !IsVisible)
                {
                    return false;
                }

                if (_handlesKeyboardFocus)
                {
                    return true;
                }

                if (Children == null)
                {
                    return false;
                }

                foreach (Control c in Children)
                {
                    if (c.HandlesKeyboardFocus)
                    {
                        return true;
                    }
                }

                return false;
            }
            set => _handlesKeyboardFocus = value;
        }

        public int ActivePage
        {
            get => _activePage;
            set
            {
                if (_activePage != value) InvalidateDraw();
                _activePage = value;

                OnPageChanged();
            }
        }

        public bool WantUpdateSize { get; set; } = true;

        public bool AllowedToDraw { get; set; }

        public int TooltipMaxLength { get; private set; }

        public void UpdateOffset(int x, int y)
        {
            if (_offset.X != x || _offset.Y != y)
            {
                _offset.X = x;
                _offset.Y = y;

                foreach (Control c in Children)
                {
                    c.UpdateOffset(x, y);
                }
            }
        }

        public virtual bool AddToRenderLists(RenderLists renderLists, int x, int y, ref float layerDepth)
        {
            if (IsDisposed)
            {
                return false;
            }

            foreach (Control c in Children)
            {
                if (c.Page == 0 || c.Page == ActivePage)
                {
                    if (c.IsVisible)
                    {
                        layerDepth += CHILD_LAYER_INCREMENT;
                        c.AddToRenderLists(renderLists, c.X + x, c.Y + y, ref layerDepth);
                    }
                }
            }

            DrawDebug(renderLists, x, y, layerDepth);

            return true;
        }

        public virtual void Update()
        {
            if (IsDisposed)
            {
                return;
            }

            if (Children.Count != 0)
            {
                //InitializeControls();
                int w = 0, h = 0;

                for (int i = 0; i < Children.Count; i++)
                {
                    Control c = Children[i];

                    if (c.IsDisposed)
                    {
                        // perf-10: the run of disposed children from here goes at once. No Update runs between them, so it is
                        // what one RemoveAt per child did, but a rebuild that disposed n children is one shift, not n.
                        int end = i + 1;

                        while (end < Children.Count && Children[end].IsDisposed)
                        {
                            end++;
                        }

                        for (int k = i; k < end; k++)
                        {
                            OnChildRemoved();
                        }

                        Children.RemoveRange(i, end - i);
                        i--;

                        continue;
                    }

                    c.Update();

                    if (WantUpdateSize)
                    {
                        if ((c.Page == 0 || c.Page == ActivePage) && c.IsVisible)
                        {
                            if (w < c.Bounds.Right)
                            {
                                w = c.Bounds.Right;
                            }

                            if (h < c.Bounds.Bottom)
                            {
                                h = c.Bounds.Bottom;
                            }
                        }
                    }
                }

                if (WantUpdateSize && IsVisible)
                {
                    if (w != Width)
                    {
                        Width = w;
                    }

                    if (h != Height)
                    {
                        Height = h;
                    }

                    WantUpdateSize = false;
                }
            }
        }

        public virtual void OnPageChanged()
        {
            //Update size as pages may vary in size.
            if (ServerSerial != 0)
            {
                WantUpdateSize = true;
            }
        }

        private void DrawDebug(RenderLists renderLists, int x, int y, float layerDepth)
        {
            // a struct command, not a closure: C# allocates a closure's captures at method entry, so even the
            // debug-off path allocated one per visible control per frame
            if (!IsVisible || !CUOEnviroment.Debug)
            {
                return;
            }

            renderLists.AddGumpNoAtlas
            (
                GumpCommand.Outline(SolidColorTextureCache.GetTexture(Color.Green), x, y, Width, Height, ShaderHueTranslator.GetHueVector(0), layerDepth)
            );
        }

        public void BringOnTop()
        {
            UIManager.MakeTopMostGump(this);
        }

        public void SetTooltip(string text, int maxWidth = 0)
        {
            ClearTooltip();

            if (!string.IsNullOrEmpty(text))
            {
                Tooltip = text;
                TooltipMaxLength = maxWidth;
            }
        }

        public void SetTooltip(uint entity)
        {
            ClearTooltip();
            Tooltip = entity;
        }

        public void ClearTooltip()
        {
            Tooltip = null;
        }

        public void SetKeyboardFocus()
        {
            if (AcceptKeyboardInput && !HasKeyboardFocus)
            {
                UIManager.KeyboardFocusControl = this;
            }
        }

        internal event EventHandler<MouseEventArgs> MouseDown, MouseUp, MouseOver, MouseEnter, MouseExit, DragBegin, DragEnd;

        internal event EventHandler<MouseWheelEventArgs> MouseWheel;

        internal event EventHandler<MouseDoubleClickEventArgs> MouseDoubleClick;

        internal event EventHandler FocusEnter, FocusLost;

        internal event EventHandler<KeyboardEventArgs> KeyDown, KeyUp;


        public void HitTest(int x, int y, ref Control res)
        {
            if (!IsVisible || !IsEnabled || IsDisposed)
            {
                return;
            }

            int parentX = ParentX;
            int parentY = ParentY;

            if (Bounds.Contains(x - parentX - _offset.X, y - parentY - _offset.Y))
            {
                if (Contains(x - X - parentX, y - Y - parentY))
                {
                    if (AcceptMouseInput)
                    {
                        if (res == null || res.Priority >= Priority)
                        {
                            res = this;
                            OnHitTestSuccess(x, y, ref res);
                        }
                    }

                    for (int i = 0; i < Children.Count; ++i)
                    {
                        Control c = Children[i];

                        if (c.Page == 0 || c.Page == ActivePage)
                        {
                            c.HitTest(x, y, ref res);
                        }
                    }
                }
            }
        }

        public void HitTest(Point position, ref Control res)
        {
            HitTest(position.X, position.Y, ref res);
        }

        public virtual void OnHitTestSuccess(int x, int y, ref Control res)
        {
        }

        public Control GetFirstControlAcceptKeyboardInput()
        {
            if (_acceptKeyboardInput)
            {
                return this;
            }

            if (Children == null || Children.Count == 0)
            {
                return null;
            }

            foreach (Control c in Children)
            {
                Control a = c.GetFirstControlAcceptKeyboardInput();

                if (a != null)
                {
                    return a;
                }
            }

            return null;
        }

        public virtual T Add<T>(T c, int page = 0) where T : Control
        {
            c.Page = page;
            c.Parent = this;
            OnChildAdded();

            return c;
        }
        
        public void Insert(int index, Control c, int page = 0)
        {
            c.Page = 0;

            c.LeaveParentList();

            c._parent = this;

            Children.Insert(index, c);

            OnChildAdded();
        }

        public virtual void Remove(Control c)
        {
            if (c == null)
            {
                return;
            }

            c.Parent = null;
            Children.Remove(c);
            OnChildRemoved();
        }

        public virtual void Clear()
        {
            foreach (Control c in Children)
            {
                c.Dispose();
            }
        }

        public T[] GetControls<T>() where T : Control
        {
            return Children.OfType<T>().Where(s => !s.IsDisposed).ToArray();
        }

        public IEnumerable<T> FindControls<T>() where T : Control
        {
            return Children.OfType<T>().Where(s => !s.IsDisposed);
        }


        public void InvokeMouseDown(Point position, MouseButtonType button)
        {
            int x = position.X - X - ParentX;
            int y = position.Y - Y - ParentY;
            OnMouseDown(x, y, button);
            MouseDown.Raise(new MouseEventArgs(x, y, button, ButtonState.Pressed), this);
        }

        public void InvokeMouseUp(Point position, MouseButtonType button)
        {
            int x = position.X - X - ParentX;
            int y = position.Y - Y - ParentY;
            OnMouseUp(x, y, button);
            MouseUp.Raise(new MouseEventArgs(x, y, button), this);
        }

        public void InvokeMouseCloseGumpWithRClick()
        {
            if (CanCloseWithRightClick)
            {
                CloseWithRightClick();
            }
        }

        public void InvokeMouseOver(Point position)
        {
            int x = position.X - X - ParentX;
            int y = position.Y - Y - ParentY;
            OnMouseOver(x, y);
            // the handler is read after OnMouseOver, as Raise did; no listeners means no MouseEventArgs (hover runs every frame)
            EventHandler<MouseEventArgs> handler = MouseOver;

            if (handler != null)
            {
                handler(this, new MouseEventArgs(x, y));
            }
        }

        public void InvokeMouseEnter(Point position)
        {
            int x = position.X - X - ParentX;
            int y = position.Y - Y - ParentY;
            OnMouseEnter(x, y);
            // the handler is read after OnMouseEnter, as Raise did; no listeners means no MouseEventArgs (hover runs every frame)
            EventHandler<MouseEventArgs> handler = MouseEnter;

            if (handler != null)
            {
                handler(this, new MouseEventArgs(x, y));
            }
        }

        public void InvokeMouseExit(Point position)
        {
            int x = position.X - X - ParentX;
            int y = position.Y - Y - ParentY;
            OnMouseExit(x, y);
            // the handler is read after OnMouseExit, as Raise did; no listeners means no MouseEventArgs (hover runs every frame)
            EventHandler<MouseEventArgs> handler = MouseExit;

            if (handler != null)
            {
                handler(this, new MouseEventArgs(x, y));
            }
        }

        public bool InvokeMouseDoubleClick(Point position, MouseButtonType button)
        {
            int x = position.X - X - ParentX;
            int y = position.Y - Y - ParentY;
            bool result = OnMouseDoubleClick(x, y, button);

            MouseDoubleClickEventArgs arg = new MouseDoubleClickEventArgs(x, y, button);
            MouseDoubleClick.Raise(arg, this);
            result |= arg.Result;

            return result;
        }

        public void InvokeTextInput(string c)
        {
            OnTextInput(c);
        }

        public void InvokeKeyDown(SDL.SDL_Keycode key, SDL.SDL_Keymod mod)
        {
            OnKeyDown(key, mod);
            KeyboardEventArgs arg = new KeyboardEventArgs(key, mod, KeyboardEventType.Down);
            KeyDown?.Raise(arg);
        }

        public void InvokeKeyUp(SDL.SDL_Keycode key, SDL.SDL_Keymod mod)
        {
            OnKeyUp(key, mod);
            KeyboardEventArgs arg = new KeyboardEventArgs(key, mod, KeyboardEventType.Up);
            KeyUp?.Raise(arg);
        }

        public void InvokeMouseWheel(MouseEventType delta)
        {
            OnMouseWheel(delta);
            MouseWheel.Raise(new MouseWheelEventArgs(delta), this);
        }

        public void InvokeDragBegin(Point position)
        {
            int x = position.X - X - ParentX;
            int y = position.Y - Y - ParentY;
            OnDragBegin(x, y);
            DragBegin.Raise(new MouseEventArgs(x, y, MouseButtonType.Left, ButtonState.Pressed), this);
        }

        public void InvokeDragEnd(Point position)
        {
            int x = position.X - X - ParentX;
            int y = position.Y - Y - ParentY;
            OnDragEnd(x, y);
            DragEnd.Raise(new MouseEventArgs(x, y, MouseButtonType.Left), this);
        }

        public void InvokeMove(int x, int y)
        {
            x = x - X - ParentX;
            y = y - Y - ParentY;
            OnMove(x, y);
        }

        protected virtual void OnMouseDown(int x, int y, MouseButtonType button)
        {
            Parent?.OnMouseDown(X + x, Y + y, button);
        }

        protected virtual void OnMouseUp(int x, int y, MouseButtonType button)
        {
            Parent?.OnMouseUp(X + x, Y + y, button);

            if (button == MouseButtonType.Right && !IsDisposed && !CanCloseWithRightClick && !Keyboard.Alt && !Keyboard.Shift && !Keyboard.Ctrl)
            {
                ContextMenu?.Show();
            }
        }

        protected virtual void OnMouseWheel(MouseEventType delta)
        {
            Parent?.OnMouseWheel(delta);
        }

        protected virtual void OnMouseOver(int x, int y)
        {
            Parent?.OnMouseOver(X + x, Y + y);
        }

        protected virtual void OnMouseEnter(int x, int y)
        {
        }

        protected virtual void OnMouseExit(int x, int y)
        {
        }

        protected virtual bool OnMouseDoubleClick(int x, int y, MouseButtonType button)
        {
            return Parent?.OnMouseDoubleClick(X + x, Y + y, button) ?? false;
        }

        protected virtual void OnDragBegin(int x, int y)
        {
        }

        protected virtual void OnDragEnd(int x, int y)
        {
        }

        protected virtual void OnTextInput(string c)
        {
        }

        protected virtual void OnKeyDown(SDL.SDL_Keycode key, SDL.SDL_Keymod mod)
        {
            Parent?.OnKeyDown(key, mod);
        }

        protected virtual void OnKeyUp(SDL.SDL_Keycode key, SDL.SDL_Keymod mod)
        {
            Parent?.OnKeyUp(key, mod);
        }

        public virtual bool Contains(int x, int y)
        {
            return !IsDisposed;
        }

        protected virtual void OnMove(int x, int y)
        {
        }

        internal virtual void OnFocusEnter()
        {
            if (!IsFocused)
            {
                IsFocused = true;
                FocusEnter.Raise(this);
                //Parent?.OnFocusEnter();
            }
        }

        internal virtual void OnFocusLost()
        {
            if (IsFocused)
            {
                IsFocused = false;
                FocusLost.Raise(this);
                //Parent?.OnFocusLeft();
            }
        }

        protected virtual void OnChildAdded()
        {
        }

        protected virtual void OnChildRemoved()
        {
        }

        protected virtual void CloseWithRightClick()
        {
            if (!CanCloseWithRightClick)
            {
                return;
            }

            Control parent = Parent;

            while (parent != null)
            {
                if (!parent.CanCloseWithRightClick)
                {
                    return;
                }

                parent = parent.Parent;
            }

            if (Parent == null)
            {
                Dispose();
            }
            else
            {
                Parent.CloseWithRightClick();
            }
        }

        public void KeyboardTabToNextFocus(Control c)
        {
            int startIndex = Children.IndexOf(c);

            for (int i = startIndex + 1; i < Children.Count; i++)
            {
                if (Children[i].AcceptKeyboardInput)
                {
                    Children[i].SetKeyboardFocus();

                    return;
                }
            }

            for (int i = 0; i < startIndex; i++)
            {
                if (Children[i].AcceptKeyboardInput)
                {
                    Children[i].SetKeyboardFocus();

                    return;
                }
            }
        }

        public virtual void OnButtonClick(int buttonID)
        {
            Parent?.OnButtonClick(buttonID);
        }

        public virtual void OnKeyboardReturn(int textID, string text)
        {
            Parent?.OnKeyboardReturn(textID, text);
        }

        public virtual void ChangePage(int pageIndex)
        {
            Parent?.ChangePage(pageIndex);
        }

        public virtual void Dispose()
        {
            if (IsDisposed)
            {
                return;
            }

            if (Children != null)
            {
                foreach (Control c in Children)
                {
                    c.Dispose();
                }

                Children.Clear();
            }

            IsDisposed = true;
            InvalidateDraw();
        }
    }
}