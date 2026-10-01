using LogPro.Services;

namespace LogPro.Tests.Services;

public class MonkeyProcessRunnerTests
{
    private static MonkeyRunOptions Valid() => new("FAKE01", "com.example.app", 1000, 123, 300,
        50, 20, 5, 10, 5, 10);

    [Fact]
    public void Validate_AcceptsValidRun() => MonkeyProcessRunner.Validate(Valid());

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void Validate_RejectsInvalidIndividualPercentages(int percentage)
    {
        var options = Valid() with
        {
            PctTouch = percentage,
            PctMotion = 100 - percentage,
            PctTrackball = 0,
            PctNav = 0,
            PctSyskeys = 0,
            PctAppswitch = 0
        };
        Action act = () => MonkeyProcessRunner.Validate(options);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Validate_RejectsInvalidTotalAndPackage()
    {
        Action invalidTotal = () => MonkeyProcessRunner.Validate(Valid() with { PctTouch = 49 });
        Action invalidPackage = () => MonkeyProcessRunner.Validate(Valid() with { Package = "com.example;rm" });
        invalidTotal.Should().Throw<ArgumentException>();
        invalidPackage.Should().Throw<ArgumentException>();
    }
}
