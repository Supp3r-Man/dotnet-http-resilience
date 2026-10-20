using System.Net;
using System.Net.Http.Headers;

namespace HttpResilience.Tests;

public class RetryAfterParserTests
{
    private static readonly DateTimeOffset Now = new(2015, 10, 21, 7, 27, 30, TimeSpan.Zero);
    private static readonly TimeSpan Max = TimeSpan.FromSeconds(30);

    private static bool Parse(string? text, out TimeSpan delay) => RetryAfterParser.TryParse(text, Now, Max, out delay);

    [Theory]
    [InlineData("0", 0)]
    [InlineData("1", 1)]
    [InlineData("5", 5)]
    [InlineData(" 7 ", 7)]
    [InlineData("29", 29)]
    public void Seconds_are_parsed(string text, int expectedSeconds)
    {
        Assert.True(Parse(text, out var delay));
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), delay);
    }

    [Fact]
    public void Seconds_equal_to_the_cap_return_the_cap()
    {
        Assert.True(Parse("30", out var delay));
        Assert.Equal(Max, delay);
    }

    [Theory]
    [InlineData("31")]
    [InlineData("3600")]
    [InlineData("9223372036854775807")]
    [InlineData("99999999999999999999999999")]
    public void Huge_seconds_are_capped(string text)
    {
        Assert.True(Parse(text, out var delay));
        Assert.Equal(Max, delay);
    }

    [Theory]
    [InlineData("-5")]
    [InlineData("-0")]
    [InlineData("+5")]
    [InlineData("1.5")]
    [InlineData("5s")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0x10")]
    [InlineData("tomorrow")]
    [InlineData("2015-10-21T07:28:00Z")]
    public void Negative_and_garbage_values_are_ignored(string text)
    {
        Assert.False(Parse(text, out var delay));
        Assert.Equal(TimeSpan.Zero, delay);
    }

    [Fact]
    public void Null_is_ignored()
    {
        Assert.False(Parse(null, out _));
    }

    [Fact]
    public void Future_http_date_is_the_difference_to_now()
    {
        Assert.True(Parse("Wed, 21 Oct 2015 07:28:00 GMT", out var delay));
        Assert.Equal(TimeSpan.FromSeconds(30), delay);
        Assert.True(Parse("Wed, 21 Oct 2015 07:27:40 GMT", out var ten));
        Assert.Equal(TimeSpan.FromSeconds(10), ten);
    }

    [Fact]
    public void Http_date_in_the_past_is_zero()
    {
        Assert.True(Parse("Wed, 21 Oct 2015 07:00:00 GMT", out var delay));
        Assert.Equal(TimeSpan.Zero, delay);
    }

    [Fact]
    public void Http_date_equal_to_now_is_zero()
    {
        Assert.True(Parse("Wed, 21 Oct 2015 07:27:30 GMT", out var delay));
        Assert.Equal(TimeSpan.Zero, delay);
    }

    [Fact]
    public void Http_date_far_in_the_future_is_capped()
    {
        Assert.True(Parse("Fri, 21 Oct 2050 07:28:00 GMT", out var delay));
        Assert.Equal(Max, delay);
    }

    [Fact]
    public void Header_on_a_response_is_read_as_seconds()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(3));
        Assert.True(RetryAfterParser.TryGetDelay(response, Now, Max, out var delay));
        Assert.Equal(TimeSpan.FromSeconds(3), delay);
    }

    [Fact]
    public void Header_on_a_response_is_read_as_a_date()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(Now.AddSeconds(12));
        Assert.True(RetryAfterParser.TryGetDelay(response, Now, Max, out var delay));
        Assert.Equal(TimeSpan.FromSeconds(12), delay);
    }

    [Fact]
    public void Invalid_raw_header_on_a_response_is_ignored()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        response.Headers.TryAddWithoutValidation("Retry-After", "soon");
        Assert.False(RetryAfterParser.TryGetDelay(response, Now, Max, out _));
    }

    [Fact]
    public void Negative_raw_header_on_a_response_is_ignored()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        response.Headers.TryAddWithoutValidation("Retry-After", "-10");
        Assert.False(RetryAfterParser.TryGetDelay(response, Now, Max, out _));
    }

    [Fact]
    public void Missing_header_is_absent()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        Assert.False(RetryAfterParser.TryGetDelay(response, Now, Max, out _));
    }

    [Fact]
    public void Zero_cap_turns_everything_into_zero()
    {
        Assert.True(RetryAfterParser.TryParse("10", Now, TimeSpan.Zero, out var delay));
        Assert.Equal(TimeSpan.Zero, delay);
    }
}
