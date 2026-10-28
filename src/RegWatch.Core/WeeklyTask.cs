using System.Security;

namespace RegWatch.Core;

/// <summary>
/// The Task Scheduler definition behind Schedule weekly. It is written as XML because the
/// schtasks command line cannot set "run as soon as possible after a missed start".
/// </summary>
public static class WeeklyTask
{
    public const string Name = "RegWatch weekly digest";

    public static string Xml(string runnerPath, string root, DateTime firstRun) => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Description>Weekly RegWatch digest of new IRS and Treasury guidance.</Description>
          </RegistrationInfo>
          <Triggers>
            <CalendarTrigger>
              <StartBoundary>{firstRun:yyyy-MM-dd'T'HH:mm:ss}</StartBoundary>
              <Enabled>true</Enabled>
              <ScheduleByWeek>
                <DaysOfWeek><Monday /></DaysOfWeek>
                <WeeksInterval>1</WeeksInterval>
              </ScheduleByWeek>
            </CalendarTrigger>
          </Triggers>
          <Principals>
            <Principal id="Author">
              <LogonType>InteractiveToken</LogonType>
              <RunLevel>LeastPrivilege</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <StartWhenAvailable>true</StartWhenAvailable>
            <RunOnlyIfNetworkAvailable>true</RunOnlyIfNetworkAvailable>
            <ExecutionTimeLimit>PT2H</ExecutionTimeLimit>
            <Enabled>true</Enabled>
          </Settings>
          <Actions Context="Author">
            <Exec>
              <Command>{SecurityElement.Escape(runnerPath)}</Command>
              <Arguments>{SecurityElement.Escape($"--root \"{root}\"")}</Arguments>
            </Exec>
          </Actions>
        </Task>
        """;

    /// <summary>The next Monday at 19:00 (today, if it is Monday before 19:00).</summary>
    public static DateTime NextMondayEvening(DateTime now)
    {
        var days = ((int)DayOfWeek.Monday - (int)now.DayOfWeek + 7) % 7;
        var candidate = now.Date.AddDays(days).AddHours(19);
        return candidate > now ? candidate : candidate.AddDays(7);
    }
}
