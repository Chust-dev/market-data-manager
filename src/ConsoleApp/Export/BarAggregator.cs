using HistoricalData.Config;
using HistoricalData.Models;

namespace HistoricalData.Export;

public sealed class BarAggregator
{
    private readonly Dictionary<long, BarBuilder> _bars = new();
    private readonly Dictionary<long, HashSet<TickKey>> _tickKeys = new();
    private readonly HashSet<long> _minutesWithTicks = new();
    private readonly HashSet<long> _minutesWithFallbackBars = new();
    private readonly int _digits;
    private readonly double _scale;
    private readonly BrokerOffset _broker;
    private readonly bool _filterWeekends;
    private readonly bool _deduplicateTicks;
    private readonly bool _skipFallbackIfTicked;
    private readonly SessionConfig.SessionCalendar? _sessionCalendar;
    private readonly DateTimeOffset _startServer;
    private readonly DateTimeOffset _endServer;

    public BarAggregator(
        string timeframe,
        int digits,
        BrokerOffset broker,
        bool filterWeekends,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        bool deduplicateTicks = true,
        bool skipFallbackIfTicked = true,
        SessionConfig.SessionCalendar? sessionCalendar = null)
    {
        Timeframe = timeframe;
        _digits = digits;
        _scale = Math.Pow(10, digits);
        _broker = broker;
        _filterWeekends = filterWeekends;
        _deduplicateTicks = deduplicateTicks;
        _skipFallbackIfTicked = skipFallbackIfTicked;
        _sessionCalendar = sessionCalendar;
        _startServer = startUtc.ToOffset(broker.At(startUtc));
        _endServer = endUtc.ToOffset(broker.At(endUtc));
    }

    public string Timeframe { get; }
    public long DuplicateTicksDropped { get; private set; }
    public long FallbackBarsSkipped { get; private set; }

    public void AddTick(Tick tick)
    {
        var serverTime = tick.Time.ToOffset(_broker.At(tick.Time));
        if (!IsInRange(serverTime))
        {
            return;
        }

        if (_sessionCalendar is not null && !_sessionCalendar.IsOpen(serverTime))
        {
            return;
        }

        if (_filterWeekends && IsWeekend(serverTime))
        {
            return;
        }

        var minuteKey = GetMinuteKey(serverTime);
        if (_deduplicateTicks)
        {
            var key = new TickKey(tick, _scale);
            if (!_tickKeys.TryGetValue(minuteKey, out var keys))
            {
                keys = new HashSet<TickKey>();
                _tickKeys[minuteKey] = keys;
            }

            if (!keys.Add(key))
            {
                DuplicateTicksDropped++;
                return;
            }
        }

        _minutesWithTicks.Add(minuteKey);
        if (_skipFallbackIfTicked && _minutesWithFallbackBars.Contains(minuteKey))
        {
            _bars.Remove(minuteKey);
            _minutesWithFallbackBars.Remove(minuteKey);
        }

        if (!_bars.TryGetValue(minuteKey, out var builder))
        {
            var minuteUtc = DateTimeOffset.FromUnixTimeSeconds(minuteKey * 60);
            var minuteTime = minuteUtc.ToOffset(_broker.At(minuteUtc));
            builder = new BarBuilder(minuteTime, _scale);
            _bars[minuteKey] = builder;
        }

        builder.AddTick(tick, serverTime);
    }

    public void AddBar(Bar bar)
    {
        TryAddFallbackBar(bar, onlyIfMissing: false);
    }

    public bool TryAddFallbackBar(Bar bar, bool onlyIfMissing)
    {
        var serverTime = bar.Time.ToOffset(_broker.At(bar.Time));
        if (!IsInRange(serverTime))
        {
            return false;
        }

        if (_sessionCalendar is not null && !_sessionCalendar.IsOpen(serverTime))
        {
            return false;
        }

        if (_filterWeekends && IsWeekend(serverTime))
        {
            return false;
        }

        var minuteKey = GetMinuteKey(serverTime);
        if (onlyIfMissing && _bars.ContainsKey(minuteKey))
        {
            return false;
        }

        if (_skipFallbackIfTicked && _minutesWithTicks.Contains(minuteKey))
        {
            FallbackBarsSkipped++;
            return false;
        }

        if (!_bars.TryGetValue(minuteKey, out var builder))
        {
            var minuteUtc = DateTimeOffset.FromUnixTimeSeconds(minuteKey * 60);
            var minuteTime = minuteUtc.ToOffset(_broker.At(minuteUtc));
            builder = new BarBuilder(minuteTime, _scale);
            _bars[minuteKey] = builder;
        }

        builder.MergeBar(bar);
        if (!_minutesWithTicks.Contains(minuteKey))
        {
            _minutesWithFallbackBars.Add(minuteKey);
        }

        return true;
    }

    public IReadOnlyList<Bar> GetBars()
    {
        return _bars.Values
            .Select(b => b.Build(_digits))
            .Where(b => b.Time >= _startServer && b.Time <= _endServer)
            .OrderBy(b => b.Time)
            .ToList();
    }

    private bool IsWeekend(DateTimeOffset time)
    {
        return time.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
    }

    private bool IsInRange(DateTimeOffset time)
    {
        return time >= _startServer && time <= _endServer;
    }

    private static long GetMinuteKey(DateTimeOffset time)
    {
        return time.ToUnixTimeSeconds() / 60;
    }

    private readonly struct TickKey : IEquatable<TickKey>
    {
        private readonly long _timestampMs;
        private readonly long _bid;
        private readonly long _ask;
        private readonly int _bidVolBits;
        private readonly int _askVolBits;

        public TickKey(Tick tick, double scale)
        {
            _timestampMs = tick.Time.ToUnixTimeMilliseconds();
            _bid = (long)Math.Round(tick.Bid * scale);
            _ask = (long)Math.Round(tick.Ask * scale);
            _bidVolBits = BitConverter.SingleToInt32Bits(tick.BidVolume);
            _askVolBits = BitConverter.SingleToInt32Bits(tick.AskVolume);
        }

        public bool Equals(TickKey other)
        {
            return _timestampMs == other._timestampMs
                   && _bid == other._bid
                   && _ask == other._ask
                   && _bidVolBits == other._bidVolBits
                   && _askVolBits == other._askVolBits;
        }

        public override bool Equals(object? obj)
        {
            return obj is TickKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(_timestampMs, _bid, _ask, _bidVolBits, _askVolBits);
        }
    }

    private sealed class BarBuilder
    {
        private readonly DateTimeOffset _time;
        private readonly double _scale;
        private bool _hasValue;
        private double _open;
        private double _high;
        private double _low;
        private double _close;
        private long _tickVolume;
        private long _realVolume;
        private int _spread;
        private double _lastBid;
        private double _lastAsk;

        public BarBuilder(DateTimeOffset time, double scale)
        {
            _time = time;
            _scale = scale;
        }

        public void AddTick(Tick tick, DateTimeOffset serverTime)
        {
            var price = tick.Bid;
            if (!_hasValue)
            {
                _open = price;
                _high = price;
                _low = price;
                _close = price;
                _hasValue = true;
            }
            else
            {
                _high = Math.Max(_high, price);
                _low = Math.Min(_low, price);
                _close = price;
            }

            _lastBid = tick.Bid;
            _lastAsk = tick.Ask;
            _tickVolume++;
            _realVolume += (long)Math.Round(tick.BidVolume + tick.AskVolume);
            _spread = (int)Math.Round((_lastAsk - _lastBid) * _scale);
        }

        public void MergeBar(Bar bar)
        {
            if (!_hasValue)
            {
                _open = bar.Open;
                _high = bar.High;
                _low = bar.Low;
                _close = bar.Close;
                _hasValue = true;
            }
            else
            {
                _high = Math.Max(_high, bar.High);
                _low = Math.Min(_low, bar.Low);
                _close = bar.Close;
            }

            _tickVolume += bar.Volume;
            _realVolume += bar.RealVolume;
            _spread = Math.Max(_spread, bar.Spread);
        }

        public Bar Build(int digits)
        {
            return new Bar(
                _time,
                Math.Round(_open, digits),
                Math.Round(_high, digits),
                Math.Round(_low, digits),
                Math.Round(_close, digits),
                _tickVolume,
                _spread,
                _realVolume
            );
        }
    }
}
