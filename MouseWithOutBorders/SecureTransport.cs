using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace MouseWithOutBorders;

/// <summary>
/// TLS with a certificate pin authenticated by the high-entropy pairing secret.
/// Certificates are generated per connection; no CA, certificate store or TOFU
/// exception is needed. The pin is checked before sending any client proof.
/// </summary>
internal static class SecureTransport
{
    private const SslProtocols Protocols = SslProtocols.Tls12 | SslProtocols.Tls13;

    internal static async Task<(SslStream Stream, string Nonce)> ConnectAsync(TcpClient client, byte[] key, CancellationToken ct)
    {
        ValidateKey(key);
        client.NoDelay = true;
        NetworkStream network = client.GetStream();
        string hello = await ReadHelloAsync(network, ct).ConfigureAwait(false);
        string[] parts = hello.Split('|');
        if (parts[0] != "MOUSE3")
            throw new NotSupportedException("Update MWB on both PCs and enter the new MWB3 pairing code. Unencrypted connections are no longer supported.");
        if (parts.Length != 4 || !IsHex(parts[1], 48) || !IsHex(parts[2], 64) ||
            !VerifyMac(key, $"MOUSE3|{parts[1]}|{parts[2]}", parts[3]))
            throw new UnauthorizedAccessException("Cannot verify the other PC. Check its MWB3 pairing code.");

        byte[] pin = Convert.FromHexString(parts[2]);
        SslStream tls = new(network, leaveInnerStreamOpen: true);
        try
        {
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "MWB", // Identity is the authenticated pin, not a DNS name.
                EnabledSslProtocols = Protocols,
                AllowRenegotiation = false,
                AllowTlsResume = false,
                RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate is not null &&
                    CryptographicOperations.FixedTimeEquals(pin, certificate.GetCertHash(HashAlgorithmName.SHA256))
            }, ct).ConfigureAwait(false);
            return (tls, parts[1]);
        }
        catch (AuthenticationException ex)
        {
            tls.Dispose();
            throw new UnauthorizedAccessException("The other PC's encrypted identity could not be verified.", ex);
        }
        catch { tls.Dispose(); throw; }
    }

    internal static async Task<(SslStream Stream, string Nonce)> AcceptAsync(TcpClient client, byte[] key, CancellationToken ct)
    {
        ValidateKey(key);
        client.NoDelay = true;
        using X509Certificate2 certificate = CreateServerCertificate();
        string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        string signed = $"MOUSE3|{nonce}|{certificate.GetCertHashString(HashAlgorithmName.SHA256)}";
        NetworkStream network = client.GetStream();
        await network.WriteAsync(Encoding.ASCII.GetBytes(signed + "|" + Mac(key, signed) + "\n"), ct).ConfigureAwait(false);

        SslStream tls = new(network, leaveInnerStreamOpen: true);
        try
        {
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                EnabledSslProtocols = Protocols,
                ClientCertificateRequired = false, // Client proves the secret inside the pinned TLS channel.
                AllowRenegotiation = false,
                AllowTlsResume = false
            }, ct).ConfigureAwait(false);
            return (tls, nonce);
        }
        catch (AuthenticationException ex)
        {
            tls.Dispose();
            throw new UnauthorizedAccessException("Encrypted pairing failed.", ex);
        }
        catch { tls.Dispose(); throw; }
    }

    private static X509Certificate2 CreateServerCertificate()
    {
        using ECDsa identity = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest request = new("CN=MWB", identity, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        using X509Certificate2 generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        byte[] pfx = generated.Export(X509ContentType.Pkcs12);
        try
        {
            // Schannel cannot use EphemeralKeySet. DefaultKeySet gives it a
            // temporary user key container, deleted when the certificate is
            // disposed. Do not install a certificate or use PersistKeySet.
            return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.DefaultKeySet);
        }
        finally { CryptographicOperations.ZeroMemory(pfx); }
    }

    private static void ValidateKey(byte[] key)
    {
        if (key.Length != MousePeer.PairingCodeLength || key.Any(b => !"ABCDEFGHJKLMNPQRSTUVWXYZ23456789".Contains((char)b)))
            throw new ArgumentException("A new MWB3 pairing code is required.", nameof(key));
    }

    private static bool IsHex(string value, int length) => value.Length == length && value.All(Uri.IsHexDigit);

    private static string Mac(byte[] key, string message) =>
        Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(message)));

    private static bool VerifyMac(byte[] key, string message, string actual) => IsHex(actual, 64) &&
        CryptographicOperations.FixedTimeEquals(Convert.FromHexString(Mac(key, message)), Convert.FromHexString(actual));

    private static async Task<string> ReadHelloAsync(Stream stream, CancellationToken ct)
    {
        // Do not buffer past the greeting: the next byte belongs to TLS.
        byte[] one = new byte[1];
        StringBuilder line = new();
        while (line.Length < 1024)
        {
            if (await stream.ReadAsync(one, ct).ConfigureAwait(false) == 0) throw new IOException("Receiver disconnected during pairing.");
            if (one[0] == '\n') return line.ToString().TrimEnd('\r');
            if (one[0] > 127) throw new IOException("Invalid receiver greeting.");
            line.Append((char)one[0]);
        }
        throw new IOException("Oversized receiver greeting.");
    }
}
