namespace Prescriva.Agent.Application.Security;

public interface IPayloadProtector
{
    byte[] Protect(byte[] plaintext);

    byte[] Unprotect(byte[] ciphertext);
}
