using System.Runtime.Versioning;
using System.Security.Cryptography;
using Prescriva.Agent.Application.Security;

namespace Prescriva.Agent.Infrastructure.Security;

/// <summary>
/// Protects event payloads at rest using Windows DPAPI bound to the current Windows user account
/// running the Agent (<see cref="DataProtectionScope.CurrentUser"/>). Ciphertext produced by one
/// Windows user cannot be decrypted under a different Windows account or on a different machine.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiPayloadProtector : IPayloadProtector
{
    public byte[] Protect(byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        return ProtectedData.Protect(plaintext, optionalEntropy: null, DataProtectionScope.CurrentUser);
    }

    public byte[] Unprotect(byte[] ciphertext)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        return ProtectedData.Unprotect(ciphertext, optionalEntropy: null, DataProtectionScope.CurrentUser);
    }
}
