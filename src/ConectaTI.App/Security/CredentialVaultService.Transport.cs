using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ConectaTI.App.Security;

internal sealed record TransportProtection(int Version,string Cipher,string Kdf,int Iterations,string Salt);
public partial class CredentialVaultService
{
    private static byte[] TransportKey(string password,TransportProtection metadata)
    {
        var bytes=Encoding.UTF8.GetBytes(password);
        try { return Rfc2898DeriveBytes.Pbkdf2(bytes,Convert.FromBase64String(metadata.Salt),
            metadata.Iterations,HashAlgorithmName.SHA256,32); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private static byte[] EncryptPortableV2(byte[] plain,string password)
    {
        var metadata=new TransportProtection(2,"AES-256-GCM","PBKDF2-HMAC-SHA256",600_000,
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        var header=JsonSerializer.SerializeToUtf8Bytes(metadata);
        var aad=new byte[12+header.Length];
        "CTIENC02"u8.CopyTo(aad); BinaryPrimitives.WriteInt32LittleEndian(aad.AsSpan(8),header.Length);
        header.CopyTo(aad,12);
        var result=new byte[aad.Length+12+16+plain.Length];
        aad.CopyTo(result,0);
        var nonce=RandomNumberGenerator.GetBytes(12); nonce.CopyTo(result,aad.Length);
        var key=TransportKey(password,metadata);
        try
        {
            using var aes=new AesGcm(key,16);
            aes.Encrypt(nonce,plain,result.AsSpan(aad.Length+28),result.AsSpan(aad.Length+12,16),aad);
            return result;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
    private static byte[] DecryptPortableV2(byte[] file,string password)
    {
        if(file.Length<40)throw new InvalidDataException("Invalid package header.");
        var size=BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(8,4));
        if(size<1 || size>4096 || file.Length<12+size+28)throw new InvalidDataException("Invalid package header.");
        var metadata=JsonSerializer.Deserialize<TransportProtection>(file.AsSpan(12,size))
            ?? throw new InvalidDataException("Missing protection parameters.");
        if(metadata.Version!=2 || metadata.Cipher!="AES-256-GCM" || metadata.Kdf!="PBKDF2-HMAC-SHA256" ||
            metadata.Iterations<600_000 || metadata.Iterations>5_000_000 ||
            Convert.FromBase64String(metadata.Salt).Length!=32)throw new InvalidDataException("Unsupported protection parameters.");
        var key=TransportKey(password,metadata);
        var plain=new byte[file.Length-12-size-28];
        try
        {
            using var aes=new AesGcm(key,16);
            aes.Decrypt(file.AsSpan(12+size,12),file.AsSpan(12+size+28),file.AsSpan(12+size+12,16),plain,file.AsSpan(0,12+size));
            return plain;
        }
        catch { CryptographicOperations.ZeroMemory(plain);throw; }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
}
