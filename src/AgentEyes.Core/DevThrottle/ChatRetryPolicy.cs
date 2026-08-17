using System;
using System.Globalization;

namespace AgentEyes.DevThrottle
{
    /// <summary>
    /// The retry decision for a chat-completions call, kept OUT of the transport so it is unit-testable
    /// without a network call (issue #140).
    ///
    /// Why it exists: on 2026-08-11 a 36-minute recording finished with a full transcript but kept its
    /// preset name because the single naming call came back
    /// <c>429 "Model busy, retry later"</c> and was abandoned. A 429 is by definition a "try again
    /// shortly" answer, so the call now gets a small, BOUNDED number of further attempts.
    ///
    /// Bounded is the whole point. Issue #138 fixed a 300-second packaging stall by giving chat calls
    /// their own <see cref="DevThrottleClient.ChatTimeout"/>; a retry loop must not hand that stall back.
    /// Two bounds hold the line:
    /// <list type="bullet">
    /// <item><description><see cref="MaxAttempts"/> attempts in total (1 original + 2 retries).</description></item>
    /// <item><description><see cref="RetryTailBudget"/> - the retries and their delays may extend the call
    /// past ONE attempt's own budget by at most this much, so the status worst case is 120s + 60s = 180s,
    /// still well under the 300s stall #138 removed.</description></item>
    /// </list>
    ///
    /// Issue #145 added the second transient this call actually suffers. Field evidence from
    /// 2026-08-11 showed the common failure is NOT a status at all - the upstream model simply hangs
    /// and the per-attempt budget expires, which #140 could not see because it decides on a status
    /// code. The client now stamps that stall with <see cref="LocalTimeoutStatus"/> so it is classified
    /// here like any other failed attempt. A timeout costs a WHOLE attempt budget, so it gets its own
    /// tighter attempt cap (<see cref="MaxTimeoutAttempts"/>) and its own named worst case
    /// (<see cref="TimeoutWorstCaseTotal"/>), and <see cref="WorstCaseTotal"/> is the ceiling on any
    /// mix of the two.
    /// </summary>
    internal static class ChatRetryPolicy
    {
        /// <summary>Total attempts for one chat call: the first plus <c>MaxAttempts - 1</c> retries.</summary>
        public const int MaxAttempts = 3;

        /// <summary>
        /// The status <see cref="DevThrottleClient"/> stamps on an attempt that outlived its OWN budget
        /// before the proxy answered. It is an INTERNAL convention, not a status the DevThrottle proxy
        /// returns - the client raises it locally, and callers have always seen it as
        /// <c>DevThrottleException(408)</c> (issue #145).
        /// </summary>
        public const int LocalTimeoutStatus = 408;

        /// <summary>
        /// Total attempts when the failure is the client-side timeout rather than a status. Two, not
        /// <see cref="MaxAttempts"/>, and the arithmetic is the reason: a timed-out attempt burns the
        /// WHOLE per-attempt budget, so two attempts cost 120s + 120s + 2s = 242s
        /// (<see cref="TimeoutWorstCaseTotal"/>), which still lands under the 300s packaging stall #138
        /// removed. A third would cost 366s - worse than the stall this client exists to prevent.
        /// Evidence that a second attempt is worth having at all: naming calls succeeded at 13.8s, 23.3s
        /// and 66.7s on the same account the same day the three stalls were logged, so a hang is a
        /// property of the attempt, not of the request.
        /// </summary>
        public const int MaxTimeoutAttempts = 2;

        /// <summary>
        /// The longest a single wait between attempts may be, and the clamp applied to a
        /// <c>Retry-After</c> header. A proxy asking for ten minutes does not get ten minutes - the
        /// packaging pass is waiting on this call.
        /// </summary>
        public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(10);

        /// <summary>
        /// How much wall clock the retry tail (every retry attempt plus every delay) may add on top of a
        /// single attempt's own timeout. Bounds the whole call: total &lt;= per-attempt budget + this.
        /// </summary>
        public static readonly TimeSpan RetryTailBudget = TimeSpan.FromSeconds(60);

        /// <summary>
        /// The DOCUMENTED worst-case wall clock for one fully-retried naming call whose every attempt
        /// hangs: <see cref="MaxTimeoutAttempts"/> (2) times the 120s
        /// <see cref="DevThrottleClient.ChatTimeout"/>, plus the 2s planned backoff between them = 242s.
        ///
        /// The figure is CHOSEN, not inherited. Three constraints fix it:
        /// <list type="bullet">
        /// <item><description>It must stay under the 300s packaging stall issue #138 removed. 242s does;
        /// a third attempt (366s) would not.</description></item>
        /// <item><description>Every attempt must get the FULL 120s budget. A legitimate naming call was
        /// measured at 66.7s, so truncating the second attempt to whatever a smaller total left over
        /// would start killing calls that were about to succeed - the exact mistake #138 avoided.</description></item>
        /// <item><description>It must be asserted, not assumed - see ChatRetryPolicyTests.</description></item>
        /// </list>
        /// Kept as a literal so a change to <see cref="DevThrottleClient.ChatTimeout"/> or to
        /// <see cref="MaxTimeoutAttempts"/> fails the test rather than silently moving the ceiling.
        /// </summary>
        public static readonly TimeSpan TimeoutWorstCaseTotal = TimeSpan.FromSeconds(242);

        /// <summary>
        /// Statuses worth another attempt: the proxy's "model busy" 429 and the standard transient 5xx
        /// set for a proxied inference call. Everything else - a success, a bad request, a revoked key
        /// (401, handled separately by key recovery), an empty wallet (402), a missing route (404) - is a
        /// final answer and is never retried.
        /// </summary>
        public static bool IsTransient(int status) =>
            status is 429 or 500 or 502 or 503 or 504;

        /// <summary>
        /// True when <paramref name="status"/> is this client's own expired-budget marker rather than
        /// anything the proxy said (issue #145).
        /// </summary>
        public static bool IsLocalTimeout(int status) => status == LocalTimeoutStatus;

        /// <summary>
        /// True when the call that just returned <paramref name="status"/> on attempt
        /// <paramref name="attempt"/> (1-based) should be sent again. The two retryable shapes have
        /// different attempt caps because they cost different amounts of wall clock: a transient status
        /// usually comes back fast, a hang always costs a whole attempt budget.
        /// </summary>
        /// <param name="callerCancellationRequested">
        /// True when the CALLER cancelled. A cancellation is a decision, not a transient failure - it is
        /// never replayed, whatever the status looks like (issue #145).
        /// </param>
        public static bool ShouldRetry(int status, int attempt, bool callerCancellationRequested = false)
        {
            if (callerCancellationRequested) return false;
            if (attempt < 1) return false;
            if (IsLocalTimeout(status)) return attempt < MaxTimeoutAttempts;
            return attempt < MaxAttempts && IsTransient(status);
        }

        /// <summary>
        /// How long to wait before the attempt that follows <paramref name="attempt"/> (1-based). A usable
        /// <c>Retry-After</c> is honored (clamped to <see cref="MaxDelay"/>) because the proxy knows more
        /// about its own load than we do; otherwise the wait doubles per attempt (2s, 4s, ...), also
        /// clamped. The planned no-header schedule therefore sums to 6 seconds across all
        /// <see cref="MaxAttempts"/> attempts.
        /// </summary>
        /// <param name="attempt">The 1-based number of the attempt that just failed.</param>
        /// <param name="retryAfter">The raw <c>Retry-After</c> header value, or null when absent.</param>
        public static TimeSpan DelayFor(int attempt, string? retryAfter = null)
        {
            if (attempt < 1) throw new ArgumentOutOfRangeException(nameof(attempt), "attempt is 1-based");

            if (TryParseRetryAfterSeconds(retryAfter, out var asked))
                return asked > MaxDelay ? MaxDelay : asked;

            var backoff = TimeSpan.FromSeconds(Math.Pow(2, attempt));
            return backoff > MaxDelay ? MaxDelay : backoff;
        }

        /// <summary>
        /// The sum of every planned delay when the proxy sends no <c>Retry-After</c>. Used by the tests to
        /// prove the retry tail cannot grow without bound.
        /// </summary>
        public static TimeSpan PlannedDelayTotal()
        {
            var total = TimeSpan.Zero;
            for (int attempt = 1; attempt < MaxAttempts; attempt++) total += DelayFor(attempt);
            return total;
        }

        /// <summary>
        /// The sum of every planned delay on the TIMEOUT path (2s, because that path stops at
        /// <see cref="MaxTimeoutAttempts"/>). Kept separate from <see cref="PlannedDelayTotal"/> so the
        /// worst-case arithmetic below is the arithmetic the loop actually performs.
        /// </summary>
        public static TimeSpan PlannedTimeoutDelayTotal()
        {
            var total = TimeSpan.Zero;
            for (int attempt = 1; attempt < MaxTimeoutAttempts; attempt++) total += DelayFor(attempt);
            return total;
        }

        /// <summary>
        /// The worst-case wall clock for a naming call whose every attempt hangs: the per-attempt budget
        /// spent in full, once per attempt, plus the planned backoff between them. With the shipped 120s
        /// budget this equals <see cref="TimeoutWorstCaseTotal"/>.
        /// </summary>
        public static TimeSpan TimeoutWorstCase(TimeSpan perAttempt) =>
            TimeSpan.FromTicks(perAttempt.Ticks * MaxTimeoutAttempts) + PlannedTimeoutDelayTotal();

        /// <summary>
        /// The ceiling on ONE whole chat call, whatever mix of transient statuses and hangs it hits: the
        /// larger of the status tail (<paramref name="perAttempt"/> + <see cref="RetryTailBudget"/> =
        /// 180s, issue #140) and the timeout worst case (242s, issue #145). The loop clamps every
        /// attempt to what is left of this, so the call cannot run past it.
        /// </summary>
        public static TimeSpan WorstCaseTotal(TimeSpan perAttempt)
        {
            var statusTail = perAttempt + RetryTailBudget;
            var timeoutTail = TimeoutWorstCase(perAttempt);
            return timeoutTail > statusTail ? timeoutTail : statusTail;
        }

        /// <summary>
        /// Parse a <c>Retry-After</c> value expressed in delta-seconds (the shape the DevThrottle proxy
        /// sends). Returns false for a null, blank, non-numeric or negative value - and also for an
        /// HTTP-date, which this client does not accept - so the caller uses its own backoff schedule.
        /// </summary>
        public static bool TryParseRetryAfterSeconds(string? retryAfter, out TimeSpan delay)
        {
            delay = TimeSpan.Zero;
            if (string.IsNullOrWhiteSpace(retryAfter)) return false;
            if (!int.TryParse(retryAfter.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds))
                return false;
            if (seconds < 0) return false;
            delay = TimeSpan.FromSeconds(seconds);
            return true;
        }

        /// <summary>
        /// The exact log line written before a retry. Lives here so a test can assert its shape without
        /// HTTP: <c>[DevThrottleClient] PostChatAsync: status=429, retrying (attempt 2/3) in 2s</c>.
        /// </summary>
        /// <param name="nextAttempt">The 1-based number of the attempt about to be made.</param>
        public static string RetryMessage(int status, int nextAttempt, TimeSpan delay) =>
            $"[DevThrottleClient] PostChatAsync: status={status}, " +
            $"retrying (attempt {nextAttempt}/{MaxAttempts}) in {delay.TotalSeconds:0}s";

        /// <summary>
        /// The exact log line written before a retry of a HUNG attempt. Deliberately different from
        /// <see cref="RetryMessage"/> - it reads <c>timeout,</c> where that one reads <c>status=NNN,</c> -
        /// so a stall retry is greppable in the log and never mistaken for a proxy answer (issue #145):
        /// <c>[DevThrottleClient] PostChatAsync: timeout, retrying (attempt 2/2) in 2s</c>.
        /// </summary>
        /// <param name="nextAttempt">The 1-based number of the attempt about to be made.</param>
        public static string TimeoutRetryMessage(int nextAttempt, TimeSpan delay) =>
            $"[DevThrottleClient] PostChatAsync: timeout, " +
            $"retrying (attempt {nextAttempt}/{MaxTimeoutAttempts}) in {delay.TotalSeconds:0}s";

        /// <summary>
        /// The caller-visible message on a chat call whose every attempt hung. Kept here, and unchanged
        /// word for word from the message the field log of 2026-08-11 shows, so the failure the callers
        /// already handle (a non-fatal title backfill error) keeps exactly the shape it had - only the
        /// number of attempts behind it changed (issue #145).
        /// </summary>
        public static string TimeoutMessage(TimeSpan perAttempt) =>
            $"DevThrottle did not answer the request within {perAttempt.TotalSeconds:0} seconds.";

        /// <summary>The log line written when the retry tail runs out of wall clock before the attempts do.</summary>
        public static string BudgetExhaustedMessage(int status, int attempt, TimeSpan elapsed) =>
            $"[DevThrottleClient] PostChatAsync: status={status}, giving up after attempt {attempt} - " +
            $"retry budget spent ({elapsed.TotalSeconds:0}s)";
    }
}
