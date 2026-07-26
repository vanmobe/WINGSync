using System.Globalization;
using WingSync.Core.Domain;

namespace WingSync.Core.Tests;

internal static class DomainTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("Domain.WingValue preserves native types", WingValuePreservesNativeTypes);
        suite.Add("Domain.WingValue rejects mismatched accessors", WingValueRejectsMismatchedAccessors);
        suite.Add("Domain.WingValue formats invariantly", WingValueFormatsInvariantly);
        suite.Add("Domain.ChannelMapping copies input collections", ChannelMappingCopiesCollections);
        suite.Add("Domain.ChannelMapping requires a unique source", ChannelMappingRequiresUniqueSource);
        suite.Add("Domain.ChannelMapping keeps input and auxiliary namespaces separate", MappingNamespacesRemainSeparate);
        suite.Add("Domain.IdentityInputs maps all forty inputs", IdentityInputsMapsFortyInputs);
        suite.Add("Domain.Application defaults remain conservative", ApplicationDefaultsRemainConservative);
        suite.Add("Domain.Application copies scope collection", ApplicationCopiesScopeCollection);
        suite.Add("Domain.Discovered console creates serial-pinned endpoint", DiscoveryCreatesPinnedEndpoint);
        suite.Add("Domain.Pinned discovery follows serial across an IP move", PinnedDiscoveryFollowsSerial);
        suite.Add("Domain.Pinned discovery never falls back to a reused IP", PinnedDiscoveryRejectsReusedIp);
        suite.Add("Domain.Unpinned discovery may use its configured IP", UnpinnedDiscoveryUsesIp);
        suite.Add("Domain.Safety defaults remain fail-safe", SafetyDefaultsRemainFailSafe);
    }

    private static void WingValuePreservesNativeTypes()
    {
        var integer = WingValue.FromInt32(-17);
        var floatingPoint = WingValue.FromFloat(-12.75F);
        var text = WingValue.FromString("VOCAL 1");

        AssertEx.Equal(WingValueType.I, integer.Type);
        AssertEx.Equal(-17, integer.AsInt32());
        AssertEx.Equal(WingValueType.F, floatingPoint.Type);
        AssertEx.Equal(-12.75F, floatingPoint.AsFloat());
        AssertEx.Equal(WingValueType.S, text.Type);
        AssertEx.Equal("VOCAL 1", text.AsString());
        AssertEx.Throws<ArgumentNullException>(() => WingValue.FromString(null!));
    }

    private static void WingValueRejectsMismatchedAccessors()
    {
        var integer = WingValue.FromInt32(1);
        var floatingPoint = WingValue.FromFloat(1F);
        var text = WingValue.FromString("1");

        AssertEx.Throws<InvalidOperationException>(() => integer.AsFloat());
        AssertEx.Throws<InvalidOperationException>(() => integer.AsString());
        AssertEx.Throws<InvalidOperationException>(() => floatingPoint.AsInt32());
        AssertEx.Throws<InvalidOperationException>(() => floatingPoint.AsString());
        AssertEx.Throws<InvalidOperationException>(() => text.AsInt32());
        AssertEx.Throws<InvalidOperationException>(() => text.AsFloat());
    }

    private static void WingValueFormatsInvariantly()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("nl-BE");
            AssertEx.Equal("1234", WingValue.FromInt32(1234).ToString());
            AssertEx.Equal("12.5", WingValue.FromFloat(12.5F).ToString());
            AssertEx.Equal("A|B", WingValue.FromString("A|B").ToString());
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    private static void ChannelMappingCopiesCollections()
    {
        var inputs = new List<InputChannelMapping> { new(1, 10) };
        var auxiliaries = new List<AuxChannelMapping> { new(2, 7) };
        var mapping = new ChannelMapping(inputs, auxiliaries);

        inputs.Add(new InputChannelMapping(2, 11));
        auxiliaries.Clear();

        AssertEx.Equal(1, mapping.InputChannels.Count);
        AssertEx.Equal(1, mapping.AuxChannels.Count);
        AssertEx.True(mapping.TryMapInput(1, out var inputTarget));
        AssertEx.Equal(10, inputTarget);
        AssertEx.True(mapping.TryMapAux(2, out var auxTarget));
        AssertEx.Equal(7, auxTarget);
    }

    private static void ChannelMappingRequiresUniqueSource()
    {
        var mapping = new ChannelMapping(
        [
            new InputChannelMapping(3, 8),
            new InputChannelMapping(3, 9),
        ]);

        AssertEx.False(mapping.TryMapInput(3, out var duplicateTarget));
        AssertEx.Equal(0, duplicateTarget);
        AssertEx.False(mapping.TryMapInput(4, out var missingTarget));
        AssertEx.Equal(0, missingTarget);
    }

    private static void IdentityInputsMapsFortyInputs()
    {
        var mapping = ChannelMapping.IdentityInputs();
        AssertEx.Equal(40, mapping.InputChannels.Count);
        AssertEx.Equal(0, mapping.AuxChannels.Count);

        for (var channel = 1; channel <= 40; channel++)
        {
            AssertEx.True(mapping.TryMapInput(channel, out var target));
            AssertEx.Equal(channel, target);
        }
    }

    private static void MappingNamespacesRemainSeparate()
    {
        var mapping = new ChannelMapping(
            [new InputChannelMapping(2, 20)],
            [new AuxChannelMapping(2, 7)]);

        AssertEx.True(mapping.TryMapInput(2, out var inputTarget));
        AssertEx.Equal(20, inputTarget);
        AssertEx.True(mapping.TryMapAux(2, out var auxiliaryTarget));
        AssertEx.Equal(7, auxiliaryTarget);
    }

    private static void ApplicationDefaultsRemainConservative()
    {
        var configuration = new AppConfiguration(
            new WingEndpoint("10.0.0.10", ExpectedSerial: "FOH-1"),
            new WingEndpoint("10.0.0.11", ExpectedSerial: "MON-1"));

        AssertEx.Equal(SyncDirection.FohToMonitor, configuration.Direction);
        AssertEx.Equal(InitialSync.PreviewOnly, configuration.InitialSync);
        AssertEx.SequenceEqual(
        [
            SyncScope.Cust,
            SyncScope.Filter,
            SyncScope.Delay,
            SyncScope.Gate,
            SyncScope.Dyn,
            SyncScope.Eq,
        ],
            configuration.Scopes);
        AssertEx.SequenceEqual(AppConfiguration.SafeDefaultScopes, configuration.Scopes);
        AssertEx.Equal(40, configuration.Channels.InputChannels.Count);
    }

    private static void ApplicationCopiesScopeCollection()
    {
        var scopes = new List<SyncScope> { SyncScope.Cust };
        var configuration = new AppConfiguration(
            new WingEndpoint("10.0.0.10"),
            new WingEndpoint("10.0.0.11"),
            scopes: scopes);
        scopes.Add(SyncScope.Eq);

        AssertEx.SequenceEqual([SyncScope.Cust], configuration.Scopes);
    }

    private static void DiscoveryCreatesPinnedEndpoint()
    {
        var discovered = new DiscoveredWing(
            "10.0.0.10",
            "AUDIOLAB-DESK",
            "wing-fullsize",
            "SERIAL-ABC",
            "3.1",
            DateTimeOffset.UnixEpoch);

        AssertEx.Equal(
            new WingEndpoint("10.0.0.10", WingEndpoint.DefaultPort, "SERIAL-ABC"),
            discovered.Endpoint);
    }

    private static void PinnedDiscoveryFollowsSerial()
    {
        var movedPinnedConsole = CreateDiscoveredWing("10.0.0.12", "SERIAL-A");
        var consoleAtOldAddress = CreateDiscoveredWing("10.0.0.10", "SERIAL-B");

        var selected = PinnedWingSelection.Find(
            [consoleAtOldAddress, movedPinnedConsole],
            "serial-a",
            "10.0.0.10");

        AssertEx.Equal(movedPinnedConsole, selected);
    }

    private static void PinnedDiscoveryRejectsReusedIp()
    {
        var consoleAtOldAddress = CreateDiscoveredWing("10.0.0.10", "SERIAL-B");

        var selected = PinnedWingSelection.Find(
            [consoleAtOldAddress],
            "SERIAL-A",
            "10.0.0.10");

        AssertEx.Equal<DiscoveredWing?>(null, selected);
    }

    private static void UnpinnedDiscoveryUsesIp()
    {
        var configuredConsole = CreateDiscoveredWing("10.0.0.10", "SERIAL-B");

        var selected = PinnedWingSelection.Find(
            [configuredConsole],
            null,
            "10.0.0.10");

        AssertEx.Equal(configuredConsole, selected);
    }

    private static DiscoveredWing CreateDiscoveredWing(string ipAddress, string serialNumber) =>
        new(
            ipAddress,
            $"WING-{serialNumber}",
            "wing",
            serialNumber,
            "3.1",
            DateTimeOffset.UnixEpoch);

    private static void SafetyDefaultsRemainFailSafe()
    {
        var safety = SafetySettings.SafeDefaults;
        AssertEx.True(safety.DryRun);
        AssertEx.True(safety.RequireReadback);
        AssertEx.False(safety.AllowHighRiskWrites);
        AssertEx.True(safety.StopOnVerificationFailure);
        AssertEx.Equal(0.0001F, safety.FloatTolerance);
        AssertEx.Equal(TimeSpan.FromSeconds(2), safety.EchoSuppressionWindow);
    }
}
