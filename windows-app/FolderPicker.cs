using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FlashbackStudio
{
    // Windows' own folder picker, in File Explorer's style (FolderBrowserDialog
    // shows the old tree of folders instead): the Common Item Dialog, through COM.
    static class FolderPicker
    {
        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        class FileOpenDialog
        {
        }

        // IFileDialog, up to GetResult (the methods are in their vtable order).
        [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IFileDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(uint count, IntPtr specs);
            void SetFileTypeIndex(uint index);
            void GetFileTypeIndex(out uint index);
            void Advise(IntPtr events, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(uint options);
            void GetOptions(out uint options);
            void SetDefaultFolder(IShellItem item);
            void SetFolder(IShellItem item);
            void GetFolder(out IShellItem item);
            void GetCurrentSelection(out IShellItem item);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            void GetResult(out IShellItem item);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItem
        {
            void BindToHandler(IntPtr bc, ref Guid handler, ref Guid iid, out IntPtr result);
            void GetParent(out IShellItem parent);
            void GetDisplayName(uint form, [MarshalAs(UnmanagedType.LPWStr)] out string name);
            void GetAttributes(uint mask, out uint attributes);
            void Compare(IShellItem item, uint hint, out int order);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void SHCreateItemFromParsingName(string path, IntPtr bc, ref Guid iid, out IShellItem item);

        const uint PickFolders = 0x20, ForceFileSystem = 0x40;
        const uint FileSystemPath = 0x80058000;     // (SIGDN_FILESYSPATH)

        // Asks for a folder, titled 'title', starting in 'start' (if it's
        // there). Returns its path, or null if the person cancels.
        public static string Pick(IWin32Window owner, string title, string start)
        {
            IFileDialog dialog = (IFileDialog)new FileOpenDialog();
            try
            {
                dialog.GetOptions(out uint options);
                dialog.SetOptions(options | PickFolders | ForceFileSystem);
                dialog.SetTitle(title);
                if (start != null && Directory.Exists(start))
                {
                    try
                    {
                        Guid iid = typeof(IShellItem).GUID;
                        SHCreateItemFromParsingName(start, IntPtr.Zero, ref iid, out IShellItem folder);
                        dialog.SetFolder(folder);
                    }
                    catch (COMException)
                    {
                        // (it starts wherever Windows likes, then)
                    }
                }
                if (dialog.Show(owner?.Handle ?? IntPtr.Zero) != 0)
                    return null;    // (cancelled)
                dialog.GetResult(out IShellItem result);
                result.GetDisplayName(FileSystemPath, out string path);
                return path;
            }
            finally
            {
                Marshal.ReleaseComObject(dialog);
            }
        }
    }
}
