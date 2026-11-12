using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DiskTree.Infrastructure;

/// <summary>
/// Shows the real Windows Explorer context menu (incl. "Öffnen mit", "Senden an", third-party entries)
/// for a file or folder, and can invoke single shell verbs such as "delete".
/// </summary>
public static class ShellContextMenu
{
    private const uint CMF_NORMAL = 0x0, CMF_EXPLORE = 0x4, CMF_CANRENAME = 0x10;
    private const uint TPM_RETURNCMD = 0x100, TPM_RIGHTBUTTON = 0x2;
    private const int WM_INITMENUPOPUP = 0x117, WM_DRAWITEM = 0x2B, WM_MEASUREITEM = 0x2C, WM_MENUCHAR = 0x120;
    private const int CMIC_MASK_PTINVOKE = 0x20000000;
    private const int SW_SHOWNORMAL = 1;
    private const uint FirstCmd = 1, LastCmd = 0x7FFF;

    private static Guid IID_IShellFolder = new("000214E6-0000-0000-C000-000000000046");
    private static Guid IID_IContextMenu = new("000214E4-0000-0000-C000-000000000046");

    private const uint MF_STRING = 0x0, MF_BYPOSITION = 0x400, MF_SEPARATOR = 0x800;
    // Our own entries use ids above the range the shell handler may use (1..0x7FFF).
    private const uint ExtraBase = 0x8000;

    /// <summary>
    /// Shows the menu at the given screen position (device pixels). Blocks until it is closed.
    /// <paramref name="extras"/> are added at the top, above the shell's own entries.
    /// </summary>
    public static void Show(Window owner, string path, Point screenPos, IReadOnlyList<(string Text, Action Run)>? extras = null)
    {
        var hwnd = new WindowInteropHelper(owner).Handle;
        using var session = Open(hwnd, path);
        if (session == null) return;

        var hMenu = CreatePopupMenu();
        var source = HwndSource.FromHwnd(hwnd);
        IntPtr Hook(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) => session.HandleMessage(msg, w, l, ref handled);

        try
        {
            session.Menu.QueryContextMenu(hMenu, 0, FirstCmd, LastCmd, CMF_NORMAL | CMF_EXPLORE | CMF_CANRENAME);

            if (extras is { Count: > 0 })
            {
                // Inserted back to front at position 0 so they end up in the given order, then a separator.
                for (int i = extras.Count - 1; i >= 0; i--)
                    InsertMenu(hMenu, 0, MF_BYPOSITION | MF_STRING, (UIntPtr)(ExtraBase + 1 + (uint)i), extras[i].Text);
                InsertMenu(hMenu, (uint)extras.Count, MF_BYPOSITION | MF_SEPARATOR, UIntPtr.Zero, null);
            }

            source?.AddHook(Hook);

            uint cmd = TrackPopupMenuEx(hMenu, TPM_RETURNCMD | TPM_RIGHTBUTTON,
                (int)screenPos.X, (int)screenPos.Y, hwnd, IntPtr.Zero);

            if (cmd > ExtraBase && extras != null)
                extras[(int)(cmd - ExtraBase - 1)].Run();
            else if (cmd >= FirstCmd && cmd <= LastCmd)
                session.Invoke((IntPtr)(cmd - FirstCmd), hwnd, screenPos);
        }
        finally
        {
            source?.RemoveHook(Hook);
            DestroyMenu(hMenu);
        }
    }

    /// <summary>Runs one shell verb (e.g. "open", "delete", "properties") without showing the menu.</summary>
    public static void InvokeVerb(Window owner, string path, string verb)
    {
        var hwnd = new WindowInteropHelper(owner).Handle;
        using var session = Open(hwnd, path);
        if (session == null) return;

        var hMenu = CreatePopupMenu();
        var verbPtr = Marshal.StringToHGlobalAnsi(verb);
        try
        {
            // The handler has to be initialised through QueryContextMenu before it accepts verbs.
            session.Menu.QueryContextMenu(hMenu, 0, FirstCmd, LastCmd, CMF_NORMAL | CMF_CANRENAME);
            int hr = session.Invoke(verbPtr, hwnd, default);
            // 0x800704C7 = the user cancelled (e.g. the delete confirmation), not an error.
            if (hr < 0 && hr != unchecked((int)0x800704C7))
                Marshal.ThrowExceptionForHR(hr);
        }
        finally
        {
            Marshal.FreeHGlobal(verbPtr);
            DestroyMenu(hMenu);
        }
    }

    private static Session? Open(IntPtr hwnd, string path)
    {
        if (SHParseDisplayName(path, IntPtr.Zero, out var pidl, 0, out _) != 0 || pidl == IntPtr.Zero)
            return null;

        try
        {
            if (SHBindToParent(pidl, ref IID_IShellFolder, out var folderPtr, out var child) != 0)
                return null;

            var folder = (IShellFolder)Marshal.GetObjectForIUnknown(folderPtr);
            Marshal.Release(folderPtr);

            if (folder.GetUIObjectOf(hwnd, 1, new[] { child }, ref IID_IContextMenu, IntPtr.Zero, out var ctxPtr) != 0
                || ctxPtr == IntPtr.Zero)
            {
                Marshal.ReleaseComObject(folder);
                return null;
            }

            var menu = (IContextMenu)Marshal.GetObjectForIUnknown(ctxPtr);
            Marshal.Release(ctxPtr);
            return new Session(folder, menu);
        }
        finally
        {
            // 'child' points into pidl, so the whole thing is freed once, here.
            Marshal.FreeCoTaskMem(pidl);
        }
    }

    private sealed class Session : IDisposable
    {
        private readonly IShellFolder _folder;
        public IContextMenu Menu { get; }
        private readonly IContextMenu2? _menu2;
        private readonly IContextMenu3? _menu3;

        public Session(IShellFolder folder, IContextMenu menu)
        {
            _folder = folder;
            Menu = menu;
            _menu2 = menu as IContextMenu2;
            _menu3 = menu as IContextMenu3;
        }

        // Submenus ("Öffnen mit", "Senden an") are filled and drawn by the handler through these messages.
        public IntPtr HandleMessage(int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg is not (WM_INITMENUPOPUP or WM_DRAWITEM or WM_MEASUREITEM or WM_MENUCHAR))
                return IntPtr.Zero;

            if (_menu3 != null)
            {
                if (_menu3.HandleMenuMsg2((uint)msg, wParam, lParam, out var result) == 0)
                {
                    handled = true;
                    return result;
                }
            }
            else if (_menu2 != null && msg != WM_MENUCHAR)
            {
                if (_menu2.HandleMenuMsg((uint)msg, wParam, lParam) == 0)
                {
                    handled = true;
                    return IntPtr.Zero;
                }
            }
            return IntPtr.Zero;
        }

        public int Invoke(IntPtr verb, IntPtr hwnd, Point pt)
        {
            var info = new CMINVOKECOMMANDINFOEX
            {
                cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                fMask = CMIC_MASK_PTINVOKE,
                hwnd = hwnd,
                lpVerb = verb,
                nShow = SW_SHOWNORMAL,
                ptInvoke = new POINT { x = (int)pt.X, y = (int)pt.Y },
            };
            return Menu.InvokeCommand(ref info);
        }

        public void Dispose()
        {
            Marshal.ReleaseComObject(Menu);
            Marshal.ReleaseComObject(_folder);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CMINVOKECOMMANDINFOEX
    {
        public int cbSize;
        public int fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        public IntPtr lpParameters;
        public IntPtr lpDirectory;
        public int nShow;
        public int dwHotKey;
        public IntPtr hIcon;
        public IntPtr lpTitle;
        public IntPtr lpVerbW;
        public IntPtr lpParametersW;
        public IntPtr lpDirectoryW;
        public IntPtr lpTitleW;
        public POINT ptInvoke;
    }

    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string name, ref uint eaten, out IntPtr pidl, ref uint attrs);
        [PreserveSig] int EnumObjects(IntPtr hwnd, int flags, out IntPtr enumIdList);
        [PreserveSig] int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        [PreserveSig] int CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetAttributesOf(uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref uint attrs);
        [PreserveSig] int GetUIObjectOf(IntPtr hwndOwner, uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref Guid riid, IntPtr reserved, out IntPtr ppv);
        [PreserveSig] int GetDisplayNameOf(IntPtr pidl, uint flags, IntPtr strret);
        [PreserveSig] int SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string name, uint flags, out IntPtr pidlOut);
    }

    [ComImport, Guid("000214E4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint flags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint type, IntPtr reserved, IntPtr name, uint cch);
    }

    [ComImport, Guid("000214F4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu2
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint flags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint type, IntPtr reserved, IntPtr name, uint cch);
        [PreserveSig] int HandleMenuMsg(uint msg, IntPtr wParam, IntPtr lParam);
    }

    [ComImport, Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu3
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint flags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint type, IntPtr reserved, IntPtr name, uint cch);
        [PreserveSig] int HandleMenuMsg(uint msg, IntPtr wParam, IntPtr lParam);
        [PreserveSig] int HandleMenuMsg2(uint msg, IntPtr wParam, IntPtr lParam, out IntPtr result);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindCtx, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);

    [DllImport("shell32.dll")]
    private static extern int SHBindToParent(IntPtr pidl, ref Guid riid, out IntPtr ppv, out IntPtr pidlLast);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool InsertMenu(IntPtr hMenu, uint position, uint flags, UIntPtr idNewItem, string? newItem);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(IntPtr hMenu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
}
