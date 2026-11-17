using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

static class TaskbarStyle
{
    public static string IconPath, Target;
    [StructLayout(LayoutKind.Sequential)] struct Key
    {
        public Guid Id; public uint Pid;
        public Key(uint pid) { Id = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"); Pid = pid; }
    }
    [StructLayout(LayoutKind.Sequential)] struct Variant
    {
        public ushort Type, R1, R2, R3; public IntPtr Value, Extra;
    }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface Store
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out Key key);
        [PreserveSig] int GetValue(ref Key key, out Variant value);
        [PreserveSig] int SetValue(ref Key key, ref Variant value);
        [PreserveSig] int Commit();
    }
    [DllImport("shell32.dll")] static extern int SHGetPropertyStoreForWindow(IntPtr window, ref Guid iid, out Store store);
    [DllImport("ole32.dll")] static extern int PropVariantClear(ref Variant value);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    sealed class Saved { public uint Process; public Variant[] Values = new Variant[4]; }
    static readonly Dictionary<IntPtr, Saved> originals = new Dictionary<IntPtr, Saved>();

    static Store Open(IntPtr window)
    {
        Guid iid = typeof(Store).GUID; Store store;
        Marshal.ThrowExceptionForHR(SHGetPropertyStoreForWindow(window, ref iid, out store));
        return store;
    }
    static void Set(Store store, uint pid, string value)
    {
        var key = new Key(pid);
        var variant = new Variant { Type = 31, Value = Marshal.StringToCoTaskMemUni(value) };
        try { Marshal.ThrowExceptionForHR(store.SetValue(ref key, ref variant)); }
        finally { PropVariantClear(ref variant); }
    }
    static void Free(Saved saved)
    {
        for (int i = 0; i < saved.Values.Length; i++) PropVariantClear(ref saved.Values[i]);
    }
    static void PutBack(Store store, Saved saved)
    {
        for (uint i = 0; i < 4; i++) { var key = new Key(i + 2); Marshal.ThrowExceptionForHR(store.SetValue(ref key, ref saved.Values[i])); }
        Marshal.ThrowExceptionForHR(store.Commit());
    }
    public static bool Apply(IntPtr window, uint pid)
    {
        // Owned dialogs inherit grouping from their main window.
        if (GetWindow(window, 4) != IntPtr.Zero) return false;
        Saved previous;
        if (originals.TryGetValue(window, out previous))
        {
            if (previous.Process == pid) return false;
            Free(previous); originals.Remove(window);
        }
        Store store = Open(window);
        var saved = new Saved { Process = pid };
        try
        {
            for (uint i = 0; i < 4; i++)
            {
                var key = new Key(i + 2);
                Marshal.ThrowExceptionForHR(store.GetValue(ref key, out saved.Values[i]));
            }
            try
            {
                Set(store, 2, "\"" + System.Reflection.Assembly.GetExecutingAssembly().Location + "\" --relaunch");
                Set(store, 3, IconPath + ",0");
                Set(store, 4, "SQL Server Management Studio 22");
                // An independent ID prevents the registered SSMS shortcut from overriding the icon.
                Set(store, 5, "Local.SSMS22.ClassicIcon");
                Marshal.ThrowExceptionForHR(store.Commit());
            }
            catch { try { PutBack(store, saved); } catch { } throw; }
            originals[window] = saved;
            return true;
        }
        catch { Free(saved); throw; }
        finally { Marshal.ReleaseComObject(store); }
    }
    public static void Restore()
    {
        foreach (var item in originals)
        {
            Store store = null;
            try
            {
                uint pid; GetWindowThreadProcessId(item.Key, out pid);
                if (IsWindow(item.Key) && pid == item.Value.Process) { store = Open(item.Key); PutBack(store, item.Value); }
            }
            catch { }
            finally { if (store != null) Marshal.ReleaseComObject(store); Free(item.Value); }
        }
        originals.Clear();
    }
}
