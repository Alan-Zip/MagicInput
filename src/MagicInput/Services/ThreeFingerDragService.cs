using System.Runtime.InteropServices;
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
    private readonly ThreeFingerDragRecognizer _recognizer = new();
    private readonly System.Windows.Forms.Timer _releaseTimer = new();

    private TouchpadContact[] _previousContacts = [];
    private long _lastContactAt = Environment.TickCount64;
    private bool _enabled;
    private bool _registered;

    public ThreeFingerDragService()
    {
        _contactManager = new TouchpadContactManager(OnTouchpadContacts);
        _releaseTimer.Interval = ReleaseAfterNoContactsMs;
        _releaseTimer.Tick += (_, _) =>
        {
            _releaseTimer.Stop();
            _recognizer.Release();
        };
    }

    public string Status { get; private set; } = "Three-finger drag is off.";

    public bool IsActive => _enabled && _registered;

    public void Configure(bool enabled)
    {
        if (enabled)
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
                _contactManager.Receive(report.Value.Device, report.Value.Contacts, report.Value.ContactCount);
            }
        }
        else if (_enabled && m.Msg == WmInputDeviceChange)
        {
            Status = RawTouchpadInput.TouchpadExists()
                ? "Three-finger drag is active."
                : "No Precision Touchpad raw input device found.";
        }

        base.WndProc(ref m);
    }

    private void Start()
    {
        _enabled = true;
        EnsureHandle();
        PrecisionTouchpadGestureSettings.ReserveThreeFingerGesturesForDrag();

        var touchpadExists = RawTouchpadInput.TouchpadExists();
        _registered = RawTouchpadInput.RegisterInput(Handle);
        Status = touchpadExists
            ? _registered
                ? "Three-finger drag is active."
                : $"Three-finger drag could not register raw input. Win32 error {Marshal.GetLastWin32Error()}."
            : "No Precision Touchpad raw input device found.";
        Log(Status);
    }

    private void Stop()
    {
        _enabled = false;
        _registered = false;
        _releaseTimer.Stop();
        _recognizer.Release();
        _previousContacts = [];
        RawTouchpadInput.UnregisterInput();
        Status = "Three-finger drag is off.";
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

    private void OnTouchpadContacts(IntPtr device, IReadOnlyList<TouchpadContact> contacts)
    {
        var now = Environment.TickCount64;
        var elapsed = Math.Clamp(now - _lastContactAt, 0, int.MaxValue);
        _lastContactAt = now;

        if (contacts.Count == 0)
        {
            _previousContacts = [];
            _releaseTimer.Stop();
            _releaseTimer.Start();
            return;
        }

        _releaseTimer.Stop();

        if (elapsed > ReleaseGapMs)
        {
            _previousContacts = [];
            _recognizer.ResetGestureTracking();
        }

        _recognizer.OnContacts(_previousContacts, contacts.ToArray(), (int)elapsed);
        _previousContacts = contacts.ToArray();
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
        public RawTouchpadReport(IntPtr device, List<TouchpadContact> contacts, uint contactCount)
        {
            Device = device;
            Contacts = contacts;
            ContactCount = contactCount;
        }

        public IntPtr Device { get; }
        public List<TouchpadContact> Contacts { get; }
        public uint ContactCount { get; }
    }

    private sealed class TouchpadContactManager
    {
        private readonly Action<IntPtr, IReadOnlyList<TouchpadContact>> _onContacts;
        private readonly List<TouchpadContact> _pendingContacts = new();
        private uint _targetContactCount;

        public TouchpadContactManager(Action<IntPtr, IReadOnlyList<TouchpadContact>> onContacts)
        {
            _onContacts = onContacts;
        }

        public void Receive(IntPtr device, List<TouchpadContact> contacts, uint contactCount)
        {
            if (contactCount == 0 && contacts.Count == 0)
            {
                _pendingContacts.Clear();
                _targetContactCount = 0;
                _onContacts(device, []);
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
                _onContacts(device, contacts);
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
                    _onContacts(device, completed);
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

        public void OnContacts(TouchpadContact[] previousContacts, TouchpadContact[] contacts, int elapsedMs)
        {
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
                var builders = new List<TouchpadContactBuilder>();
                var contacts = new List<TouchpadContact>();

                foreach (var valueCap in valueCaps.Take(valueCapsLength).OrderBy(cap => cap.LinkCollection))
                {
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

                return new RawTouchpadReport(device, contacts, contactCount);
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
