global using AutoCardSync.Standalone.Core.Tests;

using AutoCardSync.Standalone.Services;

namespace AutoCardSync.Standalone.Core.Tests;

internal sealed class TestLoginAutoStartService(bool enabled = false) : LoginAutoStartService
{
    public bool Enabled { get; private set; } = enabled;
    public int SetCount { get; private set; }

    public override bool IsEnabled() => Enabled;

    public override void SetEnabled(bool enabled)
    {
        Enabled = enabled;
        SetCount++;
    }
}
