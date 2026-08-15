using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ExHyperV.Services.Remote.Credentials;

public sealed class WindowsCredentialStore : IWindowsCredentialStore
{
    private const int ErrorNotFound = 1168;
    private const int MaxCredentialBlobBytes = 512;
    private const uint CredentialTypeGeneric = 1;
    private const uint CredentialPersistLocalMachine = 2;

    public void Save(string target, WindowsCredential credential)
    {
        ValidateTarget(target);
        ArgumentNullException.ThrowIfNull(credential);
        if (string.IsNullOrWhiteSpace(credential.UserName))
            throw new ArgumentException("凭据用户名不能为空。", nameof(credential));

        byte[] passwordBytes = Encoding.Unicode.GetBytes(credential.Password);
        if (passwordBytes.Length > MaxCredentialBlobBytes)
            throw new ArgumentException($"凭据密码不能超过 {MaxCredentialBlobBytes / 2} 个 UTF-16 字符。", nameof(credential));

        IntPtr password = IntPtr.Zero;
        try
        {
            if (passwordBytes.Length > 0)
            {
                password = Marshal.AllocHGlobal(passwordBytes.Length);
                Marshal.Copy(passwordBytes, 0, password, passwordBytes.Length);
            }

            var nativeCredential = new NativeCredential
            {
                Type = CredentialTypeGeneric,
                TargetName = target,
                CredentialBlobSize = (uint)passwordBytes.Length,
                CredentialBlob = password,
                Persist = CredentialPersistLocalMachine,
                UserName = credential.UserName
            };
            if (!CredWrite(ref nativeCredential, 0)) throw CreateException("保存 Windows 凭据失败");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
            if (password != IntPtr.Zero)
            {
                for (int index = 0; index < passwordBytes.Length; index++) Marshal.WriteByte(password, index, 0);
                Marshal.FreeHGlobal(password);
            }
        }
    }

    public bool TryRead(string target, out WindowsCredential? credential)
    {
        ValidateTarget(target);
        credential = null;
        if (!CredRead(target, CredentialTypeGeneric, 0, out IntPtr pointer))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound) return false;
            throw CreateException("读取 Windows 凭据失败", error);
        }

        try
        {
            NativeCredential nativeCredential = Marshal.PtrToStructure<NativeCredential>(pointer);
            string password = nativeCredential.CredentialBlob == IntPtr.Zero || nativeCredential.CredentialBlobSize == 0
                ? string.Empty
                : Marshal.PtrToStringUni(nativeCredential.CredentialBlob, checked((int)nativeCredential.CredentialBlobSize / 2))
                    ?? string.Empty;
            credential = new WindowsCredential(nativeCredential.UserName ?? string.Empty, password);
            return true;
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public bool Delete(string target)
    {
        ValidateTarget(target);
        if (CredDelete(target, CredentialTypeGeneric, 0)) return true;

        int error = Marshal.GetLastWin32Error();
        if (error == ErrorNotFound) return false;
        throw CreateException("删除 Windows 凭据失败", error);
    }

    private static void ValidateTarget(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) throw new ArgumentException("凭据目标不能为空。", nameof(target));
        if (target.Length > 32767) throw new ArgumentOutOfRangeException(nameof(target), "凭据目标过长。");
    }

    private static Win32Exception CreateException(string message, int? error = null)
    {
        int code = error ?? Marshal.GetLastWin32Error();
        return new Win32Exception(code, $"{message}（Win32 错误 {code}）。");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
