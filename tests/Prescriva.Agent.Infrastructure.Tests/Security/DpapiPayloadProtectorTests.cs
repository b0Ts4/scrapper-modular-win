using System.Text;
using Prescriva.Agent.Infrastructure.Security;

namespace Prescriva.Agent.Infrastructure.Tests.Security;

public sealed class DpapiPayloadProtectorTests
{
    [Fact]
    public void Protect_then_unprotect_round_trips_for_the_current_windows_user()
    {
        var protector = new DpapiPayloadProtector();
        var plaintext = Encoding.UTF8.GetBytes("paciente: Maria da Silva; valor: 42.50");

        var ciphertext = protector.Protect(plaintext);
        var roundTripped = protector.Unprotect(ciphertext);

        Assert.Equal(plaintext, roundTripped);
    }

    [Fact]
    public void Protect_never_returns_bytes_containing_the_plaintext()
    {
        var protector = new DpapiPayloadProtector();
        var plaintext = Encoding.UTF8.GetBytes("segredo-super-especifico-9f3c1a2b");

        var ciphertext = protector.Protect(plaintext);
        var ciphertextText = Convert.ToBase64String(ciphertext);

        Assert.DoesNotContain("segredo-super-especifico-9f3c1a2b", ciphertextText, StringComparison.Ordinal);
    }

    [Fact]
    public void Unprotect_throws_for_ciphertext_that_was_not_produced_by_dpapi()
    {
        var protector = new DpapiPayloadProtector();
        var corrupt = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };

        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => protector.Unprotect(corrupt));
    }
}
