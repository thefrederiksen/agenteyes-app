using System;
using System.Collections.Generic;
using Xunit;
using AgentEyes;

namespace AgentEyes.Tests
{
    public class CliArgsTests
    {
        [Fact]
        public void Parses_flag_with_value()
        {
            var a = CliArgs.Parse(new[] { "--screen", "2" });
            Assert.True(a.Has("screen"));
            Assert.Equal("2", a.Get("screen"));
            Assert.Equal(2, a.RequireInt("screen", "hint"));
        }

        [Fact]
        public void Parses_boolean_flag_with_no_value()
        {
            var a = CliArgs.Parse(new[] { "--region" });
            Assert.True(a.Has("region"));
            Assert.Null(a.Get("region"));
        }

        [Fact]
        public void Boolean_flag_followed_by_another_flag_stays_boolean()
        {
            var a = CliArgs.Parse(new[] { "--region", "--screen", "1" });
            Assert.True(a.Has("region"));
            Assert.Null(a.Get("region"));
            Assert.Equal("1", a.Get("screen"));
        }

        [Fact]
        public void Collects_positional_arguments()
        {
            var a = CliArgs.Parse(new[] { "some/dir", "--label", "x" });
            Assert.Single(a.Positional);
            Assert.Equal("some/dir", a.Positional[0]);
            Assert.Equal("x", a.Get("label"));
        }

        [Fact]
        public void Quoted_value_with_spaces_is_one_token()
        {
            // The shell delivers a single token; we model that directly.
            var a = CliArgs.Parse(new[] { "--mic", "Microphone (Yeti Stereo)" });
            Assert.Equal("Microphone (Yeti Stereo)", a.Get("mic"));
        }

        [Fact]
        public void Require_missing_throws_usage()
        {
            var a = CliArgs.Parse(Array.Empty<string>());
            Assert.Throws<UsageException>(() => a.Require("mic", "hint"));
        }

        [Fact]
        public void RequireInt_non_numeric_throws_usage()
        {
            var a = CliArgs.Parse(new[] { "--screen", "abc" });
            Assert.Throws<UsageException>(() => a.RequireInt("screen", "hint"));
        }

        [Fact]
        public void Flag_names_are_case_insensitive()
        {
            var a = CliArgs.Parse(new[] { "--Screen", "3" });
            Assert.True(a.Has("screen"));
            Assert.Equal("3", a.Get("SCREEN"));
        }
    }
}
