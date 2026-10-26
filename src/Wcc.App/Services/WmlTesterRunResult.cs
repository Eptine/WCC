using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Wcc.Core.Wml.Checking;

namespace Wcc.App.Services;

/// <summary>Report and source encodings from the same run-local capture, never Save authority.</summary>
public sealed class WmlTesterRunResult
{
    public WmlTesterRunResult(WmlCheckReport report, IEnumerable<StandaloneWmlFileSnapshot>? files = null)
    {
        Report = report ?? throw new ArgumentNullException(nameof(report));
        Files = Array.AsReadOnly((files ?? []).ToArray());
    }

    public WmlCheckReport Report { get; }
    public IReadOnlyList<StandaloneWmlFileSnapshot> Files { get; }
}

public delegate WmlTesterRunResult WmlTesterCapturedCheck(IReadOnlyList<WmlCheckSource> sources,
    IReadOnlyList<WmlCheckModuleId> modules, CancellationToken cancellationToken);
