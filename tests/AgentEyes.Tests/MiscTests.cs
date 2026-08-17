using System;
using System.Linq;
using Xunit;
using AgentEyes;
using AgentEyes.Packaging;
using Whisper.net.Ggml;

namespace AgentEyes.Tests
{
    public class ModelStoreTests
    {
        [Fact]
        public void PathFor_is_under_localappdata_models()
        {
            string p = ModelStore.PathFor(GgmlType.Base);
            Assert.Contains("AgentEyes", p);
            Assert.Contains("models", p);
            Assert.EndsWith("ggml-base.bin", p.ToLowerInvariant());
        }

        [Fact]
        public void PathFor_varies_by_type()
        {
            Assert.NotEqual(ModelStore.PathFor(GgmlType.Base), ModelStore.PathFor(GgmlType.Tiny));
        }
    }

    public class MonitorsTests
    {
        // These run on the interactive machine where at least one display exists.
        [Fact]
        public void All_returns_at_least_one_monitor()
        {
            var all = Monitors.All();
            Assert.NotEmpty(all);
        }

        [Fact]
        public void Indices_are_one_based_and_sequential()
        {
            var all = Monitors.All();
            for (int i = 0; i < all.Count; i++)
            {
                Assert.Equal(i + 1, all[i].Index);
            }
        }

        [Fact]
        public void Exactly_one_primary_monitor()
        {
            Assert.Equal(1, Monitors.All().Count(m => m.Primary));
        }

        [Fact]
        public void Require_out_of_range_throws()
        {
            Assert.Throws<UsageException>(() => Monitors.Require(999));
            Assert.Throws<UsageException>(() => Monitors.Require(0));
        }

        [Fact]
        public void Require_valid_index_returns_monitor()
        {
            var m = Monitors.Require(1);
            Assert.Equal(1, m.Index);
            Assert.True(m.Width > 0 && m.Height > 0);
        }
    }
}
