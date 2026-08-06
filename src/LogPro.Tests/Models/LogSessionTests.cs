using LogPro.Models;

namespace LogPro.Tests.Models;

public class LogSessionTests
{
    [Fact]
    public void LogSession_DefaultValues_AreSetCorrectly()
    {
        var session = new LogSession();

        session.Id.Should().HaveLength(8);
        session.Name.Should().BeEmpty();
        session.StartTime.Should().BeCloseTo(DateTime.Now, TimeSpan.FromSeconds(5));
        session.EndTime.Should().BeNull();
        session.DeviceId.Should().BeEmpty();
        session.DeviceName.Should().BeEmpty();
        session.Platform.Should().Be(LogPro.Models.DevicePlatform.Android);
        session.LogFilePath.Should().BeEmpty();
        session.AppLogFilePath.Should().BeEmpty();
        session.SessionDirectory.Should().BeEmpty();
        session.Status.Should().Be(SessionStatus.Idle);
        session.LogLineCount.Should().Be(0);
    }

    [Fact]
    public void DurationText_WhenLessThanOneHour_ReturnsMinutesAndSeconds()
    {
        var session = new LogSession { StartTime = DateTime.Now.AddMinutes(-5) };

        session.DurationText.Should().Contain("m");
    }

    [Fact]
    public void DurationText_WhenMoreThanOneHour_ReturnsHoursAndMinutes()
    {
        var session = new LogSession { StartTime = DateTime.Now.AddHours(-2).AddMinutes(-30) };

        session.DurationText.Should().Contain("h");
    }

    [Theory]
    [InlineData(SessionStatus.Capturing, "[REC]")]
    [InlineData(SessionStatus.Stopped, "[STOP]")]
    [InlineData(SessionStatus.Idle, "[IDLE]")]
    public void StatusIcon_ReturnsExpectedIcon(SessionStatus status, string expectedIcon)
    {
        var session = new LogSession { Status = status };
        session.StatusIcon.Should().Be(expectedIcon);
    }

    [Fact]
    public void StatusIcon_ForUnknownStatus_ReturnsQuestionMark()
    {
        var session = new LogSession { Status = (SessionStatus)99 };
        session.StatusIcon.Should().Be("[?]");
    }
}

public class SessionStatusTests
{
    [Fact]
    public void SessionStatus_HasAllExpectedMembers()
    {
        var values = Enum.GetValues<SessionStatus>();
        values.Should().Contain(SessionStatus.Idle);
        values.Should().Contain(SessionStatus.Capturing);
        values.Should().Contain(SessionStatus.Stopped);
    }
}