using System.Globalization;
using System.Text.Json;

namespace HistoricalData.Config;

public sealed class SessionConfig
{
    public string TimeZoneId { get; set; } = "UTC";
    public List<SessionRule> Sessions { get; set; } = new();
    public List<string> Holidays { get; set; } = new();

    public static SessionConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            return new SessionConfig();
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<SessionConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? new SessionConfig();
    }

    public sealed class SessionRule
    {
        public string Day { get; set; } = "Monday";
        public string Start { get; set; } = "00:00";
        public string End { get; set; } = "24:00";
    }

    public sealed class SessionCalendar
    {
        private readonly TimeZoneInfo _timeZone;
        private readonly List<SessionWindow> _windows;
        private readonly HashSet<DateOnly> _holidays;

        public SessionCalendar(SessionConfig config)
        {
            _timeZone = ResolveTimeZone(config.TimeZoneId);
            _windows = BuildWindows(config.Sessions);
            _holidays = BuildHolidays(config.Holidays);
        }

        public bool IsOpen(DateTimeOffset time)
        {
            if (_windows.Count == 0)
            {
                return true;
            }

            var localTime = TimeZoneInfo.ConvertTime(time, _timeZone);
            var date = DateOnly.FromDateTime(localTime.DateTime);
            if (_holidays.Contains(date))
            {
                return false;
            }

            var day = localTime.DayOfWeek;
            var timeOfDay = TimeOnly.FromDateTime(localTime.DateTime);

            foreach (var window in _windows)
            {
                if (window.Day != day)
                {
                    continue;
                }

                if (window.IsOpen(timeOfDay))
                {
                    return true;
                }
            }

            return false;
        }

        private static TimeZoneInfo ResolveTimeZone(string timeZoneId)
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            }
            catch
            {
                return TimeZoneInfo.Utc;
            }
        }

        private static List<SessionWindow> BuildWindows(IEnumerable<SessionRule> sessions)
        {
            var windows = new List<SessionWindow>();
            foreach (var session in sessions)
            {
                if (!Enum.TryParse<DayOfWeek>(session.Day, true, out var day))
                {
                    continue;
                }

                var start = ParseTime(session.Start);
                var end = ParseTime(session.End);
                windows.Add(new SessionWindow(day, start, end));
            }

            return windows;
        }

        private static HashSet<DateOnly> BuildHolidays(IEnumerable<string> holidays)
        {
            var set = new HashSet<DateOnly>();
            foreach (var holiday in holidays)
            {
                if (DateOnly.TryParse(holiday, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                {
                    set.Add(date);
                }
            }

            return set;
        }

        private static TimeOnly ParseTime(string value)
        {
            if (string.Equals(value, "24:00", StringComparison.Ordinal))
            {
                return TimeOnly.MaxValue;
            }

            return TimeOnly.Parse(value, CultureInfo.InvariantCulture);
        }

        private sealed record SessionWindow(DayOfWeek Day, TimeOnly Start, TimeOnly End)
        {
            public bool IsOpen(TimeOnly time)
            {
                if (End == Start)
                {
                    return true;
                }

                if (End > Start)
                {
                    return time >= Start && time < End;
                }

                return time >= Start || time < End;
            }
        }
    }
}
