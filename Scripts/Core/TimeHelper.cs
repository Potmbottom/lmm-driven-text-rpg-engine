using System;

public static class TimeHelper
{
    // Changed minutesToAdd to secondsToAdd (long)
    public static string AddSeconds(string timeStr, long secondsToAdd)
    {
        long totalSeconds = ParseToSeconds(timeStr) + secondsToAdd;

        // Prevent negative time
        if (totalSeconds < 0) totalSeconds = 0;

        // Convert back to "day X, HH:MM:SS"
        long day = totalSeconds / (24 * 3600);
        long remainder = totalSeconds % (24 * 3600);
        long hour = remainder / 3600;
        remainder %= 3600;
        long minute = remainder / 60;
        long second = remainder % 60;

        return $"day {day}, {hour:D2}:{minute:D2}:{second:D2}";
    }

    public static string SubtractSeconds(string timeStr, long secondsToSubtract)
    {
        return AddSeconds(timeStr, -secondsToSubtract);
    }

    /// <summary>
    /// Subtracts one formatted time string from another.
    /// Example: SubtractTime("day 1, 12:00:00", "day 1, 06:00:00") -> "day 0, 06:00:00"
    /// </summary>
    public static string SubtractTime(string baseTime, string timeToSubtract)
    {
        long seconds = ParseToSeconds(timeToSubtract);
        return AddSeconds(baseTime, -seconds);
    }

    /// <summary>
    /// Adds a duration string to a base time.
    /// Supports both "D MM:SS" format and standard "day X, HH:MM:SS" format.
    /// </summary>
    public static string AddDuration(string baseTime, string durationStr)
    {
        long secondsToAdd = ParseDurationStr(durationStr);
        return AddSeconds(baseTime, secondsToAdd);
    }

    /// <summary>
    /// Subtracts a duration string from a base time.
    /// Supports both "D MM:SS" format and standard "day X, HH:MM:SS" format.
    /// </summary>
    public static string SubtractDuration(string baseTime, string durationStr)
    {
        long secondsToSubtract = ParseDurationStr(durationStr);
        return AddSeconds(baseTime, -secondsToSubtract);
    }

    // Helper to parse duration. Tries "D MM:SS" first, falls back to ParseToSeconds.
    private static long ParseDurationStr(string durationStr)
    {
        if (string.IsNullOrWhiteSpace(durationStr)) return 0;

        long totalSeconds = 0;
        bool durationParseSuccess = false;

        try
        {
            // 1. Try to parse as specific Duration format: "D MM:SS" (e.g., "0 10:00" or "1 00:00")
            // This format assumes space separator between Days and Time
            var parts = durationStr.Split(' ');
            
            if (parts.Length >= 2 && parts[1].Contains(":"))
            {
                // Parse Days
                if (long.TryParse(parts[0], out long days))
                {
                    totalSeconds += days * 24 * 3600;
                    
                    var timeParts = parts[1].Split(':');
                    // Parse Minutes
                    if (timeParts.Length > 0 && int.TryParse(timeParts[0], out int minutes))
                    {
                        totalSeconds += minutes * 60;
                    }
                    // Parse Seconds
                    if (timeParts.Length > 1 && int.TryParse(timeParts[1], out int seconds))
                    {
                        totalSeconds += seconds;
                    }
                    durationParseSuccess = true;
                }
            }
        }
        catch { }

        // 2. If "D MM:SS" parsing didn't yield a valid result or failed pattern matching, 
        // try standard time parsing (e.g. "day 1, 06:19:00")
        if (!durationParseSuccess || totalSeconds == 0)
        {
            // Avoid double parsing if input was literally "0 00:00"
            if (durationStr.Trim() != "0 00:00") 
            {
                long standardParse = ParseToSeconds(durationStr);
                if (standardParse > 0) return standardParse;
            }
        }

        return totalSeconds;
    }

    // Parses "day X, HH:MM", "day X, HH:MM:SS" or "HH:MM:SS" into total seconds
    public static long ParseToSeconds(string timeStr)
    {
        if (string.IsNullOrWhiteSpace(timeStr)) return 0;

        timeStr = timeStr.ToLower().Trim();
        long days = 0;
        int hours = 0;
        int minutes = 0;
        int seconds = 0;

        try
        {
            // Format: "day 5, 12:30" or "day 5, 12:30:45"
            if (timeStr.Contains("day"))
            {
                var parts = timeStr.Split(',');
                var dayPart = parts[0].Replace("day", "").Trim();
                if (long.TryParse(dayPart, out long d)) days = d;

                if (parts.Length > 1)
                {
                    var timeParts = parts[1].Trim().Split(':');
                    if (timeParts.Length > 0) int.TryParse(timeParts[0], out hours);
                    if (timeParts.Length > 1) int.TryParse(timeParts[1], out minutes);
                    if (timeParts.Length > 2) int.TryParse(timeParts[2], out seconds);
                }
            }
            // Format: "12:30" or "12:30:45"
            else if (timeStr.Contains(":"))
            {
                var parts = timeStr.Split(':');
                if (parts.Length > 0) int.TryParse(parts[0], out hours);
                if (parts.Length > 1) int.TryParse(parts[1], out minutes);
                if (parts.Length > 2) int.TryParse(parts[2], out seconds);
            }
        }
        catch
        {
            return 0;
        }

        return (days * 24 * 3600) + (hours * 3600) + (minutes * 60) + seconds;
    }

    public static int Compare(string t1, string t2)
    {
        long s1 = ParseToSeconds(t1);
        long s2 = ParseToSeconds(t2);
        return s1.CompareTo(s2);
    }

    public static string GetMin(string t1, string t2) => Compare(t1, t2) < 0 ? t1 : t2;
    public static string GetMax(string t1, string t2) => Compare(t1, t2) > 0 ? t1 : t2;
}