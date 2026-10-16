using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace CanvasDesktop;

/// <summary>OS credential storage; secrets never enter workspace JSON, logs, or source-controlled config.</summary>
internal sealed class ComposerCredentials : IComposerCredentials
{
    private const string Target = "CanvasWindowComposer/OpenAI/CanvasComposer";
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags, Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias, UserName;
    }
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ReadCredential(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WriteCredential(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteCredential(string target, uint type, uint flags);
    [DllImport("advapi32.dll")] private static extern void CredFree(IntPtr credential);
    public string? Read()
    {
        if (!ReadCredential(Target, 1, 0, out var pointer))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == 1168) return null;
            throw new Win32Exception(error, "Could not read the saved API key.");
        }
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            if (credential.CredentialBlobSize > 2560 || credential.CredentialBlobSize % 2 != 0) throw new InvalidOperationException("Invalid saved API key.");
            return Marshal.PtrToStringUni(credential.CredentialBlob, (int)credential.CredentialBlobSize / 2);
        }
        finally { CredFree(pointer); }
    }
    public void Write(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || Encoding.Unicode.GetByteCount(apiKey) > 2560) throw new ArgumentException("Invalid API key.");
        IntPtr blob = Marshal.StringToCoTaskMemUni(apiKey);
        try
        {
            var credential = new Credential { Type = 1, TargetName = Target, UserName = "OpenAI API", Persist = 2,
                CredentialBlobSize = (uint)Encoding.Unicode.GetByteCount(apiKey), CredentialBlob = blob };
            if (!WriteCredential(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not save the API key.");
        }
        finally { Marshal.ZeroFreeCoTaskMemUnicode(blob); }
    }
    public void Delete()
    {
        if (!DeleteCredential(Target, 1, 0) && Marshal.GetLastWin32Error() != 1168)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not remove the API key.");
    }
}
