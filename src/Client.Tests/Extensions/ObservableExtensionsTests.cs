using System;
using System.Collections.Generic;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using Client.App.Extensions;
using FluentAssertions;
using Microsoft.Reactive.Testing;
using Xunit;

namespace Client.Tests.Extensions
{
    public class ObservableExtensionsTests
    {
        [Fact]
        public void ShouldSplitWithNewlines()
        {
            var output = new List<string>();
            var autoResetEvent = new AutoResetEvent(false);

            var testScheduler = new TestScheduler();

            testScheduler.CreateColdObservable(
                    new Recorded<Notification<string>>(1, Notification.CreateOnNext("Hello")),
                    new Recorded<Notification<string>>(2, Notification.CreateOnNext("Hello\r\nWorl")),
                    new Recorded<Notification<string>>(3, Notification.CreateOnNext("Hello\r\nWorld\r\n")),
                    new Recorded<Notification<string>>(4, Notification.CreateOnNext("Hello\r\nWorld\r\n!")),
                    new Recorded<Notification<string>>(5, Notification.CreateOnCompleted<string>())
                ).SplitRepeatedPrefixByNewline()
                .SelectMany(strings => strings)
                .Subscribe(s => output.Add(s), () => autoResetEvent.Set());

            testScheduler.AdvanceBy(6);

            autoResetEvent.WaitOne(100).Should().BeTrue();
            output.Should().BeEquivalentTo("Hello", "World", "!");
        }

        [Fact]
        public void ShouldSplitRepeatedWithMixedNewlines()
        {
            var output = new List<string>();
            var autoResetEvent = new AutoResetEvent(false);

            var testScheduler = new TestScheduler();
            
            testScheduler.CreateColdObservable(
                new Recorded<Notification<string>>(1, Notification.CreateOnNext("Hello")),
                new Recorded<Notification<string>>(2, Notification.CreateOnNext("Hello\nWorl")),
                new Recorded<Notification<string>>(3, Notification.CreateOnNext("Hello\nWorld\r\n")),
                new Recorded<Notification<string>>(4, Notification.CreateOnNext("Hello\nWorld\r\n!")),
                new Recorded<Notification<string>>(5, Notification.CreateOnCompleted<string>())
            ).SplitRepeatedPrefixByNewline()
                .SelectMany(strings => strings)
                .Subscribe(s => output.Add(s), () => autoResetEvent.Set());

            testScheduler.AdvanceBy(6);

            autoResetEvent.WaitOne(100).Should().BeTrue();
            output.Should().BeEquivalentTo("Hello", "World", "!");
        }

        [Fact]
        public void ShouldSplitTuple()
        {
            var autoResetEvent = new AutoResetEvent(false);

            var testScheduler = new TestScheduler();

            var testableObservable = testScheduler.CreateColdObservable(
                new Recorded<Notification<(string, int)>>(1, Notification.CreateOnNext(("Hello", 1))),
                new Recorded<Notification<(string, int)>>(2, Notification.CreateOnNext(("World", 2))),
                new Recorded<Notification<(string, int)>>(3, Notification.CreateOnNext(("!", 3)))
            );

            var (stringsObservable, numbersObservable) = testableObservable
                .SplitTuple();

            stringsObservable.Subscribe(s =>
            {

            }, () =>
            {
                autoResetEvent.Set();
            });

            numbersObservable.Subscribe(s =>
            {

            }, () =>
            {
                autoResetEvent.Set();
            });

            testScheduler.AdvanceBy(6);

            autoResetEvent.WaitOne().Should().BeTrue();
            autoResetEvent.WaitOne().Should().BeTrue();
        }

        [Fact]
        public void ShouldSplitStreamByNewlines()
        {
            (string[] output, int last, string remainder) result = default;
            var autoResetEvent = new AutoResetEvent(false);

            var input = "Hello\nWorld\r\n!";
            
            input
                .ToObservable()
                .SplitStreamByNewlines(2)
            .Subscribe(s => result = s, () => autoResetEvent.Set());

            autoResetEvent.WaitOne().Should().BeTrue();
            result.output.Should().BeEquivalentTo("llo", "World");
            result.last.Should().Be(input.Length-1);
            result.remainder.Should().Be("!");
        }
    }
}
