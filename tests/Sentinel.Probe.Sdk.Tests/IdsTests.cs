using System;
using System.Collections.Generic;
using Xunit;
using Sentinel.Probe.Sdk.Ids;

namespace Sentinel.Probe.Sdk.Tests;

public class IdsTests
{
    [Fact]
    public void GeneratedIdentifiersAreDistinctUUIDs()
    {
        var seen = new HashSet<string>();
        for (int i = 0; i < 128; i++)
        {
            foreach (var id in new[] { ProbeIds.GenerateRequestId(), ProbeIds.GenerateIdempotencyKey() })
            {
                var parsed = Guid.Parse(id);
                Assert.Equal('4', parsed.ToString()[14]); // UUID version 4 marker at index 14
                Assert.DoesNotContain(id, seen);
                seen.Add(id);
            }
        }
    }
}
