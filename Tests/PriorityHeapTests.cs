using JobBank.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace JobBank.Tests
{
    public class PriorityHeapTests
    {
        [Fact]
        public void SortWithPriorityHeap()
        {
            var heap = new IntPriorityHeap<int>(null);

            int minValue = int.MinValue;
            int maxValue = int.MaxValue;

            var random = new Random(109);
            int count = 100000;
            for (int i = 0; i < count; ++i)
            {
                var key = random.Next(minValue, maxValue);
                heap.Insert(key, key);
            }

            Assert.Equal(count, heap.Count);

            int maxKey = maxValue;
            for (int i = 0; i < count; ++i)
            {
                var item = heap.TakeMaximum();
                Assert.Equal(item.Key, item.Value);
                Assert.InRange(item.Key, minValue, maxKey);
                maxKey = item.Key;
            }

            Assert.True(heap.IsEmpty);
        }
    }
}
