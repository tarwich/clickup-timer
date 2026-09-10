using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace ClickUpTimer;

internal sealed class CredentialStore(string target = "ClickUpTimer/PersonalApiKey")
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags, Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public nint CredentialBlob;
        public uint Persist, AttributeCount;
        public nint Attributes;
        public string? TargetAlias, UserName;
    }
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WriteNative(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ReadNative(string target, uint type, uint flags, out nint credential);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool DeleteNative(string target, uint type, uint flags);
    [DllImport("advapi32.dll")] private static extern void CredFree(nint buffer);

    internal bool Exists()
    {
        if (!ReadNative(target, 1, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 1168) return false;
            throw new Win32Exception(error, "Windows Credential Manager could not check the saved ClickUp key.");
        }
        CredFree(pointer); return true;
    }
    internal string? Read()
    {
        if (!ReadNative(target, 1, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 1168) return null;
            throw new Win32Exception(error, "Windows Credential Manager could not read the ClickUp key.");
        }
        try
        {
            var value = Marshal.PtrToStructure<Credential>(pointer);
            return Marshal.PtrToStringUni(value.CredentialBlob, checked((int)value.CredentialBlobSize / 2));
        }
        finally { CredFree(pointer); }
    }
    internal void Write(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || Encoding.Unicode.GetByteCount(key) > 2560) throw new ArgumentException("Enter a valid personal API key.");
        var blob = Marshal.StringToCoTaskMemUni(key);
        try
        {
            var value = new Credential { Type = 1, TargetName = target, CredentialBlobSize = (uint)Encoding.Unicode.GetByteCount(key), CredentialBlob = blob, Persist = 2, UserName = "ClickUpTimer" };
            if (!WriteNative(ref value, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows Credential Manager could not save the ClickUp key.");
        }
        finally { Marshal.ZeroFreeCoTaskMemUnicode(blob); }
    }
    internal void Delete()
    {
        if (!DeleteNative(target, 1, 0) && Marshal.GetLastWin32Error() != 1168)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows Credential Manager could not remove the ClickUp key.");
    }
}
