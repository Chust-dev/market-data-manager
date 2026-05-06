namespace HistoricalData.Models;

public readonly record struct Tick(DateTimeOffset Time, double Bid, double Ask, float BidVolume, float AskVolume);
