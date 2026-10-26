using Wcc.Core.Wml;

namespace Wcc.Core.Scenarios;

internal static partial class WccScenarioFieldStaticReviewer
{
    private sealed class PresetActivationResolver(WmlStaticSource probe, WmlStaticSource main,
        IWmlStaticIncludeResolver captured) : IWmlStaticExistenceResolver
    {
        public WmlStaticIncludeResolution Resolve(string includePath, WmlStaticSource includingSource) =>
            ReferenceEquals(includingSource, probe) && includePath == "./_main.cfg"
                ? new(WmlStaticTruth.True, [main], "preset-activation-main:" + main.ContentHash)
                : captured.Resolve(includePath, includingSource);
        public WmlStaticIncludeResolution ResolveExistence(string includePath, WmlStaticSource includingSource) =>
            ReferenceEquals(includingSource, probe) && includePath == "./_main.cfg" ? Resolve(includePath, includingSource)
                : captured is IWmlStaticExistenceResolver existence ? existence.ResolveExistence(includePath, includingSource)
                : captured.Resolve(includePath, includingSource);
    }
}
