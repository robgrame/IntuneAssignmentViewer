using System.Security.Cryptography.X509Certificates;

namespace IntuneAssignmentViewer.Services;

/// <summary>
/// Loads an X509 certificate from multiple sources for Graph client-credentials auth.
/// </summary>
/// <remarks>
/// Source priority:
/// 1. <c>thumbprint</c> — looks up in the Windows certificate store (LocalMachine/CurrentUser, My/Root, etc.)
/// 2. <c>pfxBase64</c> — inline base64-encoded PFX content (useful for Kubernetes secrets / Key Vault references)
/// 3. <c>pfxPath</c> — file path to a PFX on disk
///
/// Each loaded certificate is validated (must have a private key, must be within
/// validity period). Validation failures throw <see cref="InvalidOperationException"/>
/// so misconfiguration is caught at startup instead of on the first Graph call.
/// </remarks>
public static class CertificateLoader
{
    public static X509Certificate2? Load(
        string? thumbprint,
        string? pfxPath,
        string? pfxPassword,
        string? pfxBase64,
        string storeLocationName,
        string storeNameName,
        ILogger logger)
    {
        X509Certificate2? cert = null;

        if (!string.IsNullOrWhiteSpace(thumbprint))
        {
            cert = LoadFromStore(thumbprint, storeLocationName, storeNameName, logger);
        }
        else if (!string.IsNullOrWhiteSpace(pfxBase64))
        {
            cert = LoadFromBase64(pfxBase64, pfxPassword, logger);
        }
        else if (!string.IsNullOrWhiteSpace(pfxPath))
        {
            cert = LoadFromFile(pfxPath, pfxPassword, logger);
        }

        if (cert == null) return null;

        ValidateOrThrow(cert);
        return cert;
    }

    // ---------- Loaders ----------

    private static X509Certificate2? LoadFromStore(string thumbprint, string storeLocationName, string storeNameName, ILogger logger)
    {
        var normalized = NormalizeThumbprint(thumbprint);
        if (normalized == null)
        {
            logger.LogError("Graph:CertificateThumbprint '{Thumb}' is not a valid hex thumbprint (expected 40 hex chars for SHA-1).", thumbprint);
            return null;
        }

        if (!OperatingSystem.IsWindows())
        {
            logger.LogWarning(
                "CertificateThumbprint store lookup is being used on a non-Windows host. " +
                "The cross-platform .NET certificate store is usually empty here. " +
                "Prefer Graph:CertificatePath or Graph:CertificateBase64 on Linux.");
        }

        if (!Enum.TryParse<StoreLocation>(storeLocationName, true, out var storeLocation))
        {
            logger.LogError("Invalid Graph:CertificateStoreLocation '{Loc}'. Use CurrentUser or LocalMachine.", storeLocationName);
            return null;
        }
        if (!Enum.TryParse<StoreName>(storeNameName, true, out var storeName))
        {
            logger.LogError("Invalid Graph:CertificateStoreName '{Name}'. Use My, Root, etc.", storeNameName);
            return null;
        }

        try
        {
            using var store = new X509Store(storeName, storeLocation);
            store.Open(OpenFlags.ReadOnly);
            var found = store.Certificates.Find(X509FindType.FindByThumbprint, normalized, validOnly: false);
            if (found.Count == 0)
            {
                logger.LogError("Certificate with thumbprint {Thumb} not found in {Location}\\{Name}",
                    normalized, storeLocation, storeName);
                return null;
            }
            return found[0];
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load certificate from store {Location}\\{Name}", storeLocation, storeName);
            return null;
        }
    }

    private static X509Certificate2? LoadFromBase64(string pfxBase64, string? pfxPassword, ILogger logger)
    {
        try
        {
            var bytes = Convert.FromBase64String(pfxBase64);
            // EphemeralKeySet: do NOT persist the private key on the host.
            // Key material lives only for the lifetime of this X509Certificate2 instance.
            return X509CertificateLoader.LoadPkcs12(bytes, pfxPassword,
                keyStorageFlags: X509KeyStorageFlags.EphemeralKeySet);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load certificate from Graph:CertificateBase64");
            return null;
        }
    }

    private static X509Certificate2? LoadFromFile(string pfxPath, string? pfxPassword, ILogger logger)
    {
        try
        {
            if (!File.Exists(pfxPath))
            {
                logger.LogError("Certificate file not found at path: {Path}", pfxPath);
                return null;
            }
            return X509CertificateLoader.LoadPkcs12FromFile(pfxPath, pfxPassword,
                keyStorageFlags: X509KeyStorageFlags.EphemeralKeySet);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load certificate from PFX file: {Path}", pfxPath);
            return null;
        }
    }

    // ---------- Validation & normalization ----------

    private static void ValidateOrThrow(X509Certificate2 cert)
    {
        if (!cert.HasPrivateKey)
            throw new InvalidOperationException(
                $"Graph certificate (thumbprint {cert.Thumbprint}) has no accessible private key. " +
                "If loading from the Windows store on IIS, grant the app pool identity 'read' on the private key " +
                "(certlm.msc -> certificate -> All Tasks -> Manage Private Keys).");

        var now = DateTimeOffset.UtcNow;
        if (now < cert.NotBefore)
            throw new InvalidOperationException(
                $"Graph certificate (thumbprint {cert.Thumbprint}) is not yet valid (NotBefore = {cert.NotBefore:o}).");

        if (now > cert.NotAfter)
            throw new InvalidOperationException(
                $"Graph certificate (thumbprint {cert.Thumbprint}) has expired (NotAfter = {cert.NotAfter:o}). " +
                "Rotate the certificate in the App Registration and update Graph:CertificateThumbprint / Graph:CertificatePath.");
    }

    /// <summary>
    /// Normalizes a thumbprint string by stripping common copy/paste artefacts
    /// (whitespace, ':' '-', leading '0x', invisible bidi/zero-width unicode characters)
    /// and validates it is a 40-char hex string (SHA-1) or 64-char (SHA-256).
    /// Returns the uppercase hex form, or null if invalid.
    /// </summary>
    internal static string? NormalizeThumbprint(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var sb = new System.Text.StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (char.IsWhiteSpace(ch) || ch == ':' || ch == '-') continue;
            // Strip non-printable and non-ASCII (LRM U+200E, ZWSP U+200B, etc.)
            if (ch < 0x20 || ch > 0x7E) continue;
            sb.Append(ch);
        }
        var cleaned = sb.ToString();
        if (cleaned.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            cleaned = cleaned[2..];

        for (int i = 0; i < cleaned.Length; i++)
        {
            if (!Uri.IsHexDigit(cleaned[i])) return null;
        }
        if (cleaned.Length != 40 && cleaned.Length != 64) return null;

        return cleaned.ToUpperInvariant();
    }
}
