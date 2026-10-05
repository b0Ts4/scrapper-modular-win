namespace Prescriva.Agent.Application.Runtime;

/// <summary>Whether the Agent starts, in background mode, when the current user signs in to Windows.</summary>
public interface IStartupRegistration
{
    bool IsEnabled { get; }

    void Enable(string executablePath);

    void Disable();
}
