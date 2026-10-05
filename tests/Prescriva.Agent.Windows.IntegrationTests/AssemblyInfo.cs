using Xunit;

// These tests launch real, visible Prescriva.Agent.TestTarget windows and, in the
// Automation tests, act on absolute screen coordinates. xUnit runs test classes in
// separate collections in parallel by default, which lets two launched windows land at
// the same default screen position and occlude one another (e.g. one window's title
// bar covering another's control). Serializing test collections keeps only one real
// desktop window under test at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
