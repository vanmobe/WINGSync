using WingSync.Core.Domain;
using WingSync.Core.Planning;

namespace WingSync.Core.Tests;

internal static class TokenAndScopeTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("TokenPath.Parsing canonicalizes outer whitespace", ParsingCanonicalizesOuterWhitespace);
        suite.Add("TokenPath.Malformed paths fail closed", MalformedPathsFailClosed);
        suite.Add("TokenPath.Channel recognition and replacement work", ChannelRecognitionAndReplacementWork);
        suite.Add("TokenPath.Read-only detection is segment-wide", ReadOnlyDetectionIsSegmentWide);
        suite.Add("TokenPath.Prefix match is case-insensitive", PrefixMatchIsCaseInsensitive);
        suite.Add("TokenPath.Equality is canonical and ordinal", EqualityIsCanonicalAndOrdinal);
        suite.Add("ScopeCatalog.Supports every public scope", SupportsEveryPublicScope);
        suite.Add("ScopeCatalog.Matches official token families", MatchesOfficialTokenFamilies);
        suite.Add("ScopeCatalog.Rejects unsafe or malformed families", RejectsUnsafeFamilies);
        suite.Add("ScopeCatalog.Rejects WAPI nodes absent from auxiliary channels", RejectsUnsupportedAuxiliaryFamilies);
        suite.Add("ScopeCatalog.Rewrites regular and auxiliary channels", RewritesMappedChannels);
        suite.Add("ScopeCatalog.Fails closed for absent mappings", FailsClosedForAbsentMappings);
        suite.Add("ScopeCatalog.Rewrites sidechain references", RewritesSidechainReferences);
        suite.Add("ScopeCatalog.Preserves non-channel sidechain values", PreservesNonChannelSidechainValues);
        suite.Add("ScopeCatalog.Snapshot nodes deduplicate shared trees", SnapshotNodesDeduplicateTrees);
        suite.Add("ScopeCatalog.Source snapshots deduplicate mapped sources", SourceSnapshotsDeduplicateSources);
        suite.Add("ScopeCatalog.Snapshot channel bounds are enforced", SnapshotBoundsAreEnforced);
        suite.Add("ScopeCatalog.Every input and auxiliary channel can be remapped", EveryChannelCanBeRemapped);
        suite.Add("ScopeCatalog.Every scope has a snapshot route", EveryScopeHasSnapshotRoute);
        suite.Add("ScopeCatalog.Auxiliary snapshot routes match WAPI 3.1", AuxiliarySnapshotRoutesMatchWapi31);
    }

    private static void ParsingCanonicalizesOuterWhitespace()
    {
        var path = TokenPath.Parse(" \t/ch/12/eq/1/g \r\n");
        AssertEx.Equal("/ch/12/eq/1/g", path.ToString());
        AssertEx.SequenceEqual(["ch", "12", "eq", "1", "g"], path.Segments);
        AssertEx.Equal("g", path.Leaf);
    }

    private static void MalformedPathsFailClosed()
    {
        string?[] invalid =
        [
            null,
            string.Empty,
            " ",
            "ch/1/eq",
            "/",
            "/ch/1/",
            "/ch//eq",
            "/ch/./eq",
            "/ch/../eq",
            "/ch/1/e q",
            "/ch/1/eq\tgain",
        ];

        foreach (var value in invalid)
        {
            AssertEx.False(TokenPath.TryParse(value, out _), $"'{value}' unexpectedly parsed.");
            AssertEx.Throws<FormatException>(() => TokenPath.Parse(value!));
        }
    }

    private static void ChannelRecognitionAndReplacementWork()
    {
        var input = TokenPath.Parse("/CH/40/eq/on");
        AssertEx.True(input.TryGetChannel(out var inputKind, out var inputChannel));
        AssertEx.Equal(WingChannelKind.Input, inputKind);
        AssertEx.Equal(40, inputChannel);
        AssertEx.Equal("/CH/3/eq/on", input.WithChannel(3).ToString());

        var auxiliary = TokenPath.Parse("/aux/8/gate/thr");
        AssertEx.True(auxiliary.TryGetChannel(out var auxKind, out var auxChannel));
        AssertEx.Equal(WingChannelKind.Aux, auxKind);
        AssertEx.Equal(8, auxChannel);
        AssertEx.Equal("/aux/1/gate/thr", auxiliary.WithChannel(1).ToString());

        AssertEx.False(TokenPath.Parse("/bus/1/fdr").TryGetChannel(out _, out _));
        AssertEx.Throws<InvalidOperationException>(
            () => TokenPath.Parse("/bus/1/fdr").WithChannel(1));
        AssertEx.Throws<ArgumentOutOfRangeException>(() => input.WithChannel(0));
        AssertEx.Throws<ArgumentOutOfRangeException>(() => input.WithChannel(41));
        AssertEx.Throws<ArgumentOutOfRangeException>(() => auxiliary.WithChannel(9));
    }

    private static void ReadOnlyDetectionIsSegmentWide()
    {
        AssertEx.True(TokenPath.Parse("/ch/1/$stat").IsReadOnly);
        AssertEx.True(TokenPath.Parse("/$ch/1/eq/on").IsReadOnly);
        AssertEx.False(TokenPath.Parse("/ch/1/eq/not$readonly").IsReadOnly);
        AssertEx.False(TokenPath.Parse("/ch/1/eq/on").IsReadOnly);
    }

    private static void PrefixMatchIsCaseInsensitive()
    {
        var path = TokenPath.Parse("/Ch/1/EQ/1/G");
        AssertEx.True(path.StartsWith("ch", "1", "eq"));
        AssertEx.True(path.StartsWith());
        AssertEx.False(path.StartsWith("ch", "2"));
        AssertEx.False(path.StartsWith("ch", "1", "eq", "1", "g", "extra"));
    }

    private static void EqualityIsCanonicalAndOrdinal()
    {
        var first = TokenPath.Parse("/ch/1/eq/on");
        var same = TokenPath.Parse(" /ch/1/eq/on ");
        var differentCase = TokenPath.Parse("/CH/1/eq/on");

        AssertEx.True(first.Equals(same));
        AssertEx.Equal(first.GetHashCode(), same.GetHashCode());
        AssertEx.False(first.Equals(differentCase));
    }

    private static void SupportsEveryPublicScope()
    {
        AssertEx.SequenceEqual(Enum.GetValues<SyncScope>(), ScopeCatalog.SupportedScopes);
    }

    private static void MatchesOfficialTokenFamilies()
    {
        (string Path, SyncScope Scope)[] examples =
        [
            ("/ch/1/name", SyncScope.Cust),
            ("/aux/8/led", SyncScope.Cust),
            ("/ch/1/tags", SyncScope.Tags),
            ("/aux/1/tags", SyncScope.Tags),
            ("/ch/1/in/conn/altgrp", SyncScope.Conn),
            ("/aux/1/in/conn/altin", SyncScope.Conn),
            ("/ch/1/in/set/srcauto", SyncScope.Conn),
            ("/aux/1/in/set/srcauto", SyncScope.Conn),
            ("/ch/1/in/set/trim", SyncScope.In),
            ("/aux/1/in/set/trim", SyncScope.In),
            ("/ch/1/flt/hpfon", SyncScope.Filter),
            ("/ch/1/in/set/dly", SyncScope.Delay),
            ("/aux/1/in/set/dly", SyncScope.Delay),
            ("/ch/1/gate/thr", SyncScope.Gate),
            ("/ch/1/gatesc/src", SyncScope.Gate),
            ("/ch/1/dyn/ratio", SyncScope.Dyn),
            ("/aux/1/dyn/ratio", SyncScope.Dyn),
            ("/ch/1/dynxo/f1", SyncScope.Dyn),
            ("/ch/1/dynsc/src", SyncScope.Dyn),
            ("/aux/1/dynsc/src", SyncScope.Dyn),
            ("/ch/1/preins/on", SyncScope.Pre),
            ("/aux/1/preins/on", SyncScope.Pre),
            ("/ch/1/postins/w", SyncScope.Post),
            ("/ch/1/eq/1/g", SyncScope.Eq),
            ("/aux/1/eq/1", SyncScope.Eq),
            ("/ch/1/peq/2/q", SyncScope.Eq),
            ("/ch/1/tapwid", SyncScope.Pan),
            ("/aux/1/wid", SyncScope.Pan),
            ("/ch/1/main/1/lvl", SyncScope.Main1),
            ("/aux/1/main/1/lvl", SyncScope.Main1),
            ("/ch/1/main/2/on", SyncScope.Main2),
            ("/ch/1/main/3/pre", SyncScope.Main3),
            ("/ch/1/main/4/lvl", SyncScope.Main4),
            ("/ch/1/send/16/pan", SyncScope.Send),
            ("/aux/1/send/16/pan", SyncScope.Send),
            ("/ch/1/send/MX.8/on", SyncScope.Send),
            ("/ch/1/send/MX8/lvl", SyncScope.Send),
            ("/ch/1/fdr", SyncScope.Fdr),
            ("/aux/1/fdr", SyncScope.Fdr),
            ("/ch/1/mute", SyncScope.Mute),
            ("/aux/1/mute", SyncScope.Mute),
            ("/ch/1/solosafe", SyncScope.Config),
            ("/aux/1/mon", SyncScope.Config),
        ];

        foreach (var example in examples)
        {
            var path = TokenPath.Parse(example.Path);
            AssertEx.True(ScopeCatalog.TryMatch(path, out var actual), example.Path);
            AssertEx.Equal(example.Scope, actual, example.Path);
            AssertEx.True(ScopeCatalog.Matches(example.Scope, path), example.Path);
        }
    }

    private static void RejectsUnsafeFamilies()
    {
        string[] rejected =
        [
            "/io/in/1/gain",
            "/io/in/1/pp",
            "/ch/0/eq/on",
            "/ch/41/eq/on",
            "/aux/9/eq/on",
            "/ch/1/$stat",
            "/ch/1/name/extra",
            "/ch/1/in/conn/unknown",
            "/ch/1/in/set/gain",
            "/ch/1/preins/unknown",
            "/ch/1/main/5/on",
            "/ch/1/main/1/unknown",
            "/ch/1/send/17/on",
            "/ch/1/send/MX.9/on",
            "/ch/1/send/1/unknown",
            "/ch/1/not-a-scope/value",
        ];

        foreach (var value in rejected)
        {
            AssertEx.False(
                ScopeCatalog.TryMatch(TokenPath.Parse(value), out _),
                $"'{value}' unexpectedly matched.");
        }
    }

    private static void RejectsUnsupportedAuxiliaryFamilies()
    {
        // These sections exist on /ch in the bundled WAPI 3.1 token range,
        // but do not exist anywhere in AUX_1 through AUX_8.
        string[] rejected =
        [
            "/aux/1/flt/lc",
            "/aux/1/gate/on",
            "/aux/1/gatesc/src",
            "/aux/1/dynxo/f",
            "/aux/1/postins/on",
            "/aux/1/peq/on",
            "/aux/1/tapwid",
            "/aux/1/proc",
            "/aux/1/ptap",
        ];

        foreach (var value in rejected)
        {
            var path = TokenPath.Parse(value);
            AssertEx.False(
                ScopeCatalog.TryMatch(path, out _),
                $"Unsupported WAPI AUX token '{value}' unexpectedly matched.");

            var rewrite = ScopeCatalog.Rewrite(
                path,
                WingValue.FromInt32(0),
                new ChannelMapping(
                    auxChannels: [new AuxChannelMapping(1, 2)]));
            AssertEx.Equal(
                TokenRewriteStatus.UnsupportedToken,
                rewrite.Status,
                value);
        }
    }

    private static void RewritesMappedChannels()
    {
        var mapping = new ChannelMapping(
            [new InputChannelMapping(3, 17)],
            [new AuxChannelMapping(2, 7)]);

        var input = ScopeCatalog.Rewrite(
            TokenPath.Parse("/ch/3/eq/1/g"),
            WingValue.FromFloat(2.5F),
            mapping);
        AssertEx.True(input.IsSuccess);
        AssertEx.Equal(SyncScope.Eq, input.Scope);
        AssertEx.Equal("/ch/17/eq/1/g", input.TargetPath!.ToString());
        AssertEx.Equal(2.5F, input.TargetValue!.Value.AsFloat());

        var auxiliary = ScopeCatalog.Rewrite(
            TokenPath.Parse("/aux/2/dyn/thr"),
            WingValue.FromFloat(-20F),
            mapping);
        AssertEx.True(auxiliary.IsSuccess);
        AssertEx.Equal(SyncScope.Dyn, auxiliary.Scope);
        AssertEx.Equal("/aux/7/dyn/thr", auxiliary.TargetPath!.ToString());
    }

    private static void FailsClosedForAbsentMappings()
    {
        var noMapping = ScopeCatalog.Rewrite(
            TokenPath.Parse("/ch/1/eq/on"),
            WingValue.FromInt32(1),
            new ChannelMapping());
        AssertEx.Equal(TokenRewriteStatus.MissingChannelMapping, noMapping.Status);
        AssertEx.False(noMapping.IsSuccess);

        var duplicateMapping = ScopeCatalog.Rewrite(
            TokenPath.Parse("/ch/1/eq/on"),
            WingValue.FromInt32(1),
            new ChannelMapping(
            [
                new InputChannelMapping(1, 2),
                new InputChannelMapping(1, 3),
            ]));
        AssertEx.Equal(TokenRewriteStatus.MissingChannelMapping, duplicateMapping.Status);

        var readOnly = ScopeCatalog.Rewrite(
            TokenPath.Parse("/ch/1/$stat"),
            WingValue.FromString("x"),
            ChannelMapping.IdentityInputs());
        AssertEx.Equal(TokenRewriteStatus.ReadOnlyToken, readOnly.Status);

        var unsupported = ScopeCatalog.Rewrite(
            TokenPath.Parse("/io/in/1/gain"),
            WingValue.FromFloat(1F),
            ChannelMapping.IdentityInputs());
        AssertEx.Equal(TokenRewriteStatus.UnsupportedToken, unsupported.Status);
    }

    private static void RewritesSidechainReferences()
    {
        var mapping = new ChannelMapping(
        [
            new InputChannelMapping(2, 12),
            new InputChannelMapping(3, 17),
        ],
        [
            new AuxChannelMapping(4, 7),
        ]);
        string[] inputForms = ["CH2", "CH.2", "CH:2", "CH/2", "ch.2"];
        string[] expectedForms = ["CH12", "CH.12", "CH:12", "CH/12", "ch.12"];

        for (var index = 0; index < inputForms.Length; index++)
        {
            var result = ScopeCatalog.Rewrite(
                TokenPath.Parse("/ch/3/gatesc/src"),
                WingValue.FromString(inputForms[index]),
                mapping);
            AssertEx.True(result.IsSuccess);
            AssertEx.Equal(expectedForms[index], result.TargetValue!.Value.AsString());
            AssertEx.True(ScopeCatalog.IsSidechainSourceToken(
                TokenPath.Parse("/ch/3/gatesc/src")));
        }

        var aux = ScopeCatalog.Rewrite(
            TokenPath.Parse("/ch/3/dynsc/src"),
            WingValue.FromString("AUX.4"),
            mapping);
        AssertEx.True(aux.IsSuccess);
        AssertEx.Equal("AUX.7", aux.TargetValue!.Value.AsString());

        var integer = ScopeCatalog.Rewrite(
            TokenPath.Parse("/ch/3/dynsc/src"),
            WingValue.FromInt32(2),
            mapping);
        AssertEx.True(integer.IsSuccess);
        AssertEx.Equal(12, integer.TargetValue!.Value.AsInt32());

        var unresolved = ScopeCatalog.Rewrite(
            TokenPath.Parse("/ch/3/dynsc/src"),
            WingValue.FromString("CH.9"),
            mapping);
        AssertEx.Equal(TokenRewriteStatus.UnresolvedSidechainReference, unresolved.Status);

        var identityNumbering = new ChannelMapping([new InputChannelMapping(2, 2)]);
        var preservedIdentityReference = ScopeCatalog.Rewrite(
            TokenPath.Parse("/ch/2/gatesc/src"),
            WingValue.FromString("CH.1"),
            identityNumbering);
        AssertEx.True(preservedIdentityReference.IsSuccess);
        AssertEx.Equal("CH.1", preservedIdentityReference.TargetValue!.Value.AsString());
    }

    private static void PreservesNonChannelSidechainValues()
    {
        var mapping = new ChannelMapping([new InputChannelMapping(3, 17)]);
        WingValue[] values =
        [
            WingValue.FromString("SELF"),
            WingValue.FromString("OFF"),
            WingValue.FromString("BUS.1"),
            WingValue.FromInt32(0),
            WingValue.FromInt32(41),
            WingValue.FromFloat(3F),
        ];

        foreach (var value in values)
        {
            var result = ScopeCatalog.Rewrite(
                TokenPath.Parse("/ch/3/gatesc/src"),
                value,
                mapping);
            AssertEx.True(result.IsSuccess);
            AssertEx.Equal(value, result.TargetValue!.Value);
        }
    }

    private static void SnapshotNodesDeduplicateTrees()
    {
        var nodes = ScopeCatalog.GetSnapshotNodes(
            [SyncScope.Conn, SyncScope.In, SyncScope.Delay, SyncScope.Eq, SyncScope.Eq],
            WingChannelKind.Input,
            7);

        AssertEx.SequenceEqual(
        [
            "/ch/7/eq",
            "/ch/7/in/conn",
            "/ch/7/in/set",
            "/ch/7/peq",
        ],
            nodes.Select(static node => node.Path.ToString()));
        var inputSet = nodes.Single(static node => node.Path.ToString() == "/ch/7/in/set");
        AssertEx.SequenceEqual(
            [SyncScope.Conn, SyncScope.In, SyncScope.Delay],
            inputSet.ServedScopes);
    }

    private static void SourceSnapshotsDeduplicateSources()
    {
        var mapping = new ChannelMapping(
        [
            new InputChannelMapping(1, 10),
            new InputChannelMapping(1, 11),
            new InputChannelMapping(3, 12),
        ],
        [
            new AuxChannelMapping(2, 5),
        ]);

        var nodes = ScopeCatalog.GetSourceSnapshotNodes([SyncScope.Cust], mapping);
        AssertEx.Equal(12, nodes.Count);
        AssertEx.SequenceEqual(
        [
            "/aux/2/col",
            "/aux/2/icon",
            "/aux/2/led",
            "/aux/2/name",
            "/ch/1/col",
            "/ch/1/icon",
            "/ch/1/led",
            "/ch/1/name",
            "/ch/3/col",
            "/ch/3/icon",
            "/ch/3/led",
            "/ch/3/name",
        ],
            nodes.Select(static node => node.Path.ToString()));
    }

    private static void SnapshotBoundsAreEnforced()
    {
        AssertEx.Throws<ArgumentOutOfRangeException>(
            () => ScopeCatalog.GetSnapshotNodes([SyncScope.Eq], WingChannelKind.Input, 0));
        AssertEx.Throws<ArgumentOutOfRangeException>(
            () => ScopeCatalog.GetSnapshotNodes([SyncScope.Eq], WingChannelKind.Input, 41));
        AssertEx.Throws<ArgumentOutOfRangeException>(
            () => ScopeCatalog.GetSnapshotNodes([SyncScope.Eq], WingChannelKind.Aux, 9));
        AssertEx.Throws<ArgumentOutOfRangeException>(
            () => ScopeCatalog.GetSnapshotNodes([SyncScope.Eq], (WingChannelKind)999, 1));
    }

    private static void EveryChannelCanBeRemapped()
    {
        var inputs = Enumerable.Range(1, 40)
            .Select(static channel => new InputChannelMapping(channel, 41 - channel));
        var auxiliaries = Enumerable.Range(1, 8)
            .Select(static channel => new AuxChannelMapping(channel, 9 - channel));
        var mapping = new ChannelMapping(inputs, auxiliaries);

        for (var channel = 1; channel <= 40; channel++)
        {
            var result = ScopeCatalog.Rewrite(
                TokenPath.Parse($"/ch/{channel}/eq/on"),
                WingValue.FromInt32(1),
                mapping);
            AssertEx.True(result.IsSuccess);
            AssertEx.Equal($"/ch/{41 - channel}/eq/on", result.TargetPath!.ToString());
        }

        for (var channel = 1; channel <= 8; channel++)
        {
            var result = ScopeCatalog.Rewrite(
                TokenPath.Parse($"/aux/{channel}/eq/on"),
                WingValue.FromInt32(1),
                mapping);
            AssertEx.True(result.IsSuccess);
            AssertEx.Equal($"/aux/{9 - channel}/eq/on", result.TargetPath!.ToString());
        }
    }

    private static void EveryScopeHasSnapshotRoute()
    {
        foreach (var scope in Enum.GetValues<SyncScope>())
        {
            var nodes = ScopeCatalog.GetSnapshotNodes(
                [scope],
                WingChannelKind.Input,
                1);
            AssertEx.True(nodes.Count > 0, $"Scope '{scope}' has no snapshot node.");
            AssertEx.True(
                nodes.All(node => node.ServedScopes.Contains(scope)),
                $"Scope '{scope}' is not marked as served.");
        }
    }

    private static void AuxiliarySnapshotRoutesMatchWapi31()
    {
        Dictionary<SyncScope, string[]> expectedByScope =
            new Dictionary<SyncScope, string[]>
            {
                [SyncScope.Cust] =
                    ["/aux/1/col", "/aux/1/icon", "/aux/1/led", "/aux/1/name"],
                [SyncScope.Tags] = ["/aux/1/tags"],
                [SyncScope.Conn] = ["/aux/1/in/conn", "/aux/1/in/set"],
                [SyncScope.In] = ["/aux/1/in/set"],
                [SyncScope.Filter] = [],
                [SyncScope.Delay] = ["/aux/1/in/set"],
                [SyncScope.Gate] = [],
                [SyncScope.Dyn] = ["/aux/1/dyn", "/aux/1/dynsc"],
                [SyncScope.Pre] = ["/aux/1/preins"],
                [SyncScope.Post] = [],
                [SyncScope.Eq] = ["/aux/1/eq"],
                [SyncScope.Pan] = ["/aux/1/pan", "/aux/1/wid"],
                [SyncScope.Main1] = ["/aux/1/main/1"],
                [SyncScope.Main2] = ["/aux/1/main/2"],
                [SyncScope.Main3] = ["/aux/1/main/3"],
                [SyncScope.Main4] = ["/aux/1/main/4"],
                [SyncScope.Send] = ["/aux/1/send"],
                [SyncScope.Fdr] = ["/aux/1/fdr"],
                [SyncScope.Mute] = ["/aux/1/mute"],
                [SyncScope.Config] = ["/aux/1/mon", "/aux/1/solosafe"],
            };

        AssertEx.Equal(
            Enum.GetValues<SyncScope>().Length,
            expectedByScope.Count,
            "Every global UI scope must have an explicit AUX routing decision.");

        foreach (var scope in Enum.GetValues<SyncScope>())
        {
            var nodes = ScopeCatalog.GetSnapshotNodes(
                [scope],
                WingChannelKind.Aux,
                1);
            AssertEx.SequenceEqual(
                expectedByScope[scope],
                nodes.Select(static node => node.Path.ToString()),
                scope.ToString());
            AssertEx.True(
                nodes.All(node => node.ServedScopes.SequenceEqual([scope])),
                $"AUX scope '{scope}' has incorrect served-scope metadata.");
        }
    }
}
