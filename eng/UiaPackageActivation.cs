using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

// Windows supplies the installed application's activation and file routing.
public static class ElliePdfPackageActivation
{
    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string id,
            [MarshalAs(UnmanagedType.LPWStr)] string arguments, uint options, out uint processId);
        [PreserveSig] int ActivateForFile([MarshalAs(UnmanagedType.LPWStr)] string id,
            IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string verb, out uint processId);
        [PreserveSig] int ActivateForProtocol([MarshalAs(UnmanagedType.LPWStr)] string id,
            IntPtr items, out uint processId);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr context, ref Guid iid, out IntPtr item);
    [DllImport("shell32.dll", PreserveSig = false)]
    private static extern void SHCreateShellItemArrayFromShellItem(IntPtr item, ref Guid iid, out IntPtr array);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetApplicationUserModelId(IntPtr process, ref uint length, StringBuilder id);

    public static string GetProcessApplicationId(Process process)
    {
        uint length = 0;
        int result = GetApplicationUserModelId(process.Handle, ref length, null);
        if (result != 122) throw new InvalidOperationException("Cannot establish process application identity: " + result);
        var id = new StringBuilder((int)length);
        result = GetApplicationUserModelId(process.Handle, ref length, id);
        if (result != 0) throw new InvalidOperationException("Cannot read process application identity: " + result);
        return id.ToString();
    }

    public static int Activate(string applicationId, string filePath)
    {
        var manager = (IApplicationActivationManager)Activator.CreateInstance(Type.GetTypeFromCLSID(
            new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")));
        IntPtr item = IntPtr.Zero, array = IntPtr.Zero;
        try
        {
            uint processId;
            int result;
            if (filePath == null)
                result = manager.ActivateApplication(applicationId, null, 0, out processId);
            else
            {
                var itemId = new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe");
                var arrayId = new Guid("b63ea76d-1f85-456f-a19c-48159efa858b");
                SHCreateItemFromParsingName(filePath, IntPtr.Zero, ref itemId, out item);
                SHCreateShellItemArrayFromShellItem(item, ref arrayId, out array);
                result = manager.ActivateForFile(applicationId, array, "open", out processId);
            }
            Marshal.ThrowExceptionForHR(result);
            if (processId == 0) throw new InvalidOperationException("Activation returned no process identity.");
            return checked((int)processId);
        }
        finally
        {
            if (array != IntPtr.Zero) Marshal.Release(array);
            if (item != IntPtr.Zero) Marshal.Release(item);
            Marshal.FinalReleaseComObject(manager);
        }
    }
}
