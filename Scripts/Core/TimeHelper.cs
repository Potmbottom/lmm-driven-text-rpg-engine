using System;

public static class TimeHelper
{
    public static string AddSeconds(string timeStr, long secondsToAdd)
    {
        var totalSeconds = ParseToSeconds(timeStr) + secondsToAdd;
        if (totalSeconds < 0) totalSeconds = 0;
        var day = totalSeconds / (24 * 3600);
        var remainder = totalSeconds % (24 * 3600);
        var hour = remainder / 3600;
        remainder %= 3600;
        var minute = remainder / 60;
        var second = remainder % 60;

        return $"day {day}, {hour:D2}:{minute:D2}:{second:D2}";
    }

    public static string SubtractSeconds(string timeStr, long secondsToSubtract)
    {
        return AddSeconds(timeStr, -secondsToSubtract);
    }
    
    public static string SubtractTime(string baseTime, string timeToSubtract)
    {
        var seconds = ParseToSeconds(timeToSubtract);
        return AddSeconds(baseTime, -seconds);
    }
    
    public static string AddDuration(string baseTime, string durationStr)
    {
        var secondsToAdd = ParseDurationStr(durationStr);
        return AddSeconds(baseTime, secondsToAdd);
    }
    
    public static string SubtractDuration(string baseTime, string durationStr)
    {
        var secondsToSubtract = ParseDurationStr(durationStr);
        return AddSeconds(baseTime, -secondsToSubtract);
    }
    
    private static long ParseDurationStr(string durationStr)
    {
        if (string.IsNullOrWhiteSpace(durationStr)) return 0;

        long totalSeconds = 0;
        var durationParseSuccess = false;

        try
        {
            var parts = durationStr.Split(' ');
            
            if (parts.Length >= 2 && parts[1].Contains(":"))
            {
                if (long.TryParse(parts[0], out var days))
                {
                    totalSeconds += days * 24 * 3600;
                    
                    var timeParts = parts[1].Split(':');
                    if (timeParts.Length > 0 && int.TryParse(timeParts[0], out var minutes))
                    {
                        totalSeconds += minutes * 60;
                    }
                    if (timeParts.Length > 1 && int.TryParse(timeParts[1], out var seconds))
                    {
                        totalSeconds += seconds;
                    }
                    durationParseSuccess = true;
                }
            }
        }
        catch { }
        if (!durationParseSuccess || totalSeconds == 0)
        {
            if (durationStr.Trim() != "0 00:00") 
            {
                var standardParse = ParseToSeconds(durationStr);
                if (standardParse > 0) return standardParse;
            }
        }

        return totalSeconds;
    }
    
    public static long ParseToSeconds(string timeStr)
    {
        if (string.IsNullOrWhiteSpace(timeStr)) return 0;

        timeStr = timeStr.ToLower().Trim();
        long days = 0;
        var hours = 0;
        var minutes = 0;
        var seconds = 0;

        try
        {
            if (timeStr.Contains("day"))
            {
                var parts = timeStr.Split(',');
                var dayPart = parts[0].Replace("day", "").Trim();
                if (long.TryParse(dayPart, out var d)) days = d;

                if (parts.Length > 1)
                {
                    var timeParts = parts[1].Trim().Split(':');
                    if (timeParts.Length > 0) int.TryParse(timeParts[0], out hours);
                    if (timeParts.Length > 1) int.TryParse(timeParts[1], out minutes);
                    if (timeParts.Length > 2) int.TryParse(timeParts[2], out seconds);
                }
            }
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
        var s1 = ParseToSeconds(t1);
        var s2 = ParseToSeconds(t2);
        return s1.CompareTo(s2);
    }

    public static string GetMin(string t1, string t2) => Compare(t1, t2) < 0 ? t1 : t2;
    public static string GetMax(string t1, string t2) => Compare(t1, t2) > 0 ? t1 : t2;
}