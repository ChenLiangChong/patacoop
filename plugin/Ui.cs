using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace PataCoop;

/// <summary>
/// The few widgets the panel needs. The game's build strips IMGUI's buttons and text fields; boxes,
/// labels and textures remain, so these draw with those and read the mouse and keyboard from the
/// IMGUI event themselves. Only call them from OnGUI.
/// </summary>
internal static class Ui
{
    internal static readonly Color Back = new(0f, 0f, 0f, 0.72f), Line = new(1f, 1f, 1f, 0.15f);
    private static readonly Color ButtonColor = new(0.22f, 0.40f, 0.62f, 0.95f), ButtonHover = new(0.30f, 0.52f, 0.80f, 1f),
        ButtonOff = new(0.25f, 0.25f, 0.25f, 0.9f), FieldColor = new(1f, 1f, 1f, 0.12f), FieldFocus = new(1f, 1f, 1f, 0.24f);
    internal static readonly Color Dim = new(0.75f, 0.75f, 0.75f), Good = new(0.55f, 0.95f, 0.55f), Warn = new(1f, 0.85f, 0.4f);

    private static GUIStyle? _left, _center, _block, _fill;
    private static float _fontScale = -1;

    /// <summary>The text field being typed in (null: none). Game keys are off while one is.</summary>
    internal static string? Focus { get; private set; }
    private static int _focusDrawnFrame;
    private static float _lastTyped, _focusLost;
    // a field nobody types in lets go after a while (a gamepad player who clicked it by accident)
    private const float FieldIdle = 20f, KeysBackAfter = 2f;

    /// <summary>Panel scale: 1 at 1080p and below, more on bigger screens.</summary>
    internal static float U => Mathf.Clamp(Screen.height / 1080f, 1f, 2f);

    private static void Styles()
    {
        if (_left != null && _fontScale == U) return;
        _fontScale = U;
        _left = Make(TextAnchor.MiddleLeft);
        _center = Make(TextAnchor.MiddleCenter);
        _block = Make(TextAnchor.UpperLeft);
        _fill = Make(TextAnchor.MiddleCenter);
        _fill.normal.background = White();
    }

    private static readonly Dictionary<int, GUIStyle> Outline = new();

    /// <summary>Centred text with a dark outline, readable over the battlefield.</summary>
    internal static void Outlined(Rect r, string text, Color color, float size)
    {
        if (Event.current.type != EventType.Repaint) return;
        int px = Mathf.Max(8, Mathf.RoundToInt(size));
        if (!Outline.TryGetValue(px, out var style))
        {
            style = new GUIStyle { alignment = TextAnchor.MiddleCenter, fontSize = px, fontStyle = FontStyle.Bold, wordWrap = false, richText = false, clipping = TextClipping.Overflow };
            style.normal.textColor = Color.white;
            Il2CppInterop.Runtime.IL2CPP.il2cpp_gchandle_new(style.Pointer, false);
            Outline[px] = style;
        }
        var content = new GUIContent(text);
        var old = GUI.contentColor;
        GUI.contentColor = new Color(0f, 0f, 0f, 0.85f);
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
                if (dx != 0 || dy != 0) GUI.Label(new Rect(r.x + dx, r.y + dy, r.width, r.height), content, style);
        GUI.contentColor = color;
        GUI.Label(r, content, style);
        GUI.contentColor = old;
    }

    /// <summary>Height of one line of text in <see cref="Block"/>.</summary>
    internal static float LineHeight => 20 * U;

    /// <summary>Several lines of text from the top-left corner of <paramref name="r"/>.</summary>
    internal static void Block(Rect r, string text)
    {
        Styles();
        if (Event.current.type == EventType.Repaint) GUI.Label(r, new GUIContent(text), _block);
    }

    private static GUIStyle Make(TextAnchor anchor)
    {
        var s = new GUIStyle { alignment = anchor, fontSize = Mathf.RoundToInt(15 * U), wordWrap = false, richText = false, clipping = TextClipping.Clip };
        s.normal.textColor = Color.white;
        // only our side holds it: without a strong handle the game's garbage collector frees it
        Il2CppInterop.Runtime.IL2CPP.il2cpp_gchandle_new(s.Pointer, false);
        return s;
    }

    private static Texture2D? _white;

    /// <summary>
    /// A 1x1 white texture for flat fills, drawn as a box background: GUI.DrawTexture* exists only
    /// as a rebuilt copy in this game that fails on its own argument object.
    /// </summary>
    private static Texture2D White()
    {
        if (_white != null && _white.WasCollected == false) return _white;
        var t = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
        t.SetPixel(0, 0, Color.white);
        t.Apply();
        UnityEngine.Object.DontDestroyOnLoad(t);
        Il2CppInterop.Runtime.IL2CPP.il2cpp_gchandle_new(t.Pointer, false);
        return _white = t;
    }

    internal static void Fill(Rect r, Color c)
    {
        Styles();
        if (Event.current.type != EventType.Repaint) return;
        var old = GUI.color;
        GUI.color = c;
        GUI.Box(r, GUIContent.none, _fill);
        GUI.color = old;
    }

    internal static void Label(Rect r, string text, Color? color = null, bool center = false)
    {
        Styles();
        if (Event.current.type != EventType.Repaint) return;
        var old = GUI.contentColor;
        GUI.contentColor = color ?? Color.white;
        GUI.Label(r, new GUIContent(text), center ? _center : _left);
        GUI.contentColor = old;
    }

    internal static bool Button(Rect r, string label, bool enabled = true)
    {
        var e = Event.current;
        bool hover = enabled && r.Contains(e.mousePosition);
        if (e.type == EventType.Repaint)
        {
            Fill(r, !enabled ? ButtonOff : hover ? ButtonHover : ButtonColor);
            Label(r, label, enabled ? Color.white : Dim, center: true);
        }
        if (enabled && hover && e.type == EventType.MouseDown && e.button == 0)
        {
            e.Use();
            return true;
        }
        return false;
    }

    /// <summary>
    /// A one-line text field. Click to type; Enter (sets <paramref name="submitted"/>), Esc or a click
    /// elsewhere stops; Backspace deletes, Ctrl+V pastes. <paramref name="allowed"/> filters characters.
    /// </summary>
    internal static string Field(Rect r, string id, string value, int max, Func<char, bool> allowed, out bool submitted, bool ime = false)
    {
        submitted = false;
        var e = Event.current;
        bool focused = Focus == id;
        if (e.type == EventType.MouseDown && e.button == 0 && r.Contains(e.mousePosition))
        {
            if (!focused) SetFocus(id, ime);
            focused = true;
            e.Use();
        }
        if (focused)
        {
            _focusDrawnFrame = Time.frameCount;
            if (e.type == EventType.KeyDown)
            {
                _lastTyped = Time.unscaledTime;
                bool ctrl = (e.modifiers & EventModifiers.Control) != 0;
                switch (e.keyCode)
                {
                    case KeyCode.Backspace:
                        if (value.Length > 0) value = value[..^1];
                        break;
                    case KeyCode.Return or KeyCode.KeypadEnter:
                        submitted = true;
                        SetFocus(null);
                        break;
                    case KeyCode.Escape:
                        SetFocus(null);
                        break;
                    case KeyCode.V when ctrl:
                        foreach (char c in GUIUtility.systemCopyBuffer ?? "")
                            if (value.Length < max && !char.IsControl(c) && allowed(c)) value += c;
                        value = value.Trim();
                        break;
                    default:
                        char ch = e.character;
                        if (!ctrl && ch != '\0' && !char.IsControl(ch) && value.Length < max && allowed(ch)) value += ch;
                        break;
                }
                e.Use();
            }
        }
        if (e.type == EventType.Repaint)
        {
            Fill(r, focused ? FieldFocus : FieldColor);
            bool caret = focused && (int)(Time.unscaledTime * 2) % 2 == 0;
            Label(new Rect(r.x + 6 * U, r.y, r.width - 12 * U, r.height), value + (caret ? "|" : ""));
        }
        return value;
    }

    /// <summary>Call after drawing every widget: a click that no widget took ends typing.</summary>
    internal static void EndPanel()
    {
        var e = Event.current;
        if (e.type == EventType.MouseDown && Focus != null) SetFocus(null);
    }

    /// <summary>Where the panel is on screen (GUI coordinates), set while drawing it.</summary>
    internal static Rect Panel;

    /// <summary>The mouse pointer is over the panel and visible.</summary>
    internal static bool Pointing
    {
        get
        {
            var mouse = Mouse.current;
            if (mouse == null || !Cursor.visible) return false;
            var p = mouse.position.ReadValue();
            return Panel.Contains(new Vector2(p.x, Screen.height - p.y));
        }
    }

    /// <summary>
    /// Per frame from Update. A field no longer on screen, left alone for a while, or abandoned for
    /// the gamepad stops being typed in. The game's keys are off while typing (its menus take letters,
    /// Enter and Esc) and come back once no key is held, so the Enter that ends typing does not also
    /// confirm something in the game (or after a moment anyway, should a key seem stuck). Pointing
    /// at the panel only keeps the game's "any button" (the left mouse button) from reacting to clicks.
    /// </summary>
    internal static void Tick()
    {
        if (Focus != null && (Time.frameCount - _focusDrawnFrame > 2 || Time.unscaledTime - _lastTyped > FieldIdle || GamepadPressed())) SetFocus(null);
        if (Focus != null) GameKeys.Enabled = false;
        else if (!GameKeys.Enabled && (!Held() || Time.unscaledTime - _focusLost > KeysBackAfter)) GameKeys.Enabled = true;
        GameKeys.ClicksEnabled = !(Pointing && PanelCursor.InUse);
    }

    private static bool GamepadPressed()
    {
        var pad = Gamepad.current;
        return pad != null && (pad.buttonSouth.wasPressedThisFrame || pad.buttonEast.wasPressedThisFrame || pad.startButton.wasPressedThisFrame
            || pad.dpad.up.wasPressedThisFrame || pad.dpad.down.wasPressedThisFrame || pad.dpad.left.wasPressedThisFrame || pad.dpad.right.wasPressedThisFrame);
    }

    private static bool Held()
    {
        var kb = Keyboard.current;
        var mouse = Mouse.current;
        return (kb != null && kb.anyKey.isPressed) || (mouse != null && mouse.leftButton.isPressed);
    }

    private static void SetFocus(string? id, bool ime = false)
    {
        if (Focus != null && id == null) _focusLost = Time.unscaledTime;
        Focus = id;
        if (id != null)
        {
            _lastTyped = Time.unscaledTime;
            GameKeys.Enabled = false;
        }
        try { Keyboard.current?.SetIMEEnabled(id != null && ime); }
        catch (Exception e) { CoopPlugin.L.LogDebug("IME switch failed: " + e.Message); }
    }
}

/// <summary>
/// The game hides and locks the mouse cursor. Moving the mouse outside battle shows it so the
/// panel can be clicked; a few seconds without moving it (and not typing) hide it again.
/// </summary>
internal static class PanelCursor
{
    private const float HideAfter = 4f;
    private static bool _shown, _wasVisible;
    private static CursorLockMode _wasLock;
    private static float _lastMove;

    /// <summary>The mouse moved or clicked in the last few seconds.</summary>
    internal static bool InUse => _lastMove > 0 && Time.unscaledTime - _lastMove < HideAfter;

    internal static void Tick(bool allowed)
    {
        var mouse = Mouse.current;
        if (mouse != null && (mouse.delta.ReadValue().sqrMagnitude > 1f || mouse.leftButton.isPressed)) _lastMove = Time.unscaledTime;
        bool recent = InUse;
        if (!_shown && allowed && recent && (!Cursor.visible || Cursor.lockState != CursorLockMode.None))
        {
            _wasVisible = Cursor.visible;
            _wasLock = Cursor.lockState;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            _shown = true;
        }
        else if (_shown && (!allowed || (!recent && Ui.Focus == null)))
        {
            Cursor.lockState = _wasLock;
            Cursor.visible = _wasVisible;
            _shown = false;
        }
    }
}

/// <summary>Turns the game's own key handling off while the player types in, or clicks on, the panel.</summary>
internal static class GameKeys
{
    private static readonly List<InputActionMap> Paused = new();
    private static InputAction? _anyButton;

    private static InputActionAsset? Actions() => SingletonMonobehaviour<MultiPlatformInputManager>.Instance?.playerInput?.actions;

    /// <summary>
    /// The game's "any button" action (bound to the left mouse button, among others), off while the
    /// pointer is on the panel so a click there does not also reach the game. It comes back only if
    /// its menu map is still on: a map the game switched off meanwhile stays the game's business.
    /// </summary>
    internal static bool ClicksEnabled
    {
        get => _anyButton == null;
        set
        {
            try
            {
                if (!value && _anyButton == null)
                {
                    var any = Actions()?.FindAction("Menu_Any");
                    if (any != null && any.enabled)
                    {
                        any.Disable();
                        _anyButton = any;
                    }
                }
                else if (value && _anyButton != null)
                {
                    var any = _anyButton;
                    _anyButton = null;
                    if (any.actionMap != null && any.actionMap.enabled) any.Enable();
                }
            }
            catch (Exception e)
            {
                CoopPlugin.L.LogWarning("could not switch the game's any-button action: " + e.Message);
                _anyButton = null;
            }
        }
    }

    internal static bool Enabled
    {
        get => Paused.Count == 0;
        set
        {
            try
            {
                if (!value && Paused.Count == 0)
                {
                    var maps = Actions()?.m_ActionMaps;
                    if (maps == null) return;
                    foreach (var map in maps)
                        if (map != null && map.enabled)
                        {
                            map.Disable();
                            Paused.Add(map);
                        }
                }
                else if (value)
                {
                    foreach (var map in Paused) map.Enable();
                    Paused.Clear();
                }
            }
            catch (Exception e)
            {
                CoopPlugin.L.LogWarning("could not switch the game's keys " + (value ? "on" : "off") + ": " + e.Message);
                // never leave the game without keys
                foreach (var map in Paused)
                    try { map.Enable(); } catch (Exception) { }
                Paused.Clear();
            }
        }
    }
}
