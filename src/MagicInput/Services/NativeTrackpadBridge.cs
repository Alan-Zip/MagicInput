using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MagicInput.Services;

public static class NativeTrackpadBridge
{
    private const uint FileDeviceUnknown = 0x00000022;
    private const uint MethodBuffered = 0;
    private const uint FileAnyAccess = 0;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint OpenExisting = 3;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;

    private static readonly uint IoctlReloadSettings = CtlCode(FileDeviceUnknown, 0x800, MethodBuffered, FileAnyAccess);
    private static readonly uint IoctlGetBattery = CtlCode(FileDeviceUnknown, 0x801, MethodBuffered, FileAnyAccess);

    public static bool ReloadSettings() => ExecuteIoctl(IoctlReloadSettings, out _, false, out _);

    public static bool TryGetBattery(out uint level, out string error)
    {
        var ok = ExecuteIoctl(IoctlGetBattery, out level, true, out error);
        if (level > 100)
        {
            error = $"Driver returned an invalid battery level: {level}";
            return false;
        }

        return ok;
    }

    private static uint CtlCode(uint deviceType, uint function, uint method, uint access)
    {
        return (deviceType << 16) | (access << 14) | (function << 2) | method;
    }

    private static bool ExecuteIoctl(uint code, out uint result, bool expectData, out string error)
    {
        result = 0;
        error = "";
        IntPtr pOutBuffer = IntPtr.Zero;

        using var handle = CreateFile(
            @"\\.\AmtPtpControlDeviceUm",
            GenericRead | GenericWrite,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            error = $"Failed to open trackpad control device. Win32 error {Marshal.GetLastWin32Error()}.";
            return false;
        }

        try
        {
            uint outBufferSize = 0;
            if (expectData)
            {
                outBufferSize = sizeof(uint);
                pOutBuffer = Marshal.AllocHGlobal((int)outBufferSize);
            }

            var success = DeviceIoControl(handle, code, IntPtr.Zero, 0, pOutBuffer, outBufferSize, out _, IntPtr.Zero);
            if (!success)
            {
                error = $"DeviceIoControl failed. Win32 error {Marshal.GetLastWin32Error()}.";
                return false;
            }

            if (expectData)
            {
                result = (uint)Marshal.ReadInt32(pOutBuffer);
            }

            return true;
        }
        finally
        {
            if (pOutBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(pOutBuffer);
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);
}
