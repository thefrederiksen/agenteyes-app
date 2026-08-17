using System;
using AgentEyes.DevThrottle;
using Xunit;

namespace AgentEyes.Tests
{
    /// <summary>
    /// Issue #140: a 36-minute recording finished with a full transcript but kept its preset name
    /// because the single naming call answered 429 "Model busy, retry later" and was abandoned. The
    /// retry decision lives in <see cref="ChatRetryPolicy"/> precisely so it is provable here with no
    /// network call - bounded attempts, bounded delays, and a bounded total.
    /// </summary>
    public class ChatRetryPolicyTests
    {
        // --- Criterion 1: which statuses are worth another attempt -------------------------------

        [Theory]
        [InlineData(429)]   // the observed failure: "Model busy, retry later"
        [InlineData(500)]
        [InlineData(502)]
        [InlineData(503)]
        [InlineData(504)]
        public void IsTransient_TransientStatus_ReturnsTrue(int status)
        {
            Assert.True(ChatRetryPolicy.IsTransient(status));
            Assert.True(ChatRetryPolicy.ShouldRetry(status, attempt: 1));
        }

        [Theory]
        [InlineData(200)]   // success
        [InlineData(400)]   // bad request - replaying it changes nothing
        [InlineData(401)]   // revoked key - key recovery owns this path, not the retry loop
        [InlineData(402)]   // out of credits
        [InlineData(404)]
        public void IsTransient_FinalStatus_ReturnsFalse(int status)
        {
            Assert.False(ChatRetryPolicy.IsTransient(status));
            Assert.False(ChatRetryPolicy.ShouldRetry(status, attempt: 1));
        }

        // --- Criterion 2: the attempt count and the delay total are bounded ----------------------

        [Fact]
        public void MaxAttempts_IsBoundedBetweenTwoAndFour()
        {
            Assert.InRange(ChatRetryPolicy.MaxAttempts, 2, 4);
        }

        [Fact]
        public void ShouldRetry_StopsAtTheLastAttempt()
        {
            // A transient status is still retryable up to the attempt before the last...
            Assert.True(ChatRetryPolicy.ShouldRetry(429, ChatRetryPolicy.MaxAttempts - 1));
            // ...and never on or after the last, however transient the answer looks.
            Assert.False(ChatRetryPolicy.ShouldRetry(429, ChatRetryPolicy.MaxAttempts));
            Assert.False(ChatRetryPolicy.ShouldRetry(429, ChatRetryPolicy.MaxAttempts + 1));
        }

        [Fact]
        public void PlannedDelayTotal_WithoutRetryAfter_StaysUnderThirtySeconds()
        {
            // The packaging pass is waiting on this call - the tail must not become the new stall.
            Assert.True(ChatRetryPolicy.PlannedDelayTotal() <= TimeSpan.FromSeconds(30),
                $"planned delays total {ChatRetryPolicy.PlannedDelayTotal().TotalSeconds}s");
        }

        [Fact]
        public void DelayFor_WithoutRetryAfter_BacksOffAndIsClamped()
        {
            Assert.Equal(TimeSpan.FromSeconds(2), ChatRetryPolicy.DelayFor(1));
            Assert.Equal(TimeSpan.FromSeconds(4), ChatRetryPolicy.DelayFor(2));
            // Even a far higher attempt number cannot produce an unbounded wait.
            Assert.Equal(ChatRetryPolicy.MaxDelay, ChatRetryPolicy.DelayFor(20));
        }

        [Fact]
        public void DelayFor_AttemptBelowOne_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => ChatRetryPolicy.DelayFor(0));
        }

        [Fact]
        public void RetryTailBudget_KeepsTheWholeCallBelowTheStallIssue138Removed()
        {
            // #138: one stalled titling call held a packaging pass for 300 seconds. The retry tail
            // may extend a single attempt's budget, but the total must stay clearly under that.
            var worstCase = DevThrottleClient.ChatTimeout + ChatRetryPolicy.RetryTailBudget;
            Assert.True(worstCase < TimeSpan.FromSeconds(300),
                $"worst-case chat call is {worstCase.TotalSeconds}s");
        }

        // --- Criterion 3: Retry-After is honored, and clamped -------------------------------------

        [Fact]
        public void DelayFor_RetryAfterFive_YieldsFiveSeconds()
        {
            Assert.Equal(TimeSpan.FromSeconds(5), ChatRetryPolicy.DelayFor(1, "5"));
        }

        [Fact]
        public void DelayFor_AbsurdRetryAfter_IsClampedToTheMaximum()
        {
            Assert.Equal(ChatRetryPolicy.MaxDelay, ChatRetryPolicy.DelayFor(1, "600"));
        }

        [Fact]
        public void MaxDelay_LeavesRoomForARealisticRetryAfter()
        {
            // The clamp exists to stop a ten-minute ask, not to ignore a five-second one.
            Assert.True(ChatRetryPolicy.MaxDelay >= TimeSpan.FromSeconds(5));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("soon")]
        [InlineData("Wed, 21 Oct 2026 07:28:00 GMT")]   // HTTP-date shape: not accepted
        [InlineData("-3")]
        public void TryParseRetryAfterSeconds_Unusable_ReturnsFalse(string? header)
        {
            Assert.False(ChatRetryPolicy.TryParseRetryAfterSeconds(header, out _));
            // ...and the caller therefore falls back to its own schedule, not to zero.
            Assert.Equal(TimeSpan.FromSeconds(2), ChatRetryPolicy.DelayFor(1, header));
        }

        [Fact]
        public void TryParseRetryAfterSeconds_DeltaSeconds_ReturnsThatDelay()
        {
            Assert.True(ChatRetryPolicy.TryParseRetryAfterSeconds(" 7 ", out var delay));
            Assert.Equal(TimeSpan.FromSeconds(7), delay);
        }

        [Fact]
        public void TryParseRetryAfterSeconds_Zero_IsUsableAndMeansNoWait()
        {
            Assert.True(ChatRetryPolicy.TryParseRetryAfterSeconds("0", out var delay));
            Assert.Equal(TimeSpan.Zero, delay);
        }

        // --- Criterion 5: the retry log line names the status, the attempt, and the delay ---------

        [Fact]
        public void RetryMessage_HasTheDocumentedShape()
        {
            Assert.Equal(
                "[DevThrottleClient] PostChatAsync: status=429, retrying (attempt 2/3) in 2s",
                ChatRetryPolicy.RetryMessage(429, nextAttempt: 2, delay: ChatRetryPolicy.DelayFor(1)));
        }

        [Fact]
        public void BudgetExhaustedMessage_NamesTheStatusAndTheAttempt()
        {
            string msg = ChatRetryPolicy.BudgetExhaustedMessage(503, attempt: 2, elapsed: TimeSpan.FromSeconds(178));
            Assert.Contains("status=503", msg);
            Assert.Contains("attempt 2", msg);
            Assert.Contains("178s", msg);
        }

        // =========================================================================================
        // Issue #145: the transient that #140 could not see. The common field failure is not a status
        // at all - the upstream model hangs, the per-attempt budget expires, and a status-driven policy
        // never gets a status to judge. Three naming calls died this way in one hour on the #140 build.
        // =========================================================================================

        // --- Criterion 1: a chat timeout is retryable; a caller cancellation is not ---------------

        [Fact]
        public void IsLocalTimeout_TheClientsOwnExpiredBudget_ReturnsTrue()
        {
            Assert.Equal(408, ChatRetryPolicy.LocalTimeoutStatus);
            Assert.True(ChatRetryPolicy.IsLocalTimeout(ChatRetryPolicy.LocalTimeoutStatus));
            Assert.False(ChatRetryPolicy.IsLocalTimeout(429));
            Assert.False(ChatRetryPolicy.IsLocalTimeout(200));
        }

        [Fact]
        public void ShouldRetry_ChatTimeout_IsRetryable()
        {
            // The exact escape #145 fixes: SendChatOnceAsync reports its expired budget as 408 and the
            // call is sent again, on the same bounded policy that already judges 429/5xx.
            Assert.True(ChatRetryPolicy.ShouldRetry(ChatRetryPolicy.LocalTimeoutStatus, attempt: 1));
        }

        [Fact]
        public void ShouldRetry_ChatTimeout_StaysOutOfTheProxyTransientSet()
        {
            // #140 settled which PROXY statuses are retried, and 408 is not one of them - it is this
            // client's local marker, judged on its own tighter attempt cap.
            Assert.False(ChatRetryPolicy.IsTransient(ChatRetryPolicy.LocalTimeoutStatus));
        }

        [Fact]
        public void ShouldRetry_CallerCancelled_IsNeverRetried()
        {
            // A cancellation is a decision, not a stall. Neither a timeout nor a transient status is
            // replayed once the caller has asked to stop.
            Assert.False(ChatRetryPolicy.ShouldRetry(
                ChatRetryPolicy.LocalTimeoutStatus, attempt: 1, callerCancellationRequested: true));
            Assert.False(ChatRetryPolicy.ShouldRetry(429, attempt: 1, callerCancellationRequested: true));
        }

        // --- Criterion 2: the timeout path is bounded to 2..3 total attempts ----------------------

        [Fact]
        public void MaxTimeoutAttempts_IsBoundedBetweenTwoAndThree()
        {
            // At least 2 - one retry is the whole point. At most 3 - each attempt costs a FULL budget.
            Assert.InRange(ChatRetryPolicy.MaxTimeoutAttempts, 2, 3);
        }

        [Fact]
        public void ShouldRetry_ChatTimeout_StopsAtTheLastTimeoutAttempt()
        {
            Assert.True(ChatRetryPolicy.ShouldRetry(
                ChatRetryPolicy.LocalTimeoutStatus, ChatRetryPolicy.MaxTimeoutAttempts - 1));
            Assert.False(ChatRetryPolicy.ShouldRetry(
                ChatRetryPolicy.LocalTimeoutStatus, ChatRetryPolicy.MaxTimeoutAttempts));
            Assert.False(ChatRetryPolicy.ShouldRetry(
                ChatRetryPolicy.LocalTimeoutStatus, ChatRetryPolicy.MaxTimeoutAttempts + 1));
        }

        [Fact]
        public void ShouldRetry_ChatTimeout_IsCappedTighterThanAStatusRetry()
        {
            // A hung attempt burns the whole per-attempt budget; a 429 usually comes back at once. The
            // caps differ deliberately, and this is the assertion that says so.
            Assert.True(ChatRetryPolicy.MaxTimeoutAttempts <= ChatRetryPolicy.MaxAttempts);
        }

        // --- Criterion 3: the worst-case wall clock is named, asserted, and under the #138 stall ---

        [Fact]
        public void TimeoutWorstCase_MatchesTheNamedConstant()
        {
            // The documented arithmetic: 2 attempts x 120s + the 2s planned backoff = 242s. Asserting
            // the computed figure against the named constant means a change to MaxTimeoutAttempts, to
            // the backoff schedule, or to ChatTimeout fails HERE instead of silently moving the ceiling.
            Assert.Equal(
                ChatRetryPolicy.TimeoutWorstCaseTotal,
                ChatRetryPolicy.TimeoutWorstCase(DevThrottleClient.ChatTimeout));
            Assert.Equal(TimeSpan.FromSeconds(242), ChatRetryPolicy.TimeoutWorstCaseTotal);
            Assert.Equal(TimeSpan.FromSeconds(2), ChatRetryPolicy.PlannedTimeoutDelayTotal());
        }

        [Fact]
        public void TimeoutWorstCaseTotal_StaysBelowTheStallIssue138Removed()
        {
            // #138: one stalled titling call held a packaging pass for 300 seconds. A fully-retried
            // hang must still land under that - which is exactly why the timeout path stops at 2.
            Assert.True(ChatRetryPolicy.TimeoutWorstCaseTotal < TimeSpan.FromSeconds(300),
                $"fully-retried timeout is {ChatRetryPolicy.TimeoutWorstCaseTotal.TotalSeconds}s");
        }

        [Fact]
        public void WorstCaseTotal_IsTheCeilingOnAnyMixOfTimeoutsAndStatuses()
        {
            var total = ChatRetryPolicy.WorstCaseTotal(DevThrottleClient.ChatTimeout);
            // It covers BOTH tails: the #140 status tail (120s + 60s) and the #145 timeout worst case.
            Assert.True(total >= DevThrottleClient.ChatTimeout + ChatRetryPolicy.RetryTailBudget);
            Assert.True(total >= ChatRetryPolicy.TimeoutWorstCaseTotal);
            Assert.Equal(ChatRetryPolicy.TimeoutWorstCaseTotal, total);
            // ...and the ceiling itself is still under the stall that started this whole line of work.
            Assert.True(total < TimeSpan.FromSeconds(300), $"whole-call ceiling is {total.TotalSeconds}s");
        }

        [Fact]
        public void WorstCaseTotal_LeavesEveryRetriedAttemptItsFullBudget()
        {
            // The trap this constant exists to avoid: a total so small that the second attempt is
            // truncated to the leftover. A legitimate naming call was MEASURED at 66.7s, so a truncated
            // retry would kill calls that were about to succeed - the mistake #138 documented.
            var perAttempt = DevThrottleClient.ChatTimeout;
            var afterFirstAttemptAndBackoff =
                ChatRetryPolicy.WorstCaseTotal(perAttempt) - perAttempt - ChatRetryPolicy.DelayFor(1);
            Assert.True(afterFirstAttemptAndBackoff >= perAttempt,
                $"second attempt would be truncated to {afterFirstAttemptAndBackoff.TotalSeconds}s");
        }

        // --- Criterion 5: the exhausted timeout still reaches the caller in the shape it does today -

        [Fact]
        public void TimeoutFailure_KeepsTheTypedShapeTheCallerAlreadyHandles()
        {
            // Verbatim from the field log of 2026-08-11 that produced this issue. The title backfill
            // catches this, logs the recording directory, and moves on - behavior that must not change
            // just because more attempts now sit behind the failure.
            var ex = new DevThrottleException(
                ChatRetryPolicy.TimeoutMessage(DevThrottleClient.ChatTimeout),
                ChatRetryPolicy.LocalTimeoutStatus);
            Assert.Equal(408, ex.Status);
            Assert.Equal("DevThrottle did not answer the request within 120 seconds.", ex.Message);
        }

        // --- Criterion 4: the timeout retry log line is distinguishable from the status one --------

        [Fact]
        public void TimeoutRetryMessage_NamesTheAttemptAndTheDelay()
        {
            string msg = ChatRetryPolicy.TimeoutRetryMessage(nextAttempt: 2, delay: ChatRetryPolicy.DelayFor(1));
            Assert.Equal(
                $"[DevThrottleClient] PostChatAsync: timeout, retrying (attempt 2/{ChatRetryPolicy.MaxTimeoutAttempts}) in 2s",
                msg);
        }

        [Fact]
        public void TimeoutRetryMessage_IsNotMistakenForTheStatusRetryLine()
        {
            string timeoutLine = ChatRetryPolicy.TimeoutRetryMessage(2, TimeSpan.FromSeconds(2));
            string statusLine = ChatRetryPolicy.RetryMessage(429, 2, TimeSpan.FromSeconds(2));
            Assert.Contains("timeout,", timeoutLine);
            Assert.DoesNotContain("status=", timeoutLine);
            Assert.DoesNotContain("timeout,", statusLine);
            Assert.NotEqual(statusLine, timeoutLine);
        }
    }
}
