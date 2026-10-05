namespace Prescriva.Agent.Domain.Configuration;

/// <summary>
/// Records that a configuration's exact content - identified by the content hash an
/// Application-layer <c>ConfigurationFingerprint</c> computes over it - was proven to
/// work by running <c>IntegrationTestRunner</c>'s test suite against a live application
/// and passing every check (fields resolve, triggers are detected).
///
/// Deliberately plain Domain data: it only ever carries the configuration's identifier,
/// the fingerprint it was approved for, and when. It has no reference to
/// Application/Windows types and does not itself know how to compute a fingerprint - that
/// stays the Application layer's job, so this record can be trusted to do nothing but
/// compare two already-computed strings.
///
/// Bound to the fingerprint, not just the configuration ID: <see cref="IsValidFor"/> is
/// what makes this a genuine test-then-activate gate rather than a one-time checkbox - an
/// approval only ever matches the exact content it was granted for. Any edit to the
/// configuration, however small, produces a different fingerprint, so a previously stored
/// approval stops being valid for it (see the <c>ConfigurationFingerprint</c> remarks for
/// exactly what "any edit" covers).
/// </summary>
public sealed record ConfigurationApproval(
    string ConfigurationId,
    string Fingerprint,
    DateTimeOffset ApprovedAtUtc)
{
    /// <summary>
    /// True only when this approval was granted for exactly <paramref name="currentFingerprint"/> -
    /// the configuration's current content hash. False for any other fingerprint, including
    /// one computed from the same configuration ID after any edit at all.
    /// </summary>
    public bool IsValidFor(string currentFingerprint) =>
        !string.IsNullOrEmpty(currentFingerprint) &&
        string.Equals(Fingerprint, currentFingerprint, StringComparison.Ordinal);
}
