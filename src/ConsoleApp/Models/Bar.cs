namespace HistoricalData.Models;

public sealed record Bar(
    DateTimeOffset Time,
    double Open,
    double High,
    double Low,
    double Close,
    long Volume,
    int Spread,
    long RealVolume
);
