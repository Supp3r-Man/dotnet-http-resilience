using System.Globalization;

namespace HttpResilience;

/// <summary>
/// Parses the Retry-After header. Accepted: non-negative integer seconds, or an IMF-fixdate HTTP-date
/// ("Wed, 21 Oct 2015 07:28:00 GMT"). Anything else (negative, garbage, empty) is treated as absent.
/// Results are capped at the supplied maximum, including values too large for a long. A date in the past
/// yields <see cref="TimeSpan.Zero"/>.
/// </summary>
public static class RetryAfterParser
{
    public static bool TryGetDelay(HttpResponseMessage response, DateTimeOffset utcNow, TimeSpan maxDelay, out TimeSpan delay)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.Headers.NonValidated.TryGetValues("Retry-After", out var values))
        {
            foreach (var value in values)
            {
                return TryParse(value, utcNow, maxDelay, out delay);
            }
        }

        delay = default;
        return false;
    }

    public static bool TryParse(string? value, DateTimeOffset utcNow, TimeSpan maxDelay, out TimeSpan delay)
    {
        delay = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        if (text.All(char.IsAsciiDigit))
        {
            if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
                || seconds >= maxDelay.TotalSeconds)
            {
                delay = maxDelay;
                return true;
            }

            delay = TimeSpan.FromSeconds(seconds);
            return true;
        }

        if (DateTimeOffset.TryParseExact(text, "r", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
        {
            var remaining = date - utcNow;
            if (remaining <= TimeSpan.Zero)
            {
                delay = TimeSpan.Zero;
            }
            else
            {
                delay = remaining > maxDelay ? maxDelay : remaining;
            }

            return true;
        }

        return false;
    }
}
