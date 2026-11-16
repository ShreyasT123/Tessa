using Tessellation.Configuration;

namespace Tessellation.Tests;

public sealed class PreferenceValidationTests
{
    [Fact]
    public void DefaultPreferencesAreValid()
    {
        PreferenceStore.Validate(new TessellationPreferences());
    }

    [Fact]
    public void WorkspaceNamesCannotExceedTheSupportedCount()
    {
        var preferences = new TessellationPreferences
        {
            WorkspaceNames = Enumerable.Repeat("x", 21).ToList()
        };

        Assert.Throws<InvalidDataException>(() => PreferenceStore.Validate(preferences));
    }

    [Fact]
    public void WorkspaceNamesCannotExceedTheSupportedLength()
    {
        var preferences = new TessellationPreferences
        {
            WorkspaceNames = ["this workspace label is too long"]
        };

        Assert.Throws<InvalidDataException>(() => PreferenceStore.Validate(preferences));
    }

    [Fact]
    public void NonFiniteScrollingColumnWidthIsRejected()
    {
        var preferences = new TessellationPreferences { ScrollColumnWidth = double.NaN };

        Assert.Throws<InvalidDataException>(() => PreferenceStore.Validate(preferences));
    }
}
