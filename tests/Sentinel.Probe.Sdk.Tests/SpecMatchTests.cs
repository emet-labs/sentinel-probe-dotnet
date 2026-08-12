using System.Collections.Generic;
using Xunit;
using Emet.Sentinel.Model.V1;
using Sentinel.Probe.Sdk.Filter;

namespace Sentinel.Probe.Sdk.Tests;

public class SpecMatchTests
{
    private static SpecificationFilter WithKinds(params string[] kinds) =>
        new() { EventMatch = new EventMatch { EventKinds = new List<string>(kinds) } };

    public static IEnumerable<object[]> Cases()
    {
        yield return new object[] { WithKinds(), new ProducerEvent { Kind = "anything" }, true };
        yield return new object[] { WithKinds("a", "b"), new ProducerEvent { Kind = "b" }, true };
        yield return new object[] { WithKinds("a", "b"), new ProducerEvent { Kind = "c" }, false };
        yield return new object[] { WithKinds("order"), new ProducerEvent { Kind = "order.charged" }, false };
        yield return new object[] { new SpecificationFilter(), new ProducerEvent { Kind = "x" }, true };
        yield return new object[] { null!, new ProducerEvent { Kind = "x" }, true };
        yield return new object[] { WithKinds(""), new ProducerEvent(), true };
        yield return new object[] { WithKinds("a"), new ProducerEvent(), false };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Selects(SpecificationFilter spec, ProducerEvent @event, bool want)
    {
        Assert.Equal(want, SpecMatch.Selects(spec, @event));
    }
}
