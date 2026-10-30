namespace Common;

/// <summary>Holds the three sign counts produced by a task run.</summary>
public class CounterResult
{
    public long Pos;
    public long Neg;
    public long Zero;

    public long Total => Pos + Neg + Zero;

    public bool IsCorrect(long expected) => Total == expected;

    public override string ToString() =>
        $"pos={Pos:N0}  neg={Neg:N0}  zero={Zero:N0}  total={Total:N0}";
}
