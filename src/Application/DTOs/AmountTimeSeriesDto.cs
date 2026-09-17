namespace ConvivenciaPix.Application.DTOs;

/// <summary>
/// Daily evolution of monetary value flowing through the coexistence layer for the analytics
/// dashboard: total amount (transfer + withdrawal) per day, split into inbound (received) vs
/// outbound (sent) and success vs failed. Scoped to the [From, To] window (by CreatedAt).
/// </summary>
public sealed record AmountTimeSeriesDto(
    DateTime? From,
    DateTime? To,
    IReadOnlyList<AmountPointDto> Points);

/// <summary>
/// One day's summed amounts. Failed = row carries any error code (System A or B); Success = neither.
/// Each figure is the sum of <c>TransferAmount + WithdrawalAmount</c> (nulls treated as 0).
/// </summary>
public sealed record AmountPointDto(
    DateTime Day,             // UTC day start
    decimal ReceivedSuccess,
    decimal ReceivedFailed,
    decimal SentSuccess,
    decimal SentFailed);
