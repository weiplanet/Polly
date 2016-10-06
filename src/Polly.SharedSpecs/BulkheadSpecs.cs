﻿using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Polly.Bulkhead;
using Polly.Specs.Helpers;

using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace Polly.Specs
{
    public class BulkheadSpecs : BulkheadSpecsHelper
    {
        public BulkheadSpecs(ITestOutputHelper testOutputHelper) : base(testOutputHelper) { }

        #region Configuration

        [Fact]
        public void Should_throw_when_maxparallelization_less_or_equal_to_zero()
        {
            Action policy = () => Policy
                .Bulkhead(0, 1);

            policy.ShouldThrow<ArgumentOutOfRangeException>().And
                .ParamName.Should().Be("maxParallelization");
        }

        [Fact]
        public void Should_throw_when_maxqueuedactions_less_than_zero()
        {
            Action policy = () => Policy
                .Bulkhead(1, -1);

            policy.ShouldThrow<ArgumentOutOfRangeException>().And
                .ParamName.Should().Be("maxQueuingActions");
        }

        [Fact]
        public void Should_throw_when_onBulkheadRejected_is_null()
        {
            Action policy = () => Policy
                .Bulkhead(1, 0, null);

            policy.ShouldThrow<ArgumentNullException>().And
                .ParamName.Should().Be("onBulkheadRejected");
        }

        #endregion

        #region onBulkheadRejected delegate

        [Fact]
        public void Should_call_onBulkheadRejected_with_passed_context()
        {
            string executionKey = Guid.NewGuid().ToString();
            Context contextPassedToExecute = new Context(executionKey);

            Context contextPassedToOnRejected = null;
            Action<Context> onRejected = ctx => { contextPassedToOnRejected = ctx; };

            BulkheadPolicy bulkhead = Policy.Bulkhead(1, onRejected);

            TaskCompletionSource<object> tcs = new TaskCompletionSource<object>();
            using (CancellationTokenSource cancellationSource = new CancellationTokenSource())
            {
                Task.Run(() => {
                    bulkhead.Execute(() =>
                    {
                        tcs.Task.Wait();
                    });
                });

                Within(shimTimeSpan, () => bulkhead.BulkheadAvailableCount.Should().Be(0)); // Time for the other thread to kick up and take the bulkhead.

                bulkhead.Invoking(b => b.Execute(() => { }, contextPassedToExecute)).ShouldThrow<BulkheadRejectedException>();

                cancellationSource.Cancel();
                tcs.SetCanceled();
            }

            contextPassedToOnRejected.Should().NotBeNull();
            contextPassedToOnRejected.ExecutionKey.Should().Be(executionKey);
            contextPassedToOnRejected.Should().BeSameAs(contextPassedToExecute);
        }

        #endregion

        #region Bulkhead behaviour

        [Theory, ClassData(typeof (BulkheadScenarios))]
        public void Should_control_executions_queuing_and_rejections_per_specification_with_cancellations(
            int maxParallelization, int maxQueuingActions, int totalActions, string because, bool cancelQueuing,
            bool cancelExecuting)
        {
            if (totalActions < 0) throw new ArgumentOutOfRangeException(nameof(totalActions));
            because = String.Format("MaxParallelization {0}; MaxQueuing {1}; TotalActions {2}; CancelQueuing {3}; CancelExecuting {4}: {5}", maxParallelization, maxQueuingActions, totalActions, cancelQueuing, cancelExecuting, because);

            BulkheadPolicy bulkhead = Policy.Bulkhead(maxParallelization, maxQueuingActions);

            // Set up delegates which we can track whether they've started; and control when we allow them to complete (to release their semaphore slot).
            actions = new TraceableAction[totalActions];
            for (int i = 0; i < totalActions; i++) { actions[i] = new TraceableAction(i, _statusChanged, testOutputHelper); }

            // Throw all the delegates at the bulkhead simultaneously.
            Task[] tasks = new Task[totalActions];
            for (int i = 0; i < totalActions; i++) { tasks[i] = actions[i].ExecuteOnBulkhead(bulkhead); }

            testOutputHelper.WriteLine("Immediately after queueing...");
            testOutputHelper.WriteLine("Bulkhead: {0} slots out of {1} available.", bulkhead.BulkheadAvailableCount, maxParallelization);
            testOutputHelper.WriteLine("Bulkhead queue: {0} slots out of {1} available.", bulkhead.QueueAvailableCount, maxQueuingActions);
            OutputActionStatuses();

            // Assert the expected distributions of executing, queuing, rejected and completed - when all delegates thrown at bulkhead.
            int expectedCompleted = 0;
            int expectedCancelled = 0;
            int expectedExecuting = Math.Min(totalActions, maxParallelization);
            int expectedRejects = Math.Max(0, totalActions - maxParallelization - maxQueuingActions);
            int expectedQueuing = Math.Min(maxQueuingActions, Math.Max(0, totalActions - maxParallelization));
            int expectedBulkheadFree = maxParallelization - expectedExecuting;
            int expectedQueueFree = maxQueuingActions - expectedQueuing;

            try
            {
                actions.Count(a => a.Status == TraceableActionStatus.Faulted).Should().Be(0);
                Within(shimTimeSpan, () => actions.Count(a => a.Status == TraceableActionStatus.Executing).Should().Be(expectedExecuting, because + ", when checking expectedExecuting"));
                Within(shimTimeSpan, () => actions.Count(a => a.Status == TraceableActionStatus.QueueingForSemaphore).Should().Be(expectedQueuing, because + ", when checking expectedQueuing"));
                Within(shimTimeSpan, () => actions.Count(a => a.Status == TraceableActionStatus.Rejected).Should().Be(expectedRejects, because + ", when checking expectedRejects"));
                actions.Count(a => a.Status == TraceableActionStatus.Completed).Should().Be(expectedCompleted, because + ", when checking expectedCompleted");
                actions.Count(a => a.Status == TraceableActionStatus.Canceled).Should().Be(expectedCancelled, because + ", when checking expectedCancelled");
                Within(shimTimeSpan, () => bulkhead.BulkheadAvailableCount.Should().Be(expectedBulkheadFree, because + ", when checking expectedBulkheadFree"));
                Within(shimTimeSpan, () => bulkhead.QueueAvailableCount.Should().Be(expectedQueueFree, because + ", when checking expectedQueueFree"));
            }
            finally
            {
                testOutputHelper.WriteLine("Expected initial state verified...");
                testOutputHelper.WriteLine("Bulkhead: {0} slots out of {1} available.", bulkhead.BulkheadAvailableCount, maxParallelization);
                testOutputHelper.WriteLine("Bulkhead queue: {0} slots out of {1} available.", bulkhead.QueueAvailableCount, maxQueuingActions);
                OutputActionStatuses();
            }

            // Complete or cancel delegates one by one, and expect others to take their place (if a slot released and others remain queueing); until all work is done.
            while (expectedExecuting > 0)
            {
                if (cancelQueuing)
                {
                    testOutputHelper.WriteLine("Cancelling a queueing task...");

                    actions.First(a => a.Status == TraceableActionStatus.QueueingForSemaphore).Cancel();

                    expectedCancelled++;
                    expectedQueuing--;
                    expectedQueueFree++;

                    cancelQueuing = false;
                }
                else if (cancelExecuting)
                {
                    testOutputHelper.WriteLine("Cancelling an executing task...");

                    actions.First(a => a.Status == TraceableActionStatus.Executing).Cancel();

                    expectedCancelled++;
                    if (expectedQueuing > 0)
                    {
                        expectedQueuing--;
                        expectedQueueFree++;
                    }
                    else
                    {
                        expectedExecuting--;
                        expectedBulkheadFree++;
                    }

                    cancelExecuting = false;
                }
                else // Complete an executing delegate.
                {
                    testOutputHelper.WriteLine("Completing a task...");

                    actions.First(a => a.Status == TraceableActionStatus.Executing).AllowCompletion();

                    expectedCompleted++;

                    if (expectedQueuing > 0)
                    {
                        expectedQueuing--;
                        expectedQueueFree++;
                    }
                    else
                    {
                        expectedExecuting--;
                        expectedBulkheadFree++;
                    }

                }
                try
                {
                    actions.Count(a => a.Status == TraceableActionStatus.Faulted).Should().Be(0);
                    Within(shimTimeSpan, () => actions.Count(a => a.Status == TraceableActionStatus.Executing).Should().Be(expectedExecuting, because + ", when checking expectedExecuting"));
                    Within(shimTimeSpan, () => actions.Count(a => a.Status == TraceableActionStatus.QueueingForSemaphore).Should().Be(expectedQueuing, because + ", when checking expectedQueuing"));
                    Within(shimTimeSpan, () => actions.Count(a => a.Status == TraceableActionStatus.Completed).Should().Be(expectedCompleted, because + ", when checking expectedCompleted"));
                    Within(shimTimeSpan, () => actions.Count(a => a.Status == TraceableActionStatus.Canceled).Should().Be(expectedCancelled, because + ", when checking expectedCancelled"));
                    actions.Count(a => a.Status == TraceableActionStatus.Rejected).Should().Be(expectedRejects, because + ", when checking expectedRejects");
                    Within(shimTimeSpan, () => bulkhead.BulkheadAvailableCount.Should().Be(expectedBulkheadFree, because + ", when checking expectedBulkheadFree"));
                    Within(shimTimeSpan, () => bulkhead.QueueAvailableCount.Should().Be(expectedQueueFree, because + ", when checking expectedQueueFree"));
                }
                finally
                {
                    testOutputHelper.WriteLine("End of next loop iteration...");
                    testOutputHelper.WriteLine("Bulkhead: {0} slots out of {1} available.", bulkhead.BulkheadAvailableCount, maxParallelization);
                    testOutputHelper.WriteLine("Bulkhead queue: {0} slots out of {1} available.", bulkhead.QueueAvailableCount, maxQueuingActions);
                    OutputActionStatuses();
                }

            }

            EnsureNoUnbservedTaskExceptions(tasks); 
            testOutputHelper.WriteLine("Verifying all tasks completed...");
            Within(shimTimeSpan, () => tasks.All(t => t.IsCompleted).Should().BeTrue());

        }

        #endregion

    }
}