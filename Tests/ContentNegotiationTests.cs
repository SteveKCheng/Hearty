using System;
using Microsoft.Extensions.Primitives;
using Xunit;
using JobBank.Server;

namespace JobBank.Tests
{
    public class ContentNegotiationTests
    {
        [Fact]
        public void SelectBestFormat()
        {
            var available = new ContentFormatInfo[]
            {
                new("application/messagepack", ContentPreference.Best),
                new("application/json", ContentPreference.Good),
                new("application/xml", ContentPreference.Bad)
            };

            var requests = new StringValues(new string[]
            {
                // Deliberate use some strange syntax here to check that
                // parsing is RFC-correct
                @"text/plain, application/messagepack; dummy=""5\"","", application/json ,",
                "application/xml",
                ", multipart/mixed, application/*"
            });

            int best = ContentFormatInfo.Negotiate(available, requests);
            Assert.Equal(1, best);
        }
    }
}
