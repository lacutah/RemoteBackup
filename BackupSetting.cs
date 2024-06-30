using Microsoft.Extensions.Primitives;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RemoteSQLBackup;

public class AllBackupSettings
{
    public BackupSetting[] Targets { get; set; } = [];
}

public class BackupSetting
{
    internal int Id { get; set; }
    public string Name { get; init; } = String.Empty;
    public string Program { get; init; } = String.Empty;
    public string? ProgramParameters { get; init; }
    public TimeSpan FrequencyTimeSpan { get; init; }
    public TimeOnly TimeOfDay { get; init; } = TimeOnly.Parse("12:00 AM");
    public int KeepMostRecents { get; init; }
    public int KeepDays { get; init; }
    public int KeepWeeks { get; init; }
    public int KeepMonths { get; init; }
    public string BackupFileExtension { get; init; } = String.Empty;
    public string BackupFolder { get; init; } = String.Empty;
    public bool ZIPResults { get; init; } = false;
    public bool IsZIP { get; init; } = false;

    /// <summary>
    /// Based on its FrequencyTimeSpan, calculate the next time this backup should run.
    /// </summary>
    /// <returns></returns>
    public DateTime GetNextRunTime(DateTime currentDateTime)
    {
        var myFrequency = 
            FrequencyTimeSpan.TotalMinutes < 30 // Can't be less than 30 minutes.
            ? new TimeSpan(0, 30, 0) // So default to 30 minutes.
            : FrequencyTimeSpan.TotalDays > 365
                ? new TimeSpan(24, 0, 0) // Set to 24 hours / 1 day.
                : new TimeSpan(0, (int)FrequencyTimeSpan.TotalMinutes, 0); // Otherwise, use the FrequencyTimeSpan stripping off any seconds.
        
        var scheduleOffset = new TimeSpan(TimeOfDay.Hour, TimeOfDay.Minute, 0); // Strip any seconds off of schedule offset.
        

        if (myFrequency.TotalDays < 1) // Possibly multiple times a day, but every day is a new base to offset from - eg: 5 hours would mean 4 daily backups.
        {
            // If we're before the schedule start today
            if (currentDateTime.TimeOfDay < scheduleOffset)
            {
                var previousDay = currentDateTime.Date.Add(scheduleOffset).AddDays(-1);
                var nextScheduleTime = previousDay.AddMinutes(Math.Floor(currentDateTime.Subtract(previousDay).TotalMinutes / myFrequency.TotalMinutes) * myFrequency.TotalMinutes);
                
                // Edge case, we're exactly at the schedule time.
                if (nextScheduleTime == currentDateTime)
                    return currentDateTime;

                // Add one more frequency to get the next schedule time.
                nextScheduleTime.AddMinutes(myFrequency.TotalMinutes);

                // If the next schedule time is after the offset time for today, retun the offset time.
                if (nextScheduleTime.TimeOfDay > currentDateTime.TimeOfDay)
                    return currentDateTime.Date.Add(scheduleOffset);
                return nextScheduleTime;
            }
            else if (currentDateTime.TimeOfDay == scheduleOffset) // Edge case, current time is equal to the offset.
                return currentDateTime;
            else // It's currently after the offset time.
            {
                var todayBaseline = currentDateTime.Date.Add(scheduleOffset);
                var nextScheduletime = todayBaseline.AddMinutes(Math.Floor(currentDateTime.Subtract(todayBaseline).TotalMinutes / myFrequency.TotalMinutes) * myFrequency.TotalMinutes);
                if (nextScheduletime == currentDateTime) // Edge case, now is the next schedule time.
                    return currentDateTime;
                nextScheduletime = nextScheduletime.AddMinutes(myFrequency.TotalMinutes);
                if (nextScheduletime > todayBaseline.AddDays(1)) // If the next schedule time is after the offset time for tomorrow, return the offset time for tomorrow.
                    return todayBaseline.AddDays(1);
                return nextScheduletime;
            }
        }
        else if (myFrequency.TotalDays == 1) // Every day at offset.
        {
            if (currentDateTime <= currentDateTime.Date.Add(scheduleOffset)) 
                return currentDateTime.Date.Add(scheduleOffset);
            return currentDateTime.Date.Add(scheduleOffset).AddDays(1);
        }
        else if (myFrequency.TotalDays <= 15) // Start at begining of month
        {
            var curMonth = new DateTime(currentDateTime.Year, currentDateTime.Month, currentDateTime.Day);
            if (currentDateTime <= curMonth.Add(scheduleOffset)) // If we're before the schedule start today
                return curMonth.Add(scheduleOffset);
            
            var possibleNextScheduletime = curMonth.AddMinutes(Math.Floor(currentDateTime.Subtract(curMonth.Add(scheduleOffset)).TotalMinutes / myFrequency.TotalMinutes) * myFrequency.TotalMinutes);
            if (possibleNextScheduletime >= currentDateTime) return possibleNextScheduletime;
            possibleNextScheduletime = possibleNextScheduletime.AddMinutes(myFrequency.TotalMinutes);
            return (possibleNextScheduletime.Month != curMonth.Month) 
                ? curMonth.AddMonths(1).Add(scheduleOffset)
                : possibleNextScheduletime;

        }
        else // Start at begining of year.
        {
            var curYear = new DateTime(currentDateTime.Year, 1, 1);
            if (currentDateTime <= curYear.Add(scheduleOffset)) // If we're before the schedule start today
                return curYear.Add(scheduleOffset);

            var possibleNextScheduletime = curYear.AddMinutes(Math.Floor(currentDateTime.Subtract(curYear.Add(scheduleOffset)).TotalMinutes / myFrequency.TotalMinutes) * myFrequency.TotalMinutes);
            if (possibleNextScheduletime >= currentDateTime) return possibleNextScheduletime;
            possibleNextScheduletime = possibleNextScheduletime.AddMinutes(myFrequency.TotalMinutes);
            return (possibleNextScheduletime.Year != curYear.Year)
                ? curYear.AddYears(1).Add(scheduleOffset)
                : possibleNextScheduletime;
        }
    }
}
