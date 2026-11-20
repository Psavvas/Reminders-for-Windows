using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Reminders.Core;

internal interface ISecrets
{
    string? Read(string service, string account);
    void Write(string service, string account, string value);
    void Delete(string service, string account);
}

internal sealed class WindowsSecrets : ISecrets
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags, Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias, UserName;
    }
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredWrite(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll")] private static extern void CredFree(IntPtr credential);
    // Match keyring's Windows backend so existing credentials survive the port.
    private static string Target(string service, string account) => account + "." + service;
    public string? Read(string service, string account)
    {
        if (account.Length == 0) return null;
        if (!CredRead(Target(service, account), 1, 0, out var pointer)) { if (Marshal.GetLastWin32Error() == 1168) return null; throw new Win32Exception(); }
        try
        {
            var c = Marshal.PtrToStructure<Credential>(pointer);
            var bytes = new byte[c.CredentialBlobSize]; Marshal.Copy(c.CredentialBlob, bytes, 0, bytes.Length);
            try { return Encoding.Unicode.GetString(bytes); } finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally { CredFree(pointer); }
    }
    public void Write(string service, string account, string value)
    {
        var bytes = Encoding.Unicode.GetBytes(value);
        if (bytes.Length > 2560) throw new CoreException("ERROR", "The credential is too large for Windows Credential Manager").WithPresentation("ErrorCredentialTooLarge");
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            var c = new Credential { Type = 1, TargetName = Target(service, account), UserName = account, Persist = 3, CredentialBlobSize = (uint)bytes.Length, CredentialBlob = pointer };
            if (!CredWrite(ref c, 0)) throw new Win32Exception();
        }
        finally { CryptographicOperations.ZeroMemory(bytes); Marshal.Copy(bytes, 0, pointer, bytes.Length); Marshal.FreeHGlobal(pointer); }
    }
    public void Delete(string service, string account) { if (account.Length > 0 && !CredDelete(Target(service, account), 1, 0) && Marshal.GetLastWin32Error() != 1168) throw new Win32Exception(); }
}

internal sealed class SecretStore(ISecrets vault)
{
    internal const string PasswordService = "com.paulsavvas.reminders-sync.password";
    internal const string SessionService = "com.paulsavvas.reminders-sync.session";
    private static string Chunk(char slot, int index) => $"com.paulsavvas.reminders-sync.session.chunk.{slot}.{index:00}";
    internal static List<string> Chunks(string value)
    {
        var result = new List<string>(); var start = 0;
        while (start < value.Length) { var count = Math.Min(1000, value.Length - start); if (start + count < value.Length && char.IsHighSurrogate(value[start + count - 1])) count--; result.Add(value.Substring(start, count)); start += count; }
        if (result.Count == 0) result.Add(""); return result;
    }
    private static (char Slot, int Count)? Manifest(string? value)
    {
        var parts = value?.Split(':');
        return parts is { Length: 4 } && parts[0] == "chunks" && parts[1] == "v1" && parts[2] is "a" or "b" && int.TryParse(parts[3], out var count) && count is > 0 and <= 64 ? (parts[2][0], count) : null;
    }
    public string? Password(string account) => vault.Read(PasswordService, account);
    public bool HasPassword(string account) { try { return Password(account) is not null; } catch (Exception) { return false; } }
    public void Password(string account, string value) => vault.Write(PasswordService, account, value);
    public string? Session(string account)
    {
        var value = vault.Read(SessionService, account); if (Manifest(value) is not { } manifest) return value;
        return string.Concat(Enumerable.Range(0, manifest.Count).Select(i => vault.Read(Chunk(manifest.Slot, i), account) ?? throw new CoreException("ERROR", "Could not read the saved iCloud session").WithPresentation("ErrorSavedICloudSessionUnreadable")));
    }
    public void Session(string account, string value)
    {
        var chunks = Chunks(value); if (chunks.Count > 64) throw new CoreException("ERROR", "The iCloud session is unexpectedly large").WithPresentation("ErrorICloudSessionTooLarge");
        var previous = Manifest(vault.Read(SessionService, account)); var slot = previous?.Slot == 'a' ? 'b' : 'a';
        for (var i = 0; i < chunks.Count; i++) vault.Write(Chunk(slot, i), account, chunks[i]);
        vault.Write(SessionService, account, $"chunks:v1:{slot}:{chunks.Count}");
        if (previous is { } old) for (var i = 0; i < old.Count; i++) vault.Delete(Chunk(old.Slot, i), account);
    }
    public void Delete(string account)
    {
        vault.Delete(PasswordService, account); vault.Delete(SessionService, account);
        foreach (var slot in new[] { 'a', 'b' }) for (var i = 0; i < 64; i++) vault.Delete(Chunk(slot, i), account);
    }
}
