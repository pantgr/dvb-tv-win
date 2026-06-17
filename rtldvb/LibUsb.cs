using System.Runtime.InteropServices;

namespace RtlDvb;

/// <summary>
/// Minimal P/Invoke wrapper around libusb-1.0.dll (same native lib librtlsdr uses underneath).
/// Talks to the RTL2832U via its WinUSB driver (Zadig "Bulk-In, Interface" on MI_00).
/// Only the calls needed to drive the chip's hardware DVB-T demod: control transfers
/// (register/I2C programming) + bulk reads (MPEG-TS from EP 0x81).
/// </summary>
internal static class LibUsb
{
    private const string DLL = "libusb-1.0";
    private const CallingConvention CC = CallingConvention.Cdecl;

    // libusb control transfer request types (bmRequestType)
    public const byte VENDOR_OUT = 0x40; // LIBUSB_REQUEST_TYPE_VENDOR | LIBUSB_ENDPOINT_OUT
    public const byte VENDOR_IN  = 0xC0; // LIBUSB_REQUEST_TYPE_VENDOR | LIBUSB_ENDPOINT_IN

    [DllImport(DLL, CallingConvention = CC)] public static extern int libusb_init(out IntPtr ctx);
    [DllImport(DLL, CallingConvention = CC)] public static extern void libusb_exit(IntPtr ctx);

    [DllImport(DLL, CallingConvention = CC)]
    public static extern IntPtr libusb_open_device_with_vid_pid(IntPtr ctx, ushort vendor_id, ushort product_id);

    [DllImport(DLL, CallingConvention = CC)] public static extern void libusb_close(IntPtr dev_handle);

    [DllImport(DLL, CallingConvention = CC)]
    public static extern int libusb_claim_interface(IntPtr dev_handle, int interface_number);
    [DllImport(DLL, CallingConvention = CC)]
    public static extern int libusb_release_interface(IntPtr dev_handle, int interface_number);

    [DllImport(DLL, CallingConvention = CC)]
    public static extern int libusb_set_interface_alt_setting(IntPtr dev_handle, int interface_number, int alternate_setting);

    [DllImport(DLL, CallingConvention = CC)]
    public static extern int libusb_reset_device(IntPtr dev_handle);

    [DllImport(DLL, CallingConvention = CC)]
    public static extern int libusb_kernel_driver_active(IntPtr dev_handle, int interface_number);
    [DllImport(DLL, CallingConvention = CC)]
    public static extern int libusb_detach_kernel_driver(IntPtr dev_handle, int interface_number);

    [DllImport(DLL, CallingConvention = CC)]
    public static extern int libusb_control_transfer(
        IntPtr dev_handle, byte bmRequestType, byte bRequest,
        ushort wValue, ushort wIndex,
        [In, Out] byte[] data, ushort wLength, uint timeout);

    [DllImport(DLL, CallingConvention = CC)]
    public static extern int libusb_bulk_transfer(
        IntPtr dev_handle, byte endpoint,
        [In, Out] byte[] data, int length, out int actual_length, uint timeout);

    [DllImport(DLL, CallingConvention = CC)] public static extern IntPtr libusb_error_name(int errcode);

    public static string ErrName(int code)
    {
        var p = libusb_error_name(code);
        return p == IntPtr.Zero ? code.ToString() : (Marshal.PtrToStringAnsi(p) ?? code.ToString());
    }
}
