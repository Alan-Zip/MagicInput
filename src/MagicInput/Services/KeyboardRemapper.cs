using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MagicInput.Services;

public sealed class KeyboardRemapper : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const uint LlkHfInjected = 0x10;
    private const uint KeyEventFKeyUp = 0x0002;
    private const uint KeyEventFExtendedKey = 0x0001;
    private const uint MapVkToVsc = 0;

    private readonly LowLevelKeyboardProc _proc;
    private readonly HashSet<Keys> _swallowedFunctionRowKeys = new();
    private readonly HashSet<Keys> _swallowedScreenshotKeys = new();
    private readonly HashSet<Keys> _swallowedCommandDeleteKeys = new();
    private readonly HashSet<Keys> _swallowedCommandInfoKeys = new();
    private IntPtr _hookId = IntPtr.Zero;

    private bool _mediaRowEnabled;
    private bool _swapCommandControlEnabled;
    private SyntheticModifier _leftCommandState;
    private SyntheticModifier _rightCommandState;
    private bool _leftControlSentWin;
    private bool _rightControlSentWin;
    private bool _commandSpaceActive;
    private Keys _commandSpaceKey;

    public KeyboardRemapper()
    {
        _proc = HookCallback;
    }

    public string Status { get; private set; } = "Keyboard mapper is off.";

    public bool IsActive => _hookId != IntPtr.Zero;

    public void Configure(bool mediaRowEnabled, bool swapCommandControlEnabled)
    {
        _mediaRowEnabled = mediaRowEnabled;
        _swapCommandControlEnabled = swapCommandControlEnabled;

        if (_mediaRowEnabled || _swapCommandControlEnabled)
        {
            Start();
        }
        else
        {
            Stop();
            Status = "Keyboard mapper is off.";
        }
    }

    public void Dispose()
    {
        Stop();
    }

    private void Start()
    {
        if (_hookId != IntPtr.Zero)
        {
            Status = "Keyboard mapper is active.";
            return;
        }

        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule;
        var moduleHandle = module == null ? IntPtr.Zero : GetModuleHandle(module.ModuleName);
        _hookId = SetWindowsHookEx(WhKeyboardLl, _proc, moduleHandle, 0);
        Status = _hookId == IntPtr.Zero
            ? $"Keyboard mapper failed to start. Win32 error {Marshal.GetLastWin32Error()}."
            : "Keyboard mapper is active.";
    }

    private void Stop()
    {
        if (_hookId == IntPtr.Zero)
        {
            return;
        }

        ReleaseSyntheticModifiers();
        UnhookWindowsHookEx(_hookId);
        _hookId = IntPtr.Zero;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
        {
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        var message = wParam.ToInt32();
        var isKeyDown = message is WmKeyDown or WmSysKeyDown;
        var isKeyUp = message is WmKeyUp or WmSysKeyUp;
        if (!isKeyDown && !isKeyUp)
        {
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        var info = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
        if ((info.Flags & LlkHfInjected) != 0)
        {
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        var key = (Keys)info.VkCode;
        if (isKeyDown && IsDiagnosticKey(key, info.ScanCode))
        {
            LogKey($"seen {key} vk=0x{info.VkCode:X2} scan=0x{info.ScanCode:X2} flags=0x{info.Flags:X2}");
        }

        if (_mediaRowEnabled && TryHandleFunctionRowKey(key, info.ScanCode, info.Flags, isKeyDown, isKeyUp))
        {
            return (IntPtr)1;
        }

        if (TryHandleScreenshotShortcut(key, isKeyDown, isKeyUp))
        {
            return (IntPtr)1;
        }

        if (_swapCommandControlEnabled && TryHandleCommandDelete(key, isKeyDown, isKeyUp))
        {
            return (IntPtr)1;
        }

        if (_swapCommandControlEnabled && TryHandleCommandInfo(key, isKeyDown, isKeyUp))
        {
            return (IntPtr)1;
        }

        if (_swapCommandControlEnabled && key == Keys.Space && TryHandleCommandSpace(isKeyDown, isKeyUp))
        {
            return (IntPtr)1;
        }

        if (_swapCommandControlEnabled && TryHandleModifierSwap(key, isKeyDown, isKeyUp))
        {
            return (IntPtr)1;
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private bool TryHandleModifierSwap(Keys key, bool isKeyDown, bool isKeyUp)
    {
        if (TryMapCommandAsControlKey(key, out var mappedKey))
        {
            if (isKeyDown && GetCommandState(key) != SyntheticModifier.None)
            {
                return true;
            }

            if (isKeyDown)
            {
                if (SendKey(mappedKey, false, out var error))
                {
                    SetCommandState(key, SyntheticModifier.Control);
                    LogKey($"{key} -> {((Keys)mappedKey)} down");
                    return true;
                }

                LogKey($"{key} down injection failed: {error}");
                return false;
            }

            var state = GetCommandState(key);
            if (isKeyUp && state != SyntheticModifier.None)
            {
                var keyToRelease = state == SyntheticModifier.Win ? CommandToWinKey(key) : mappedKey;
                if (!SendKey(keyToRelease, true, out var error))
                {
                    LogKey($"{key} up injection failed: {error}");
                    return false;
                }

                SetCommandState(key, SyntheticModifier.None);
                LogKey($"{key} -> {((Keys)keyToRelease)} up");
                return true;
            }
        }

        if (TryMapControlAsCommandKey(key, out mappedKey))
        {
            if (isKeyDown && IsSyntheticControlMappedDown(key))
            {
                return true;
            }

            if (isKeyDown)
            {
                if (SendKey(mappedKey, false, out var error))
                {
                    SetSyntheticControlMappedState(key, true);
                    LogKey($"{key} -> {((Keys)mappedKey)} down");
                    return true;
                }

                LogKey($"{key} down injection failed: {error}");
                return false;
            }

            if (isKeyUp && IsSyntheticControlMappedDown(key))
            {
                if (!SendKey(mappedKey, true, out var error))
                {
                    LogKey($"{key} up injection failed: {error}");
                    return false;
                }

                SetSyntheticControlMappedState(key, false);
                LogKey($"{key} -> {((Keys)mappedKey)} up");
                return true;
            }
        }

        return false;
    }

    private bool TryHandleScreenshotShortcut(Keys key, bool isKeyDown, bool isKeyUp)
    {
        if (!TryGetScreenshotAction(key, out var action))
        {
            return false;
        }

        if (isKeyUp)
        {
            return _swallowedScreenshotKeys.Remove(key);
        }

        if (!isKeyDown)
        {
            return false;
        }

        if (_swallowedScreenshotKeys.Contains(key))
        {
            return true;
        }

        if (!IsCommandShortcutModifierDown() || !IsPhysicalShiftDown())
        {
            return false;
        }

        if (SendScreenshotAction(action, out var error))
        {
            _swallowedScreenshotKeys.Add(key);
            LogKey($"screenshot Shift+Cmd+{DescribeScreenshotDigit(action)} -> {DescribeScreenshotAction(action)}");
            return true;
        }

        LogKey($"screenshot Shift+Cmd+{DescribeScreenshotDigit(action)} failed: {error}");
        return false;
    }

    private bool TryHandleCommandDelete(Keys key, bool isKeyDown, bool isKeyUp)
    {
        if (key != Keys.Back)
        {
            return false;
        }

        if (isKeyUp)
        {
            return _swallowedCommandDeleteKeys.Remove(key);
        }

        if (!isKeyDown || !IsCommandShortcutModifierDown())
        {
            return false;
        }

        if (SendCommandDeleteAction(out var error))
        {
            _swallowedCommandDeleteKeys.Add(key);
            LogKey("Cmd+Delete -> Delete");
            return true;
        }

        LogKey($"Cmd+Delete failed: {error}");
        return false;
    }

    private bool TryHandleCommandInfo(Keys key, bool isKeyDown, bool isKeyUp)
    {
        if (key != Keys.I)
        {
            return false;
        }

        if (isKeyUp)
        {
            return _swallowedCommandInfoKeys.Remove(key);
        }

        if (!isKeyDown || !IsCommandShortcutModifierDown())
        {
            return false;
        }

        if (SendCommandInfoAction(out var error))
        {
            _swallowedCommandInfoKeys.Add(key);
            LogKey("Cmd+I -> Alt+Enter");
            return true;
        }

        LogKey($"Cmd+I failed: {error}");
        return false;
    }

    private bool TryHandleFunctionRowKey(Keys key, uint scanCode, uint flags, bool isKeyDown, bool isKeyUp)
    {
        if (!TryGetFunctionRowAction(key, scanCode, flags, out var action))
        {
            return false;
        }

        if (isKeyUp)
        {
            return _swallowedFunctionRowKeys.Remove(key);
        }

        if (!isKeyDown)
        {
            return false;
        }

        var repeatWhileHeld = action is FunctionRowAction.VolumeDown or FunctionRowAction.VolumeUp;
        if (!repeatWhileHeld && _swallowedFunctionRowKeys.Contains(key))
        {
            return true;
        }

        if (SendFunctionRowAction(action, out var error))
        {
            _swallowedFunctionRowKeys.Add(key);
            LogKey($"function {key} -> {DescribeFunctionRowAction(action)}");
            return true;
        }

        LogKey($"function {key} injection failed: {error}");
        return false;
    }

    private static bool TryGetFunctionRowAction(Keys key, uint scanCode, uint flags, out FunctionRowAction action)
    {
        if (IsAppleEjectKey(key, scanCode, flags))
        {
            action = FunctionRowAction.PreviousWindow;
            return true;
        }

        action = key switch
        {
            Keys.F1 => FunctionRowAction.Dictation,
            Keys.F3 => FunctionRowAction.TaskView,
            Keys.F4 => FunctionRowAction.StartMenu,
            Keys.F5 => FunctionRowAction.PreviousWindow,
            Keys.F6 => FunctionRowAction.ShowDesktop,
            Keys.F7 => FunctionRowAction.MediaPreviousTrack,
            Keys.F8 => FunctionRowAction.MediaPlayPause,
            Keys.F9 => FunctionRowAction.MediaNextTrack,
            Keys.F10 => FunctionRowAction.VolumeMute,
            Keys.F11 => FunctionRowAction.VolumeDown,
            Keys.F12 => FunctionRowAction.VolumeUp,
            _ => FunctionRowAction.None
        };

        return action != FunctionRowAction.None;
    }

    private static bool SendFunctionRowAction(FunctionRowAction action, out string error)
    {
        return action switch
        {
            FunctionRowAction.PreviousWindow => SendChord((ushort)Keys.Menu, (ushort)Keys.Tab, out error),
            FunctionRowAction.TaskView => SendChord((ushort)Keys.LWin, (ushort)Keys.Tab, out error),
            FunctionRowAction.StartMenu => SendKeyPress((ushort)Keys.LWin, out error),
            FunctionRowAction.Dictation => SendChord((ushort)Keys.LWin, (ushort)Keys.H, out error),
            FunctionRowAction.ShowDesktop => SendChord((ushort)Keys.LWin, (ushort)Keys.D, out error),
            FunctionRowAction.MediaPreviousTrack => SendKeyPress(0xB1, out error),
            FunctionRowAction.MediaPlayPause => SendKeyPress(0xB3, out error),
            FunctionRowAction.MediaNextTrack => SendKeyPress(0xB0, out error),
            FunctionRowAction.VolumeMute => SendKeyPress(0xAD, out error),
            FunctionRowAction.VolumeDown => SendKeyPress(0xAE, out error),
            FunctionRowAction.VolumeUp => SendKeyPress(0xAF, out error),
            _ => FailUnknownAction(out error)
        };
    }

    private static bool FailUnknownAction(out string error)
    {
        error = "Unknown function-row action.";
        return false;
    }

    private static string DescribeFunctionRowAction(FunctionRowAction action)
    {
        return action switch
        {
            FunctionRowAction.PreviousWindow => "Alt+Tab",
            FunctionRowAction.TaskView => "Win+Tab",
            FunctionRowAction.StartMenu => "Win",
            FunctionRowAction.Dictation => "Win+H",
            FunctionRowAction.ShowDesktop => "Win+D",
            FunctionRowAction.MediaPreviousTrack => "previous track",
            FunctionRowAction.MediaPlayPause => "play/pause",
            FunctionRowAction.MediaNextTrack => "next track",
            FunctionRowAction.VolumeMute => "mute",
            FunctionRowAction.VolumeDown => "volume down",
            FunctionRowAction.VolumeUp => "volume up",
            _ => "unknown"
        };
    }

    private static bool TryGetScreenshotAction(Keys key, out ScreenshotAction action)
    {
        action = key switch
        {
            Keys.D3 or Keys.NumPad3 => ScreenshotAction.FullScreen,
            Keys.D4 or Keys.NumPad4 => ScreenshotAction.AreaSnip,
            Keys.D5 or Keys.NumPad5 => ScreenshotAction.SnippingTool,
            _ => ScreenshotAction.None
        };

        return action != ScreenshotAction.None;
    }

    private static bool IsAppleEjectKey(Keys key, uint scanCode, uint flags)
    {
        var virtualKey = (int)key;
        var isExtended = (flags & 0x01) != 0;

        return key is (Keys.F24 or Keys.Sleep or Keys.LaunchApplication2 or Keys.OemClear)
            || virtualKey == 0xFF
            || (isExtended && (scanCode is 0x6C or 0x16C));
    }

    private bool SendScreenshotAction(ScreenshotAction action, out string error)
    {
        var releasedControls = ReleaseActiveCommandControls();
        var releasedShifts = ReleasePhysicalShiftKeys();

        try
        {
            return action switch
            {
                ScreenshotAction.FullScreen => SendChord([(ushort)Keys.LWin], (ushort)Keys.PrintScreen, out error),
                ScreenshotAction.AreaSnip => SendChord([(ushort)Keys.LWin, (ushort)Keys.LShiftKey], (ushort)Keys.S, out error),
                ScreenshotAction.SnippingTool => LaunchSnippingTool(out error),
                _ => FailUnknownAction(out error)
            };
        }
        finally
        {
            RestorePhysicalShiftKeys(releasedShifts);
            RestoreActiveCommandControls(releasedControls);
        }
    }

    private bool SendCommandDeleteAction(out string error)
    {
        var releasedControls = ReleaseActiveCommandControls();

        try
        {
            return SendKeyPress((ushort)Keys.Delete, out error);
        }
        finally
        {
            RestoreActiveCommandControls(releasedControls);
        }
    }

    private bool SendCommandInfoAction(out string error)
    {
        var releasedControls = ReleaseActiveCommandControls();

        try
        {
            return SendChord((ushort)Keys.Menu, (ushort)Keys.Enter, out error);
        }
        finally
        {
            RestoreActiveCommandControls(releasedControls);
        }
    }

    private List<ushort> ReleaseActiveCommandControls()
    {
        var released = new List<ushort>();
        if (_leftCommandState == SyntheticModifier.Control)
        {
            var controlKey = (ushort)Keys.LControlKey;
            SendKey(controlKey, true, out _);
            released.Add(controlKey);
        }

        if (_rightCommandState == SyntheticModifier.Control)
        {
            var controlKey = (ushort)Keys.RControlKey;
            SendKey(controlKey, true, out _);
            released.Add(controlKey);
        }

        return released;
    }

    private void RestoreActiveCommandControls(IEnumerable<ushort> releasedControls)
    {
        foreach (var controlKey in releasedControls)
        {
            var stillHeld = controlKey == (ushort)Keys.RControlKey
                ? _rightCommandState == SyntheticModifier.Control
                : _leftCommandState == SyntheticModifier.Control;
            if (stillHeld)
            {
                SendKey(controlKey, false, out _);
            }
        }
    }

    private static List<ushort> ReleasePhysicalShiftKeys()
    {
        var released = new List<ushort>();
        foreach (var shiftKey in new[] { (ushort)Keys.LShiftKey, (ushort)Keys.RShiftKey })
        {
            if (IsKeyCurrentlyDown((Keys)shiftKey))
            {
                SendKey(shiftKey, true, out _);
                released.Add(shiftKey);
            }
        }

        return released;
    }

    private static void RestorePhysicalShiftKeys(IEnumerable<ushort> releasedShifts)
    {
        foreach (var shiftKey in releasedShifts)
        {
            SendKey(shiftKey, false, out _);
        }
    }

    private static bool LaunchSnippingTool(out string error)
    {
        try
        {
            Process.Start(new ProcessStartInfo("snippingtool.exe") { UseShellExecute = true });
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string DescribeScreenshotDigit(ScreenshotAction action)
    {
        return action switch
        {
            ScreenshotAction.FullScreen => "3",
            ScreenshotAction.AreaSnip => "4",
            ScreenshotAction.SnippingTool => "5",
            _ => "?"
        };
    }

    private static string DescribeScreenshotAction(ScreenshotAction action)
    {
        return action switch
        {
            ScreenshotAction.FullScreen => "Win+PrintScreen",
            ScreenshotAction.AreaSnip => "Win+Shift+S",
            ScreenshotAction.SnippingTool => "Snipping Tool",
            _ => "unknown"
        };
    }

    private static bool TryMapCommandAsControlKey(Keys key, out ushort mappedKey)
    {
        mappedKey = key switch
        {
            Keys.LWin => (ushort)Keys.LControlKey,
            Keys.RWin => (ushort)Keys.RControlKey,
            _ => 0
        };

        return mappedKey != 0;
    }

    private static bool TryMapControlAsCommandKey(Keys key, out ushort mappedKey)
    {
        mappedKey = key switch
        {
            Keys.LControlKey => (ushort)Keys.LWin,
            Keys.RControlKey => (ushort)Keys.RWin,
            Keys.ControlKey => (ushort)Keys.LWin,
            _ => 0
        };

        return mappedKey != 0;
    }

    private static bool SendKeyPress(ushort virtualKey, out string error)
    {
        if (!SendKey(virtualKey, false, out error))
        {
            return false;
        }

        return SendKey(virtualKey, true, out error);
    }

    private static bool SendChord(ushort modifierKey, ushort key, out string error)
    {
        return SendChord([modifierKey], key, out error);
    }

    private static bool SendChord(IReadOnlyList<ushort> modifierKeys, ushort key, out string error)
    {
        var pressedModifiers = new List<ushort>();
        foreach (var modifierKey in modifierKeys)
        {
            if (!SendKey(modifierKey, false, out error))
            {
                ReleaseKeys(pressedModifiers);
                return false;
            }

            pressedModifiers.Add(modifierKey);
        }

        if (!SendKey(key, false, out error))
        {
            ReleaseKeys(pressedModifiers);
            return false;
        }

        if (!SendKey(key, true, out error))
        {
            ReleaseKeys(pressedModifiers);
            return false;
        }

        for (var i = pressedModifiers.Count - 1; i >= 0; i--)
        {
            if (!SendKey(pressedModifiers[i], true, out error))
            {
                return false;
            }
        }

        return true;
    }

    private static void ReleaseKeys(IEnumerable<ushort> keys)
    {
        foreach (var key in keys.Reverse())
        {
            SendKey(key, true, out _);
        }
    }

    private static bool SendKey(ushort virtualKey, bool keyUp, out string error)
    {
        error = "";
        var input = new Input
        {
            Type = 1,
            U = new InputUnion
            {
                Ki = new KeyboardInput
                {
                    WVk = virtualKey,
                    WScan = 0,
                    DwFlags = (IsExtendedKey(virtualKey) ? KeyEventFExtendedKey : 0) | (keyUp ? KeyEventFKeyUp : 0),
                    Time = 0,
                    DwExtraInfo = UIntPtr.Zero
                }
            }
        };

        if (SendInput(1, new[] { input }, Marshal.SizeOf<Input>()) == 1)
        {
            return true;
        }

        var sendInputError = Marshal.GetLastWin32Error();
        var fallbackFlags = (IsExtendedKey(virtualKey) ? KeyEventFExtendedKey : 0) | (keyUp ? KeyEventFKeyUp : 0);
        var scanCode = (byte)MapVirtualKey(virtualKey, MapVkToVsc);
        keybd_event((byte)virtualKey, scanCode, fallbackFlags, UIntPtr.Zero);
        error = $"SendInput failed with Win32 error {sendInputError}; used keybd_event fallback.";
        return true;
    }

    private static bool IsExtendedKey(ushort virtualKey)
    {
        return virtualKey is 0x2E or 0x5B or 0x5C or 0xA3 or 0xAD or 0xAE or 0xAF or 0xB0 or 0xB1 or 0xB3;
    }

    private static bool IsDiagnosticKey(Keys key, uint scanCode)
    {
        return key is >= Keys.F1 and <= Keys.F6
            or >= Keys.F7 and <= Keys.F12
            or >= Keys.F13 and <= Keys.F24
            or Keys.D3
            or Keys.D4
            or Keys.D5
            or Keys.NumPad3
            or Keys.NumPad4
            or Keys.NumPad5
            or Keys.LWin
            or Keys.RWin
            or Keys.LControlKey
            or Keys.RControlKey
            or Keys.ControlKey
            or Keys.Space
            or Keys.Back
            or Keys.Delete
            or Keys.I
            or Keys.VolumeMute
            or Keys.VolumeDown
            or Keys.VolumeUp
            or Keys.MediaPreviousTrack
            or Keys.MediaPlayPause
            or Keys.MediaNextTrack
            or Keys.Sleep
            or Keys.LaunchApplication1
            or Keys.LaunchApplication2
            or Keys.SelectMedia
            or Keys.OemClear
            || (int)key == 0xFF
            || (scanCode is 0x6C or 0x16C);
    }

    private bool IsCommandShortcutModifierDown()
    {
        if (_swapCommandControlEnabled)
        {
            return _leftCommandState != SyntheticModifier.None || _rightCommandState != SyntheticModifier.None;
        }

        return IsKeyCurrentlyDown(Keys.LWin) || IsKeyCurrentlyDown(Keys.RWin);
    }

    private static bool IsPhysicalShiftDown()
    {
        return IsKeyCurrentlyDown(Keys.LShiftKey) || IsKeyCurrentlyDown(Keys.RShiftKey) || IsKeyCurrentlyDown(Keys.ShiftKey);
    }

    private static bool IsKeyCurrentlyDown(Keys key)
    {
        return (GetAsyncKeyState((int)key) & 0x8000) != 0;
    }

    private bool TryHandleCommandSpace(bool isKeyDown, bool isKeyUp)
    {
        if (isKeyDown && _commandSpaceActive)
        {
            return true;
        }

        if (isKeyDown && TryGetCommandInState(SyntheticModifier.Control, out var commandKey))
        {
            var controlKey = CommandToControlKey(commandKey);
            var winKey = CommandToWinKey(commandKey);

            if (!SendKey(controlKey, true, out var error))
            {
                LogKey($"{commandKey}+Space failed to release Ctrl: {error}");
                return false;
            }

            if (!SendKey(winKey, false, out error))
            {
                SendKey(controlKey, false, out _);
                LogKey($"{commandKey}+Space failed to press Win: {error}");
                return false;
            }

            if (!SendKey((ushort)Keys.Space, false, out error))
            {
                SendKey(winKey, true, out _);
                SendKey(controlKey, false, out _);
                LogKey($"{commandKey}+Space failed to press Space: {error}");
                return false;
            }

            SetCommandState(commandKey, SyntheticModifier.Win);
            _commandSpaceActive = true;
            _commandSpaceKey = commandKey;
            LogKey($"{commandKey}+Space -> Win+Space down");
            return true;
        }

        if (isKeyUp && _commandSpaceActive)
        {
            SendKey((ushort)Keys.Space, true, out _);

            if (GetCommandState(_commandSpaceKey) == SyntheticModifier.Win)
            {
                var winKey = CommandToWinKey(_commandSpaceKey);
                var controlKey = CommandToControlKey(_commandSpaceKey);
                SendKey(winKey, true, out _);
                SendKey(controlKey, false, out _);
                SetCommandState(_commandSpaceKey, SyntheticModifier.Control);
            }

            LogKey($"{_commandSpaceKey}+Space -> Win+Space up");
            _commandSpaceActive = false;
            _commandSpaceKey = Keys.None;
            return true;
        }

        return false;
    }

    private bool TryGetCommandInState(SyntheticModifier state, out Keys key)
    {
        if (_leftCommandState == state)
        {
            key = Keys.LWin;
            return true;
        }

        if (_rightCommandState == state)
        {
            key = Keys.RWin;
            return true;
        }

        key = Keys.None;
        return false;
    }

    private SyntheticModifier GetCommandState(Keys key)
    {
        return key switch
        {
            Keys.LWin => _leftCommandState,
            Keys.RWin => _rightCommandState,
            _ => SyntheticModifier.None
        };
    }

    private void SetCommandState(Keys key, SyntheticModifier state)
    {
        if (key == Keys.LWin)
        {
            _leftCommandState = state;
        }
        else if (key == Keys.RWin)
        {
            _rightCommandState = state;
        }
    }

    private static ushort CommandToControlKey(Keys key)
    {
        return key == Keys.RWin ? (ushort)Keys.RControlKey : (ushort)Keys.LControlKey;
    }

    private static ushort CommandToWinKey(Keys key)
    {
        return key == Keys.RWin ? (ushort)Keys.RWin : (ushort)Keys.LWin;
    }

    private bool IsSyntheticControlMappedDown(Keys key)
    {
        return key switch
        {
            Keys.LControlKey or Keys.ControlKey => _leftControlSentWin,
            Keys.RControlKey => _rightControlSentWin,
            _ => false
        };
    }

    private void SetSyntheticControlMappedState(Keys key, bool down)
    {
        if (key is Keys.LControlKey or Keys.ControlKey)
        {
            _leftControlSentWin = down;
        }
        else if (key == Keys.RControlKey)
        {
            _rightControlSentWin = down;
        }
    }

    private void ReleaseSyntheticModifiers()
    {
        if (_commandSpaceActive)
        {
            SendKey((ushort)Keys.Space, true, out _);
            _commandSpaceActive = false;
            _commandSpaceKey = Keys.None;
        }

        ReleaseCommandSide(Keys.LWin);
        ReleaseCommandSide(Keys.RWin);

        if (_leftControlSentWin)
        {
            SendKey((ushort)Keys.LWin, true, out _);
            _leftControlSentWin = false;
        }

        if (_rightControlSentWin)
        {
            SendKey((ushort)Keys.RWin, true, out _);
            _rightControlSentWin = false;
        }

        _swallowedFunctionRowKeys.Clear();
        _swallowedScreenshotKeys.Clear();
        _swallowedCommandDeleteKeys.Clear();
        _swallowedCommandInfoKeys.Clear();
    }

    private void ReleaseCommandSide(Keys key)
    {
        var state = GetCommandState(key);
        if (state == SyntheticModifier.None)
        {
            return;
        }

        var keyToRelease = state == SyntheticModifier.Win ? CommandToWinKey(key) : CommandToControlKey(key);
        SendKey(keyToRelease, true, out _);
        SetCommandState(key, SyntheticModifier.None);
    }

    private static void LogKey(string message)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MagicInput");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "keyboard.log"), $"{DateTime.Now:O} {message}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics must never affect input handling.
        }
    }

    private enum SyntheticModifier
    {
        None,
        Control,
        Win
    }

    private enum FunctionRowAction
    {
        None,
        PreviousWindow,
        TaskView,
        StartMenu,
        Dictation,
        ShowDesktop,
        MediaPreviousTrack,
        MediaPlayPause,
        MediaNextTrack,
        VolumeMute,
        VolumeDown,
        VolumeUp
    }

    private enum ScreenshotAction
    {
        None,
        FullScreen,
        AreaSnip,
        SnippingTool
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr DwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort WVk;
        public ushort WScan;
        public uint DwFlags;
        public uint Time;
        public UIntPtr DwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public KeyboardInput Ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion U;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, Input[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
}
