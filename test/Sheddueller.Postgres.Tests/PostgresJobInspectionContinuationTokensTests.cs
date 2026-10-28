namespace Sheddueller.Postgres.Tests;

using Sheddueller.Postgres.Internal.Operations;

using Shouldly;

public sealed class PostgresJobInspectionContinuationTokensTests
{
    [Fact]
    public void NewestToken_RoundTrip_ReturnsEnqueueSequence()
    {
        var token = PostgresJobInspectionContinuationTokens.EncodeNewest(42);

        token.ShouldBe("42");
        PostgresJobInspectionContinuationTokens.DecodeNewest(token).ShouldBe(42);
    }

    [Fact]
    public void NewestToken_Null_StartsAtFirstPage()
      => PostgresJobInspectionContinuationTokens.DecodeNewest(null).ShouldBeNull();

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("op:5")]
    [InlineData("abc")]
    [InlineData("")]
    public void NewestToken_NotAPositiveSequence_Throws(string token)
      => Should.Throw<ArgumentException>(() => PostgresJobInspectionContinuationTokens.DecodeNewest(token));

    [Fact]
    public void OperationalToken_RoundTrip_ReturnsOffset()
    {
        var token = PostgresJobInspectionContinuationTokens.EncodeOperational(40);

        token.ShouldBe("op:40");
        PostgresJobInspectionContinuationTokens.DecodeOperational(token).ShouldBe(40);
    }

    [Fact]
    public void OperationalToken_BareNumber_ReadsAsOffset()
      => PostgresJobInspectionContinuationTokens.DecodeOperational("15").ShouldBe(15);

    [Fact]
    public void OperationalToken_Null_StartsAtZero()
      => PostgresJobInspectionContinuationTokens.DecodeOperational(null).ShouldBe(0);

    [Theory]
    [InlineData("op:-1")]
    [InlineData("op:")]
    [InlineData("op:x")]
    [InlineData("-3")]
    public void OperationalToken_NotANonNegativeOffset_Throws(string token)
      => Should.Throw<ArgumentException>(() => PostgresJobInspectionContinuationTokens.DecodeOperational(token));
}
