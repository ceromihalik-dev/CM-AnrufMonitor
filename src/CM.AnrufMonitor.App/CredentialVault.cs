using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace CM.AnrufMonitor.App;

/// <summary>Windows CurrentUser DPAPI storage. Plaintext passwords are never written to disk.</summary>
internal sealed class CredentialVault
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("CMACRED2");
    private readonly string _path;
    public CredentialVault(string directory) => _path = Path.Combine(directory, "Credentials.bin");
    public bool Exists => File.Exists(_path);

    public void Save(string host, int port, string username, string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length > 4096)
            throw new ArgumentException("Bitte ein Kennwort mit höchstens 4.096 Zeichen eingeben.");
        byte[] scope = Scope(host, port, username);
        byte[] plain = Encoding.UTF8.GetBytes(password);
        byte[] encrypted;
        try { encrypted = Transform(plain, scope, protect: true); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(Magic);
                stream.Write(scope);
                stream.Write(encrypted);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public string? Load(string host, int port, string username)
    {
        if (!File.Exists(_path)) return null;
        if (new FileInfo(_path).Length is < 41 or > 65536)
            throw new InvalidDataException("Die gespeicherten Zugangsdaten sind nicht lesbar. Die Datei bleibt erhalten. Bitte das Kennwort erneut eingeben und speichern.");
        byte[] stored = File.ReadAllBytes(_path);
        if (!stored.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException("Die gespeicherten Zugangsdaten haben ein unbekanntes Format. Die Datei bleibt erhalten. Bitte das Kennwort erneut eingeben und speichern.");
        byte[] scope = Scope(host, port, username);
        if (!CryptographicOperations.FixedTimeEquals(stored.AsSpan(Magic.Length, scope.Length), scope))
            throw new InvalidDataException("Das gespeicherte Kennwort gehört zu einer anderen Box-Adresse, einem anderen Port oder Benutzer. Die bisherige Datei bleibt erhalten. Für diesen Zugang bitte das Kennwort neu eingeben und speichern.");
        byte[] encrypted = stored.AsSpan(Magic.Length + scope.Length).ToArray();
        byte[] plain;
        try { plain = Transform(encrypted, scope, protect: false); }
        catch (Win32Exception)
        {
            throw new InvalidDataException("Windows konnte das gespeicherte Kennwort nicht entschlüsseln. Es ist an dieses Windows-Benutzerkonto gebunden. Die Datei bleibt erhalten; bitte das Kennwort erneut eingeben und speichern.");
        }
        try { return Encoding.UTF8.GetString(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public void Delete() { if (File.Exists(_path)) File.Delete(_path); }

    private static byte[] Scope(string host, int port, string username) => SHA256.HashData(Encoding.UTF8.GetBytes(
        "CM.AnrufMonitor.Credentials.v2\n" + host.Trim().ToLowerInvariant() + "\n" + port.ToString(CultureInfo.InvariantCulture) + "\n" + username.Trim()));

    private static byte[] Transform(byte[] input, byte[] entropy, bool protect)
    {
        DataBlob inputBlob = Allocate(input);
        DataBlob entropyBlob = Allocate(entropy);
        DataBlob outputBlob = default;
        try
        {
            bool success = protect
                ? CryptProtectData(ref inputBlob, "CM AnrufMonitor FRITZ-Zugang", ref entropyBlob, IntPtr.Zero, IntPtr.Zero, 1, out outputBlob)
                : CryptUnprotectData(ref inputBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, 1, out outputBlob);
            if (!success) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (outputBlob.Length is < 1 or > 65536) throw new InvalidDataException("Windows lieferte ungültige Zugangsdaten.");
            byte[] output = new byte[outputBlob.Length];
            Marshal.Copy(outputBlob.Data, output, 0, output.Length);
            return output;
        }
        finally
        {
            ClearAndFree(inputBlob);
            ClearAndFree(entropyBlob);
            if (outputBlob.Data != IntPtr.Zero)
            {
                if (!protect && outputBlob.Length is > 0 and <= 65536) Marshal.Copy(new byte[outputBlob.Length], 0, outputBlob.Data, outputBlob.Length);
                LocalFree(outputBlob.Data);
            }
        }
    }

    private static DataBlob Allocate(byte[] bytes)
    {
        IntPtr pointer = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        return new DataBlob { Length = bytes.Length, Data = pointer };
    }
    private static void ClearAndFree(DataBlob blob)
    {
        if (blob.Data == IntPtr.Zero) return;
        Marshal.Copy(new byte[blob.Length], 0, blob.Data, blob.Length);
        Marshal.FreeHGlobal(blob.Data);
    }

    [StructLayout(LayoutKind.Sequential)] private struct DataBlob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
