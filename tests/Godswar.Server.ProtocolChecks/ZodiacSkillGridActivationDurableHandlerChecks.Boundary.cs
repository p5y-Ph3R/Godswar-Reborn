using Godswar.Server.Application.Zodiac;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    ZodiacSkillGridActivationDurableHandlerChecks
{
    private static async Task CheckReservedValuesFailClosedAsync()
    {
        var cases = new[]
        {
            (Value2: 0, Value3: 0, Name: "reserved Value2"),
            (Value2: -1, Value3: 1, Name: "reserved Value3")
        };

        foreach (var testCase in cases)
        {
            var executor = new CapturingExecutor(
                _ => throw new InvalidOperationException(
                    "noncanonical activation reached executor"));
            await using var fixture = CreateFixture(executor);
            var before = CaptureUnrelatedState(fixture.Character);
            var goldBefore = fixture.Character.Gold;
            var gridBefore =
                fixture.Character.ZodiacSkillGridLevels[GridIndex];

            await InvokeAsync(
                fixture.Handler,
                CreateActivationPacket(
                    value2: testCase.Value2,
                    value3: testCase.Value3));

            Check.Equal(
                0,
                executor.Count,
                $"{testCase.Name} is rejected before executor");
            Check.Equal(
                0,
                fixture.Store.ActivationCount,
                $"{testCase.Name} cannot reach compatibility storage");
            AssertResponseShape(
                ReadPackets(fixture.Transport),
                expectedActivationCount: 0,
                expectedStatusCount: 0,
                testCase.Name);
            Check.Equal(
                goldBefore,
                fixture.Character.Gold,
                $"{testCase.Name} preserves Gold");
            Check.Equal(
                gridBefore,
                fixture.Character.ZodiacSkillGridLevels[GridIndex],
                $"{testCase.Name} preserves grid");
            AssertUnrelatedStatePreserved(
                fixture.Character,
                before,
                testCase.Name);
        }
    }
}
