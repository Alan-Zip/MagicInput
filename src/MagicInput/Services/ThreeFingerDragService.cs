using System.Runtime.InteropServices;
using MagicInput.Models;
using Microsoft.Win32;

namespace MagicInput.Services;

// Precision Touchpad Raw Input parsing follows the MIT-licensed approach used by
// ThreeFingerDragOnWindows / RawInput.Touchpad. See THIRD_PARTY_NOTICES.md.
public sealed class ThreeFingerDragService : NativeWindow, IDisposable
{
    private const int WmInput = 0x00FF;
    private const int WmInputDeviceChange = 0x00FE;
    private const int ReleaseAfterNoContactsMs = 450;
    private const int ReleaseGapMs = 90;

    private readonly TouchpadContactManager _contactManager;
    private readonly ThreeFingerDragRecognizer _dragRecognizer = new();
    private readonly BottomLeftTapRecognizer _bottomLeftTapRecognizer = new();
    private readonly System.Windows.Forms.Timer _releaseTimer = new();

    private TouchpadContact[] _previousContacts = [];
    private long _lastContactAt = Environment.TickCount64;
    private bool _enabled;
    private bool _registered;
    private bool _threeFingerDragEnabled;
    private CornerTapAction _bottomLeftTapAction = CornerTapAction.Off;

    public ThreeFingerDragService()
    {
        _contactManager = new TouchpadContactManager(OnTouchpadContacts);
        _releaseTimer.Interval = ReleaseAfterNoContactsMs;
        _releaseTimer.Tick += (_, _) =>
        {
            _releaseTimer.Stop();
            _dragRecognizer.Release();
        };
    }

    public string Status { get; private set; } = "Touchpad gestures are off.";

    public bool IsActive => _enabled && _registered;

    public void Configure(bool threeFingerDragEnabled, CornerTapAction bottomLeftTapAction)
    {
        _threeFingerDragEnabled = threeFingerDragEnabled;
        _bottomLeftTapAction = bottomLeftTapAction;
        _dragRecognizer.Enabled = threeFingerDragEnabled;
        _bottomLeftTapRecognizer.Configure(bottomLeftTapAction);

        if (threeFingerDragEnabled || bottomLeftTapAction != CornerTapAction.Off)
        {
            Start();
            return;
        }

        Stop();
    }

    public void Dispose()
    {
        Stop();
        _releaseTimer.Dispose();
        if (Handle != IntPtr.Zero)
        {
            DestroyHandle();
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (_enabled && m.Msg == WmInput)
        {
            var report = RawTouchpadInput.ParseInput(m.LParam);
            if (report != null)
            {
                _contactManager.Receive(report.Value.Device, report.Value.Contacts, report.Value.ContactCount, report.Value.Bounds);
            }
        }
        else if (_enabled && m.Msg == WmInputDeviceChange)
        {
            Status = RawTouchpadInput.TouchpadExists()
                ? ActiveStatus()
                : "No Precision Touchpad raw input device found.";
        }

        base.WndProc(ref m);
    }

    private void Start()
    {
        _enabled = true;
        EnsureHandle();
        if (_threeFingerDragEnabled)
        {
            PrecisionTouchpadGestureSettings.ReserveThreeFingerGesturesForDrag();
        }

        var touchpadExists = RawTouchpadInput.TouchpadExists();
        _registered = RawTouchpadInput.RegisterInput(Handle);
        Status = touchpadExists
            ? _registered
                ? ActiveStatus()
                : $"Touchpad gestures could not register raw input. Win32 error {Marshal.GetLastWin32Error()}."
            : "No Precision Touchpad raw input device found.";
        Log(Status);
    }

    private void Stop()
    {
        _enabled = false;
        _registered = false;
        _releaseTimer.Stop();
        _dragRecognizer.Release();
        _bottomLeftTapRecognizer.Reset();
        _previousContacts = [];
        RawTouchpadInput.UnregisterInput();
        Status = "Touchpad gestures are off.";
        Log(Status);
    }

    private void EnsureHandle()
    {
        if (Handle != IntPtr.Zero)
        {
            return;
        }

        CreateHandle(new CreateParams { Caption = "MagicInputThreeFingerDragSink" });
    }

    private void OnTouchpadContacts(IntPtr device, IReadOnlyList<TouchpadContact> contacts, TouchpadBounds bounds)
    {
        var now = Environment.TickCount64;
        var elapsed = Math.Clamp(now - _lastContactAt, 0, int.MaxValue);
        _lastContactAt = now;

        if (contacts.Count == 0)
        {
            _bottomLeftTapRecognizer.OnContacts(_previousContacts, [], bounds);
            _previousContacts = [];
            _releaseTimer.Stop();
            _releaseTimer.Start();
            return;
        }

        _releaseTimer.Stop();

        if (elapsed > ReleaseGapMs)
        {
            _previousContacts = [];
            _dragRecognizer.ResetGestureTracking();
            _bottomLeftTapRecognizer.Reset();
        }

        var currentContacts = contacts.ToArray();
        _bottomLeftTapRecognizer.OnContacts(_previousContacts, currentContacts, bounds);
        _dragRecognizer.OnContacts(_previousContacts, currentContacts, (int)elapsed);
        _previousContacts = currentContacts;
    }

    private string ActiveStatus()
    {
        return (_threeFingerDragEnabled, _bottomLeftTapAction != CornerTapAction.Off) switch
        {
            (true, true) => "Three-finger drag and bottom-left tap are active.",
            (true, false) => "Three-finger drag is active.",
            (false, true) => "Bottom-left tap is active.",
            _ => "Touchpad gestures are off."
        };
    }

    private static void Log(string message)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MagicInput");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "three-finger-drag.log"), $"{DateTime.Now:O} {message}{Environment.NewLine}");
        }
        catch
        {
            // Input handling must never depend on diagnostics.
        }
    }

    private readonly struct RawTouchpadReport
    {
        public RawTouchpadReport(IntPtr device, List<TouchpadContact> contacts, uint contactCount, TouchpadBounds bounds)
        {
            Device = device;
            Contacts = contacts;
            ContactCount = contactCount;
            Bounds = bounds;
        }

        public IntPtr Device { get; }
        public List<TouchpadContact> Contacts { get; }
        public uint ContactCount { get; }
        public TouchpadBounds Bounds { get; }
    }

    private sealed class TouchpadContactManager
    {
        private readonly Action<IntPtr, IReadOnlyList<TouchpadContact>, TouchpadBounds> _onContacts;
        private readonly List<TouchpadContact> _pendingContacts = new();
        private uint _targetContactCount;

        public TouchpadContactManager(Action<IntPtr, IReadOnlyList<TouchpadContact>, TouchpadBounds> onContacts)
        {
            _onContacts = onContacts;
        }

        public void Receive(IntPtr device, List<TouchpadContact> contacts, uint contactCount, TouchpadBounds bounds)
        {
            if (contactCount == 0 && contacts.Count == 0)
            {
                _pendingContacts.Clear();
                _targetContactCount = 0;
                _onContacts(device, [], bounds);
                return;
            }

            if (contacts.Count == 0)
            {
                return;
            }

            if (contactCount == contacts.Count)
            {
                _pendingContacts.Clear();
                _targetContactCount = 0;
                _onContacts(device, contacts, bounds);
                return;
            }

            if (contactCount == 0)
            {
                _pendingContacts.AddRange(contacts);
                RemoveDuplicateContacts(_pendingContacts);
                if (_targetContactCount != 0 && _pendingContacts.Count >= _targetContactCount)
                {
                    var completed = _pendingContacts.Take((int)_targetContactCount).ToArray();
                    _pendingContacts.Clear();
                    _targetContactCount = 0;
                    _onContacts(device, completed, bounds);
                }

                return;
            }

            _pendingContacts.Clear();
            _pendingContacts.AddRange(contacts);
            _targetContactCount = contactCount;
        }

        private static void RemoveDuplicateContacts(List<TouchpadContact> contacts)
        {
            for (var index = contacts.Count - 1; index >= 0; index--)
            {
                if (contacts.FindIndex(c => c.ContactId == contacts[index].ContactId) != index)
                {
                    contacts.RemoveAt(index);
                }
            }
        }
    }

    private sealed class ThreeFingerDragRecognizer
    {
        private const float StartThreshold = 140f;
        private const float CursorScale = 0.25f;
        private const float MaxSingleReportDistance = 1400f;

        private bool _isDragging;
        private float _pendingX;
        private float _pendingY;
        private float _pendingDistance;
        private float _carryX;
        private float _carryY;

        public bool Enabled { get; set; }

        public void OnContacts(TouchpadContact[] previousContacts, TouchpadContact[] contacts, int elapsedMs)
        {
            if (!Enabled)
            {
                Release();
                ResetGestureTracking();
                return;
            }

            if (contacts.Length < 2)
            {
                Release();
                ResetGestureTracking();
                return;
            }

            var common = FindCommonContacts(previousContacts, contacts);
            if (common.Count < 2)
            {
                ResetGestureTracking();
                return;
            }

            var delta = AverageDelta(common);
            var distance = MathF.Sqrt(delta.X * delta.X + delta.Y * delta.Y);
            if (distance <= 0 || distance > MaxSingleReportDistance)
            {
                return;
            }

            if (!_isDragging)
            {
                if (contacts.Length != 3 || common.Count != 3)
                {
                    ResetGestureTracking();
                    return;
                }

                _pendingX += delta.X;
                _pendingY += delta.Y;
                _pendingDistance += distance;
                if (_pendingDistance < StartThreshold)
                {
                    return;
                }

                _isDragging = MouseInput.LeftDown();
                if (_isDragging)
                {
                    Log("three-finger drag start");
                    MovePointer(_pendingX, _pendingY);
                }

                _pendingX = 0;
                _pendingY = 0;
                _pendingDistance = 0;
                return;
            }

            MovePointer(delta.X, delta.Y);
        }

        public void ResetGestureTracking()
        {
            _pendingX = 0;
            _pendingY = 0;
            _pendingDistance = 0;
        }

        public void Release()
        {
            if (!_isDragging)
            {
                return;
            }

            MouseInput.LeftUp();
            _isDragging = false;
            ResetGestureTracking();
            Log("three-finger drag release");
        }

        private void MovePointer(float rawX, float rawY)
        {
            var scaledX = rawX * CursorScale + _carryX;
            var scaledY = rawY * CursorScale + _carryY;
            var dx = (int)scaledX;
            var dy = (int)scaledY;
            _carryX = scaledX - dx;
            _carryY = scaledY - dy;

            if (dx != 0 || dy != 0)
            {
                MouseInput.Move(dx, dy);
            }
        }

        private static List<(TouchpadContact Previous, TouchpadContact Current)> FindCommonContacts(
            TouchpadContact[] previousContacts,
            TouchpadContact[] contacts)
        {
            var common = new List<(TouchpadContact Previous, TouchpadContact Current)>();
            foreach (var contact in contacts)
            {
                var previousIndex = Array.FindIndex(previousContacts, c => c.ContactId == contact.ContactId);
                if (previousIndex >= 0)
                {
                    common.Add((previousContacts[previousIndex], contact));
                }
            }

            return common;
        }

        private static (float X, float Y) AverageDelta(List<(TouchpadContact Previous, TouchpadContact Current)> contacts)
        {
            var x = 0f;
            var y = 0f;
            foreach (var contact in contacts)
            {
                x += contact.Current.X - contact.Previous.X;
                y += contact.Current.Y - contact.Previous.Y;
            }

            return (x / contacts.Count, y / contacts.Count);
        }
    }

    private sealed class BottomLeftTapRecognizer
    {
        private const float CornerWidthRatio = 0.24f;
        private const float CornerHeightRatio = 0.24f;
        private const float MaxMoveRatio = 0.045f;
        private const int MaxTapMs = 420;
        private const int CooldownMs = 300;

        private CornerTapAction _action = CornerTapAction.Off;
        private bool _tracking;
        private bool _blocked;
        private int _contactId;
        private int _startX;
        private int _startY;
        private int _lastX;
        private int _lastY;
        private long _startedAt;
        private long _lastTriggeredAt;

        public void Configure(CornerTapAction action)
        {
            _action = action;
            if (action == CornerTapAction.Off)
            {
                Reset();
            }
        }

        public void OnContacts(TouchpadContact[] previousContacts, TouchpadContact[] contacts, TouchpadBounds bounds)
        {
            if (_action == CornerTapAction.Off || !bounds.IsValid)
            {
                Reset();
                return;
            }

            if (contacts.Length == 0)
            {
                CompleteIfTap(bounds);
                Reset();
                return;
            }

            if (contacts.Length != 1)
            {
                if (_tracking)
                {
                    _blocked = true;
                }

                return;
            }

            var contact = contacts[0];
            if (!_tracking)
            {
                _tracking = true;
                _blocked = !bounds.IsBottomLeft(contact, CornerWidthRatio, CornerHeightRatio);
                _contactId = contact.ContactId;
                _startX = contact.X;
                _startY = contact.Y;
                _lastX = contact.X;
                _lastY = contact.Y;
                _startedAt = Environment.TickCount64;
                return;
            }

            if (contact.ContactId != _contactId)
            {
                _blocked = true;
                return;
            }

            _lastX = contact.X;
            _lastY = contact.Y;
            if (!bounds.IsBottomLeft(contact, CornerWidthRatio, CornerHeightRatio) || MovedTooFar(bounds))
            {
                _blocked = true;
            }
        }

        public void Reset()
        {
            _tracking = false;
            _blocked = false;
            _contactId = 0;
            _startX = 0;
            _startY = 0;
            _lastX = 0;
            _lastY = 0;
            _startedAt = 0;
        }

        private void CompleteIfTap(TouchpadBounds bounds)
        {
            if (!_tracking || _blocked)
            {
                return;
            }

            var now = Environment.TickCount64;
            if (now - _startedAt > MaxTapMs || now - _lastTriggeredAt < CooldownMs || MovedTooFar(bounds))
            {
                return;
            }

            if (KeyboardActionInput.Send(_action))
            {
                _lastTriggeredAt = now;
                Log($"bottom-left tap -> {DescribeAction(_action)}");
            }
        }

        private bool MovedTooFar(TouchpadBounds bounds)
        {
            var dx = _lastX - _startX;
            var dy = _lastY - _startY;
            var distance = MathF.Sqrt(dx * dx + dy * dy);
            var diagonal = MathF.Sqrt(bounds.Width * bounds.Width + bounds.Height * bounds.Height);
            return diagonal > 0 && distance > diagonal * MaxMoveRatio;
        }

        private static string DescribeAction(CornerTapAction action)
        {
            return action switch
            {
                CornerTapAction.ClipboardHistory => "Win+V",
                CornerTapAction.StartMenu => "Win",
                CornerTapAction.TaskView => "Win+Tab",
                CornerTapAction.ShowDesktop => "Win+D",
                CornerTapAction.PreviousWindow => "Alt+Tab",
                _ => "off"
            };
        }
    }

    private readonly struct TouchpadBounds
    {
        public TouchpadBounds(int minX, int maxX, int minY, int maxY)
        {
            MinX = minX;
            MaxX = maxX;
            MinY = minY;
            MaxY = maxY;
        }

        public int MinX { get; }
        public int MaxX { get; }
        public int MinY { get; }
        public int MaxY { get; }
        public bool IsValid => MaxX > MinX && MaxY > MinY;
        public int Width => MaxX - MinX;
        public int Height => MaxY - MinY;

        public bool IsBottomLeft(TouchpadContact contact, float widthRatio, float heightRatio)
        {
            if (!IsValid)
            {
                return false;
            }

            var leftLimit = MinX + Width * widthRatio;
            var bottomLimit = MaxY - Height * heightRatio;
            return contact.X <= leftLimit && contact.Y >= bottomLimit;
        }
    }

    private readonly struct TouchpadContact
    {
        public TouchpadContact(int contactId, int x, int y)
        {
            ContactId = contactId;
            X = x;
            Y = y;
        }

        public int ContactId { get; }
        public int X { get; }
        public int Y { get; }
    }

    private sealed class TouchpadContactBuilder
    {
        public int? ContactId { get; set; }
        public int? X { get; set; }
        public int? Y { get; set; }

        public bool TryCreate(out TouchpadContact contact)
        {
            if (ContactId.HasValue && X.HasValue && Y.HasValue)
            {
                contact = new TouchpadContact(ContactId.Value, X.Value, Y.Value);
                return true;
            }

            contact = default;
            return false;
        }

        public void Clear()
        {
            ContactId = null;
            X = null;
            Y = null;
        }
    }

    private static class PrecisionTouchpadGestureSettings
    {
        public static void ReserveThreeFingerGesturesForDrag()
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\PrecisionTouchPad",
                    true);
                key.SetValue("ThreeFingerSlideEnabled", 0, RegistryValueKind.DWord);
                key.SetValue("ThreeFingerTapEnabled", 0, RegistryValueKind.DWord);
                key.SetValue("ThreeFingerUp", 0, RegistryValueKind.DWord);
                key.SetValue("ThreeFingerDown", 0, RegistryValueKind.DWord);
                key.SetValue("ThreeFingerLeft", 0, RegistryValueKind.DWord);
                key.SetValue("ThreeFingerRight", 0, RegistryValueKind.DWord);
            }
            catch (Exception ex)
            {
                Log($"could not reserve Windows three-finger gestures: {ex.Message}");
            }
        }
    }

    private static class RawTouchpadInput
    {
        private const uint RidInput = 0x10000003;
        private const uint RidiPreparsedData = 0x20000005;
        private const uint RidiDeviceInfo = 0x2000000B;
        private const uint RimTypeHid = 2;
        private const uint RidevRemove = 0x00000001;
        private const uint RidevInputSink = 0x00000100;
        private const uint RidevDevNotify = 0x00002000;
        private const uint HidpStatusSuccess = 0x00110000;

        public static bool TouchpadExists()
        {
            uint deviceCount = 0;
            var listSize = (uint)Marshal.SizeOf<RawInputDeviceList>();
            if (GetRawInputDeviceList(null, ref deviceCount, listSize) != 0 || deviceCount == 0)
            {
                return false;
            }

            var devices = new RawInputDeviceList[deviceCount];
            if (GetRawInputDeviceList(devices, ref deviceCount, listSize) != deviceCount)
            {
                return false;
            }

            return devices.Any(device => device.Type == RimTypeHid && IsPrecisionTouchpad(device.Device));
        }

        public static bool RegisterInput(IntPtr targetWindow)
        {
            var device = new RawInputDevice
            {
                UsagePage = 0x000D,
                Usage = 0x0005,
                Flags = RidevInputSink | RidevDevNotify,
                Target = targetWindow
            };

            return RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<RawInputDevice>());
        }

        public static void UnregisterInput()
        {
            var device = new RawInputDevice
            {
                UsagePage = 0x000D,
                Usage = 0x0005,
                Flags = RidevRemove,
                Target = IntPtr.Zero
            };

            RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<RawInputDevice>());
        }

        public static RawTouchpadReport? ParseInput(IntPtr lParam)
        {
            uint rawInputSize = 0;
            var headerSize = (uint)Marshal.SizeOf<RawInputHeader>();
            if (GetRawInputData(lParam, RidInput, IntPtr.Zero, ref rawInputSize, headerSize) != 0 || rawInputSize == 0)
            {
                return null;
            }

            var rawInputPointer = IntPtr.Zero;
            try
            {
                rawInputPointer = Marshal.AllocHGlobal((int)rawInputSize);
                if (GetRawInputData(lParam, RidInput, rawInputPointer, ref rawInputSize, headerSize) != rawInputSize)
                {
                    return null;
                }

                var rawInput = Marshal.PtrToStructure<RawInput>(rawInputPointer);
                if (rawInput.Header.Type != RimTypeHid)
                {
                    return null;
                }

                var rawInputData = new byte[rawInputSize];
                Marshal.Copy(rawInputPointer, rawInputData, 0, rawInputData.Length);

                var rawHidData = new byte[rawInput.Hid.SizeHid * rawInput.Hid.Count];
                Buffer.BlockCopy(rawInputData, rawInputData.Length - rawHidData.Length, rawHidData, 0, rawHidData.Length);
                return ParseHid(rawInput.Header.Device, rawInput.Hid.SizeHid, rawInput.Hid.Count, rawHidData);
            }
            finally
            {
                Marshal.FreeHGlobal(rawInputPointer);
            }
        }

        private static RawTouchpadReport? ParseHid(IntPtr device, uint reportSize, uint reportCount, byte[] rawHidData)
        {
            var rawHidDataPointer = IntPtr.Zero;
            var preparsedDataPointer = IntPtr.Zero;

            try
            {
                rawHidDataPointer = Marshal.AllocHGlobal(rawHidData.Length);
                Marshal.Copy(rawHidData, 0, rawHidDataPointer, rawHidData.Length);

                uint preparsedDataSize = 0;
                if (GetRawInputDeviceInfo(device, RidiPreparsedData, IntPtr.Zero, ref preparsedDataSize) != 0 || preparsedDataSize == 0)
                {
                    return null;
                }

                preparsedDataPointer = Marshal.AllocHGlobal((int)preparsedDataSize);
                if (GetRawInputDeviceInfo(device, RidiPreparsedData, preparsedDataPointer, ref preparsedDataSize) != preparsedDataSize)
                {
                    return null;
                }

                if (HidP_GetCaps(preparsedDataPointer, out var caps) != HidpStatusSuccess)
                {
                    return null;
                }

                var valueCapsLength = caps.NumberInputValueCaps;
                var valueCaps = new HidpValueCaps[valueCapsLength];
                if (HidP_GetValueCaps(HidpReportType.Input, valueCaps, ref valueCapsLength, preparsedDataPointer) != HidpStatusSuccess)
                {
                    return null;
                }

                uint contactCount = 99;
                int? minX = null;
                int? maxX = null;
                int? minY = null;
                int? maxY = null;
                var builders = new List<TouchpadContactBuilder>();
                var contacts = new List<TouchpadContact>();

                foreach (var valueCap in valueCaps.Take(valueCapsLength).OrderBy(cap => cap.LinkCollection))
                {
                    if (valueCap.UsagePage == 0x01 && valueCap.Usage == 0x30)
                    {
                        minX = valueCap.LogicalMin;
                        maxX = valueCap.LogicalMax;
                    }
                    else if (valueCap.UsagePage == 0x01 && valueCap.Usage == 0x31)
                    {
                        minY = valueCap.LogicalMin;
                        maxY = valueCap.LogicalMax;
                    }

                    for (var contactIndex = 0; contactIndex < reportCount; contactIndex++)
                    {
                        var reportPointer = IntPtr.Add(rawHidDataPointer, (int)(reportSize * contactIndex));
                        if (HidP_GetUsageValue(
                                HidpReportType.Input,
                                valueCap.UsagePage,
                                valueCap.LinkCollection,
                                valueCap.Usage,
                                out var value,
                                preparsedDataPointer,
                                reportPointer,
                                reportSize) != HidpStatusSuccess)
                        {
                            continue;
                        }

                        if (valueCap.LinkCollection == 0)
                        {
                            if (valueCap.UsagePage == 0x0D && valueCap.Usage == 0x54)
                            {
                                contactCount = value;
                            }

                            continue;
                        }

                        while (builders.Count <= contactIndex)
                        {
                            builders.Add(new TouchpadContactBuilder());
                        }

                        var builder = builders[contactIndex];
                        switch (valueCap.UsagePage, valueCap.Usage)
                        {
                            case (0x0D, 0x51):
                                builder.ContactId = (int)value;
                                break;
                            case (0x01, 0x30):
                                builder.X = (int)value;
                                break;
                            case (0x01, 0x31):
                                builder.Y = (int)value;
                                break;
                        }
                    }

                    foreach (var builder in builders)
                    {
                        if ((contactCount == 0 || contacts.Count < contactCount) && builder.TryCreate(out var contact))
                        {
                            contacts.Add(contact);
                            builder.Clear();
                        }
                    }

                    if (contactCount != 0 && contacts.Count >= contactCount)
                    {
                        break;
                    }
                }

                var bounds = minX.HasValue && maxX.HasValue && minY.HasValue && maxY.HasValue
                    ? new TouchpadBounds(minX.Value, maxX.Value, minY.Value, maxY.Value)
                    : default;
                return new RawTouchpadReport(device, contacts, contactCount, bounds);
            }
            finally
            {
                Marshal.FreeHGlobal(rawHidDataPointer);
                Marshal.FreeHGlobal(preparsedDataPointer);
            }
        }

        private static bool IsPrecisionTouchpad(IntPtr device)
        {
            uint deviceInfoSize = 0;
            if (GetRawInputDeviceInfo(device, RidiDeviceInfo, IntPtr.Zero, ref deviceInfoSize) != 0 || deviceInfoSize == 0)
            {
                return false;
            }

            var deviceInfo = new RidDeviceInfo { Size = deviceInfoSize };
            return GetRawInputDeviceInfo(device, RidiDeviceInfo, ref deviceInfo, ref deviceInfoSize) != unchecked((uint)-1)
                && deviceInfo.Hid.UsagePage == 0x000D
                && deviceInfo.Hid.Usage == 0x0005;
        }

        [DllImport("User32.dll", SetLastError = true)]
        private static extern uint GetRawInputDeviceList(
            [Out] RawInputDeviceList[]? deviceList,
            ref uint deviceCount,
            uint size);

        [DllImport("User32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RegisterRawInputDevices(
            RawInputDevice[] devices,
            uint deviceCount,
            uint size);

        [DllImport("User32.dll", SetLastError = true)]
        private static extern uint GetRawInputData(
            IntPtr rawInput,
            uint command,
            IntPtr data,
            ref uint size,
            uint headerSize);

        [DllImport("User32.dll", SetLastError = true)]
        private static extern uint GetRawInputDeviceInfo(
            IntPtr device,
            uint command,
            IntPtr data,
            ref uint size);

        [DllImport("User32.dll", SetLastError = true)]
        private static extern uint GetRawInputDeviceInfo(
            IntPtr device,
            uint command,
            ref RidDeviceInfo data,
            ref uint size);

        [DllImport("Hid.dll", SetLastError = true)]
        private static extern uint HidP_GetCaps(IntPtr preparsedData, out HidpCaps capabilities);

        [DllImport("Hid.dll", CharSet = CharSet.Auto)]
        private static extern uint HidP_GetValueCaps(
            HidpReportType reportType,
            [Out] HidpValueCaps[] valueCaps,
            ref ushort valueCapsLength,
            IntPtr preparsedData);

        [DllImport("Hid.dll", CharSet = CharSet.Auto)]
        private static extern uint HidP_GetUsageValue(
            HidpReportType reportType,
            ushort usagePage,
            ushort linkCollection,
            ushort usage,
            out uint usageValue,
            IntPtr preparsedData,
            IntPtr report,
            uint reportLength);

        [StructLayout(LayoutKind.Sequential)]
        private struct RawInputDeviceList
        {
            public IntPtr Device;
            public uint Type;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RawInputDevice
        {
            public ushort UsagePage;
            public ushort Usage;
            public uint Flags;
            public IntPtr Target;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RawInput
        {
            public RawInputHeader Header;
            public RawHid Hid;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RawInputHeader
        {
            public uint Type;
            public uint Size;
            public IntPtr Device;
            public IntPtr WParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RawHid
        {
            public uint SizeHid;
            public uint Count;
            public IntPtr RawData;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RidDeviceInfo
        {
            public uint Size;
            public uint Type;
            public RidDeviceInfoHid Hid;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RidDeviceInfoHid
        {
            public uint VendorId;
            public uint ProductId;
            public uint VersionNumber;
            public ushort UsagePage;
            public ushort Usage;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HidpCaps
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
            public ushort[] Reserved;

            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps;
            public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps;
            public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps;
            public ushort NumberFeatureDataIndices;
        }

        private enum HidpReportType
        {
            Input,
            Output,
            Feature
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HidpValueCaps
        {
            public ushort UsagePage;
            public byte ReportId;

            [MarshalAs(UnmanagedType.U1)]
            public bool IsAlias;

            public ushort BitField;
            public ushort LinkCollection;
            public ushort LinkUsage;
            public ushort LinkUsagePage;

            [MarshalAs(UnmanagedType.U1)]
            public bool IsRange;

            [MarshalAs(UnmanagedType.U1)]
            public bool IsStringRange;

            [MarshalAs(UnmanagedType.U1)]
            public bool IsDesignatorRange;

            [MarshalAs(UnmanagedType.U1)]
            public bool IsAbsolute;

            [MarshalAs(UnmanagedType.U1)]
            public bool HasNull;

            public byte Reserved;
            public ushort BitSize;
            public ushort ReportCount;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 5)]
            public ushort[] Reserved2;

            public uint UnitsExp;
            public uint Units;
            public int LogicalMin;
            public int LogicalMax;
            public int PhysicalMin;
            public int PhysicalMax;
            public ushort UsageMin;
            public ushort UsageMax;
            public ushort StringMin;
            public ushort StringMax;
            public ushort DesignatorMin;
            public ushort DesignatorMax;
            public ushort DataIndexMin;
            public ushort DataIndexMax;

            public ushort Usage => UsageMin;
        }
    }

    private static class KeyboardActionInput
    {
        private const int InputKeyboard = 1;
        private const uint KeyEventFExtendedKey = 0x0001;
        private const uint KeyEventFKeyUp = 0x0002;

        public static bool Send(CornerTapAction action)
        {
            return action switch
            {
                CornerTapAction.ClipboardHistory => SendChord([(ushort)Keys.LWin], (ushort)Keys.V),
                CornerTapAction.StartMenu => SendKeyPress((ushort)Keys.LWin),
                CornerTapAction.TaskView => SendChord([(ushort)Keys.LWin], (ushort)Keys.Tab),
                CornerTapAction.ShowDesktop => SendChord([(ushort)Keys.LWin], (ushort)Keys.D),
                CornerTapAction.PreviousWindow => SendChord([(ushort)Keys.Menu], (ushort)Keys.Tab),
                _ => false
            };
        }

        private static bool SendKeyPress(ushort virtualKey)
        {
            return SendKey(virtualKey, false) && SendKey(virtualKey, true);
        }

        private static bool SendChord(IReadOnlyList<ushort> modifierKeys, ushort key)
        {
            var pressedModifiers = new List<ushort>();
            foreach (var modifierKey in modifierKeys)
            {
                if (!SendKey(modifierKey, false))
                {
                    ReleaseKeys(pressedModifiers);
                    return false;
                }

                pressedModifiers.Add(modifierKey);
            }

            if (!SendKey(key, false) || !SendKey(key, true))
            {
                ReleaseKeys(pressedModifiers);
                return false;
            }

            ReleaseKeys(pressedModifiers);
            return true;
        }

        private static void ReleaseKeys(IEnumerable<ushort> keys)
        {
            foreach (var key in keys.Reverse())
            {
                SendKey(key, true);
            }
        }

        private static bool SendKey(ushort virtualKey, bool keyUp)
        {
            var input = new Input
            {
                Type = InputKeyboard,
                Keyboard = new KeyboardInputData
                {
                    VirtualKey = virtualKey,
                    Flags = (IsExtendedKey(virtualKey) ? KeyEventFExtendedKey : 0) | (keyUp ? KeyEventFKeyUp : 0)
                }
            };

            var ok = SendInput(1, [input], Marshal.SizeOf<Input>()) == 1;
            if (!ok)
            {
                Log($"SendInput keyboard failed. Win32 error {Marshal.GetLastWin32Error()}.");
            }

            return ok;
        }

        private static bool IsExtendedKey(ushort virtualKey)
        {
            return virtualKey is 0x5B or 0x5C;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint inputs, Input[] input, int size);

        [StructLayout(LayoutKind.Sequential)]
        private struct Input
        {
            public int Type;
            public KeyboardInputData Keyboard;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KeyboardInputData
        {
            public ushort VirtualKey;
            public ushort ScanCode;
            public uint Flags;
            public int Time;
            public IntPtr ExtraInfo;
        }
    }

    private static class MouseInput
    {
        private const int InputMouse = 0;
        private const int MouseEventMove = 0x0001;
        private const int MouseEventLeftDown = 0x0002;
        private const int MouseEventLeftUp = 0x0004;

        public static bool LeftDown() => SendMouse(MouseEventLeftDown, 0, 0);

        public static bool LeftUp() => SendMouse(MouseEventLeftUp, 0, 0);

        public static bool Move(int dx, int dy) => SendMouse(MouseEventMove, dx, dy);

        private static bool SendMouse(int flags, int dx, int dy)
        {
            var input = new Input
            {
                Type = InputMouse,
                Mouse = new MouseInputData
                {
                    Dx = dx,
                    Dy = dy,
                    Flags = flags
                }
            };

            var ok = SendInput(1, [input], Marshal.SizeOf<Input>()) == 1;
            if (!ok)
            {
                Log($"SendInput mouse failed. Win32 error {Marshal.GetLastWin32Error()}.");
            }

            return ok;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint inputs, Input[] input, int size);

        [StructLayout(LayoutKind.Sequential)]
        private struct Input
        {
            public int Type;
            public MouseInputData Mouse;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MouseInputData
        {
            public int Dx;
            public int Dy;
            public int MouseData;
            public int Flags;
            public int Time;
            public IntPtr ExtraInfo;
        }
    }
}
