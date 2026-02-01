public static class TimeHelper
{
    public static string AddMinutes(string timeStr, int minutesToAdd)
    {
        long totalMinutes = ParseToMinutes(timeStr) + minutesToAdd;

        // Обратное преобразование в формат "day X, HH:MM"
        long day = totalMinutes / (24 * 60);
        long remainder = totalMinutes % (24 * 60);
        long hour = remainder / 60;
        long minute = remainder % 60;

        return $"day {day}, {hour:D2}:{minute:D2}";
    }

    // Парсит время формата "day X, HH:MM" или "HH:MM:SS" в минуты для сравнения
    public static long ParseToMinutes(string timeStr)
    {
        if (string.IsNullOrWhiteSpace(timeStr)) return 0;

        timeStr = timeStr.ToLower().Trim();
        long days = 0;
        int hours = 0;
        int minutes = 0;

        try
        {
            // Формат: "day 5, 12:30"
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
                }
            }
            // Формат: "12:30" или "12:30:00"
            else if (timeStr.Contains(":"))
            {
                var parts = timeStr.Split(':');
                if (parts.Length > 0) int.TryParse(parts[0], out hours);
                if (parts.Length > 1) int.TryParse(parts[1], out minutes);
                // Секунды игнорируем для глобального времени, если не критично
            }
        }
        catch
        {
            return 0;
        }

        return (days * 24 * 60) + (hours * 60) + minutes;
    }

    public static int Compare(string t1, string t2)
    {
        long m1 = ParseToMinutes(t1);
        long m2 = ParseToMinutes(t2);
        return m1.CompareTo(m2);
    }

    public static string GetMin(string t1, string t2) => Compare(t1, t2) < 0 ? t1 : t2;
    public static string GetMax(string t1, string t2) => Compare(t1, t2) > 0 ? t1 : t2;
}